#if WINDOWS
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Maui.Handlers;
using System.Collections.Immutable;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace TripleG3.Camera.Maui;

public sealed class CameraViewHandler : ViewHandler<CameraView, CanvasControl>, ICameraViewHandler
{
    public CameraViewHandler() : base(Mapper)
    {
        cameraManager = CameraManager.Create(FrameReceived);
    }

    private readonly CanvasControl canvasControl = new();
    private IDirect3DSurface? latestSurface;
    private readonly Lock surfaceLock = new();
    private DrawMode drawMode;

    // Fallback (when BGRA8 surface not provided)
    private byte[] pixelBuffer = [];
    private int fbWidth;
    private int fbHeight;
    private readonly CameraManager cameraManager;

    public static IPropertyMapper<CameraView, CameraViewHandler> Mapper = new PropertyMapper<CameraView, CameraViewHandler>(ViewMapper)
    {
        [nameof(CameraView.SelectedCamera)] = MapCameraInfo,
        [nameof(CameraView.Height)] = MapHeight,
        [nameof(CameraView.Width)] = MapWidth
    };

    private DrawMode DrawMode
    {
        get => drawMode;
        set 
        { 
            if (drawMode == value) return;
            drawMode = value;
            if (canvasControl == null) return;
            switch (drawMode)
            {
                case DrawMode.None:
                    canvasControl.Draw -= CanvasDrawDirect3dSurface;
                    canvasControl.Draw -= CanvasDrawFallback;
                    break;
                case DrawMode.Direct3DSurface:
                    canvasControl.Draw -= CanvasDrawFallback;
                    canvasControl.Draw += CanvasDrawDirect3dSurface;
                    break;
                case DrawMode.Fallback:
                    canvasControl.Draw -= CanvasDrawDirect3dSurface;
                    canvasControl.Draw += CanvasDrawFallback;
                    break;
            }
        }
    }

    private static void MapCameraInfo(CameraViewHandler handler, CameraView view) => handler.VirtualView?.CameraViewHandler?.OnCameraInfoChanged(view.SelectedCamera);

    private static void MapHeight(CameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.CameraViewHandler?.OnHeightChanged(view.Height);

    private static void MapWidth(CameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.CameraViewHandler?.OnWidthChanged(view.Height);

    protected override CanvasControl CreatePlatformView()
    {
        canvasControl.Draw += CanvasDrawDirect3dSurface;
        canvasControl.Loaded += (_, _) => canvasControl.Invalidate();
        if (canvasControl.IsLoaded)
            canvasControl.Invalidate();
        VirtualView.CameraViewHandler = this;
        return canvasControl;
    }

    public bool IsStreaming => cameraManager.IsStreaming;

    public ImmutableList<CameraInfo> CameraInfos => cameraManager.CameraInfos;

    public async void OnCameraInfoChanged(CameraInfo cameraInfo) => await cameraManager.SelectCameraAsync(cameraInfo);
    public async Task StartAsync(CancellationToken cancellationToken) => await cameraManager.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await cameraManager.StopAsync(cancellationToken);
        canvasControl.Draw -= CanvasDrawDirect3dSurface;
        canvasControl.Draw -= CanvasDrawFallback;
        await MainThread.InvokeOnMainThreadAsync(canvasControl.Invalidate);
    }

    private async void FrameReceived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var vmf = frame?.VideoMediaFrame;
        if (vmf == null) return;

        switch (DrawMode)
        {
            case DrawMode.None:
                DrawMode = vmf.Direct3DSurface != null && vmf.Direct3DSurface.Description.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized
                     ? DrawMode.Direct3DSurface
                     : DrawMode.Fallback;
                break;
            case DrawMode.Direct3DSurface:
                var surface = vmf.Direct3DSurface;
                lock (surfaceLock)
                    latestSurface = surface;
                break;
            case DrawMode.Fallback:
                // Fallback path: get SoftwareBitmap in BGRA8
                SoftwareBitmap? sb = null;
                try
                {
                    // Try existing software bitmap first
                    sb = vmf.SoftwareBitmap;

                    if (sb == null && vmf.Direct3DSurface != null)
                    {
                        // IMPORTANT: use Ignore, not Pre-multiplied, for formats without alpha (e.g. NV12)
                        sb = await SoftwareBitmap
                            .CreateCopyFromSurfaceAsync(vmf.Direct3DSurface, BitmapAlphaMode.Ignore)
                            .AsTask()
                            .ConfigureAwait(false);
                    }

                    if (sb == null) return;

                    if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                    {
                        var converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                        if (!ReferenceEquals(sb, converted))
                            sb.Dispose();
                        sb = converted;
                    }

                    int w = sb.PixelWidth;
                    int h = sb.PixelHeight;
                    int needed = 4 * w * h;
                    if (pixelBuffer.Length != needed)
                        pixelBuffer = new byte[needed];

                    sb.CopyToBuffer(pixelBuffer.AsBuffer());

                    lock (surfaceLock)
                    {
                        fbWidth = w;
                        fbHeight = h;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Fallback conversion error: 0x{ex.HResult:X8} {ex.Message}");
                }
                finally
                {
                    sb?.Dispose();
                }
                break;
            default:
                break;
        }

        // Marshal invalidate to UI thread
        _ = MainThread.InvokeOnMainThreadAsync(canvasControl.Invalidate);
    }

    private void CanvasDrawDirect3dSurface(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            IDirect3DSurface? surface;
            lock (surfaceLock) surface = latestSurface;
            using var bmp = CanvasBitmap.CreateFromDirect3D11Surface(sender.Device, surface);
            DrawScaled(sender, args.DrawingSession, bmp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Canvas_Draw exception HRESULT=0x" + ex.HResult.ToString("X8") + " msg=" + ex.Message);
        }
    }

    private void CanvasDrawFallback(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            int w, h;
            byte[] local;
            lock (surfaceLock)
            {
                w = fbWidth;
                h = fbHeight;
                if (w == 0 || h == 0) { System.Diagnostics.Debug.WriteLine("Canvas_Draw: fallback no data"); return; }
                local = pixelBuffer;
            }
            using var bmp = CanvasBitmap.CreateFromBytes(sender, local, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            DrawScaled(sender, args.DrawingSession, bmp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Canvas_Draw exception HRESULT=0x" + ex.HResult.ToString("X8") + " msg=" + ex.Message);
        }
    }

    static void DrawScaled(CanvasControl sender, CanvasDrawingSession ds, CanvasBitmap bmp)
    {
        var scale = Math.Min(
            sender.ActualWidth / bmp.SizeInPixels.Width,
            sender.ActualHeight / bmp.SizeInPixels.Height);
        var drawW = bmp.SizeInPixels.Width * scale;
        var drawH = bmp.SizeInPixels.Height * scale;
        var x = (sender.ActualWidth - drawW) / 2;
        var y = (sender.ActualHeight - drawH) / 2;
        ds.DrawImage(bmp, new System.Numerics.Vector2((float)x, (float)y));
    }

    protected override async void DisconnectHandler(CanvasControl platformView)
    {
        await cameraManager.CleanupAsync();
        if (canvasControl != null) canvasControl.Draw -= CanvasDrawDirect3dSurface;
        base.DisconnectHandler(platformView);
    }

    public void OnHeightChanged(double height)
    {
        if (canvasControl == null || height < 1)
            return;

        canvasControl.Height = height;
    }

    public void OnWidthChanged(double width)
    {
        if (canvasControl == null || width < 1)
            return;

        canvasControl.Width = width;
    }

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default) => await cameraManager.LoadAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        lock (surfaceLock)
        {
            latestSurface = null;
            fbWidth = fbHeight = 0;
        }
        await cameraManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

internal enum DrawMode
{
    None,
    Direct3DSurface,
    Fallback
}
#endif
