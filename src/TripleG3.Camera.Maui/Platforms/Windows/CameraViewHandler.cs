#if WINDOWS
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Maui.Handlers;
using System.Collections.Immutable;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media.Capture.Frames;

namespace TripleG3.Camera.Maui;

public sealed class CameraViewHandler : ViewHandler<CameraView, CanvasControl>, ICameraViewHandler
{
    public CameraViewHandler() : base(Mapper)
    {
        cameraManager = CameraManager.Create(FrameReceived);
        cameraManager.CameraInfoAdded += (ci) => MainThread.BeginInvokeOnMainThread(() => VirtualView?.InvalidateMeasure());
        cameraManager.CameraInfoRemoved += (ci) => MainThread.BeginInvokeOnMainThread(() => VirtualView?.InvalidateMeasure());
        cameraManager.CameraInfosChanged += (cis) => MainThread.BeginInvokeOnMainThread(() => OnCameraInfosChanged(cis));
        cameraManager.IsStreamingChanged += (isStreaming) =>
        {
            if (!isStreaming)
            {
                DrawMode = DrawMode.None;
                MainThread.BeginInvokeOnMainThread(() => canvasControl.Invalidate());
            }
        };
        cameraManager.SelectedCameraChanged += (ci) => { };
    }

    private void OnCameraInfosChanged(ImmutableList<CameraInfo> cis) => VirtualView.CameraInfos = cis;

    private readonly CanvasControl canvasControl = new();
    private DrawMode drawMode;
    private bool isProcessingFrame = false;
    private MediaFrameReference? latestFrame = null;
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
    private static void MapHeight(CameraViewHandler handler, CameraView view) => handler.VirtualView?.CameraViewHandler?.OnHeightChanged(view.Height);
    private static void MapWidth(CameraViewHandler handler, CameraView view) => handler.VirtualView?.CameraViewHandler?.OnWidthChanged(view.Width);
    protected override CanvasControl CreatePlatformView()
    {
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

    private void FrameReceived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (isProcessingFrame) return;
        isProcessingFrame = true;

        latestFrame = sender.TryAcquireLatestFrame();
        if (latestFrame?.VideoMediaFrame == null)
        {
            latestFrame?.Dispose();
            latestFrame = null;
            return;
        }

        if (DrawMode == DrawMode.None)
        {
            DrawMode = latestFrame.VideoMediaFrame.Direct3DSurface != null && latestFrame.VideoMediaFrame.Direct3DSurface.Description.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized
                     ? DrawMode.Direct3DSurface
                     : DrawMode.Fallback;
        }
        _ = MainThread.InvokeOnMainThreadAsync(canvasControl.Invalidate);
    }

    private void CanvasDrawDirect3dSurface(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (latestFrame?.VideoMediaFrame == null)
        {
            latestFrame?.Dispose();
            isProcessingFrame = false;
            return;
        }

        var surface = latestFrame.VideoMediaFrame.Direct3DSurface;

        try
        {
            using var bmp = CanvasBitmap.CreateFromDirect3D11Surface(sender.Device, surface);
            DrawScaled(sender, args.DrawingSession, bmp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Canvas_Draw exception HRESULT=0x" + ex.HResult.ToString("X8") + " msg=" + ex.Message);
        }
        finally
        {
            latestFrame?.Dispose();
            latestFrame = null;
            isProcessingFrame = false;
        }
    }

    private async void CanvasDrawFallback(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (latestFrame?.VideoMediaFrame == null)
        {
            latestFrame?.Dispose();
            isProcessingFrame = false;
            return;
        }

        // Try existing software bitmap first
        var sb = latestFrame.VideoMediaFrame.SoftwareBitmap;
        try
        {
            if (sb == null && latestFrame.VideoMediaFrame.Direct3DSurface != null)
            {
                // IMPORTANT: use Ignore, not Pre-multiplied, for formats without alpha (e.g. NV12)
                sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(latestFrame.VideoMediaFrame.Direct3DSurface, BitmapAlphaMode.Ignore);
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
            var pixelBuffer = new byte[needed];
            sb.CopyToBuffer(pixelBuffer.AsBuffer());
            using var bmp = CanvasBitmap.CreateFromBytes(sender, pixelBuffer, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            DrawScaled(sender, args.DrawingSession, bmp);

        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Canvas_Draw exception HRESULT=0x" + ex.HResult.ToString("X8") + " msg=" + ex.Message);
        }
        finally
        {
            latestFrame?.Dispose();
            latestFrame = null;
            sb?.Dispose();
            isProcessingFrame = false;
        }
    }

    private static void DrawScaled(CanvasControl sender, CanvasDrawingSession ds, CanvasBitmap bmp)
    {
        var scale = Math.Min(sender.ActualWidth / bmp.SizeInPixels.Width, sender.ActualHeight / bmp.SizeInPixels.Height);
        var drawW = bmp.SizeInPixels.Width * scale;
        var drawH = bmp.SizeInPixels.Height * scale;
        var x = (sender.ActualWidth - drawW) / 2;
        var y = (sender.ActualHeight - drawH) / 2;
        ds.DrawImage(bmp, new System.Numerics.Vector2((float)x, (float)y));
    }

    protected override async void DisconnectHandler(CanvasControl platformView)
    {
        if (canvasControl != null)
        {
            canvasControl.Draw -= CanvasDrawDirect3dSurface;
            canvasControl.Draw -= CanvasDrawFallback;
            await MainThread.InvokeOnMainThreadAsync(canvasControl.Invalidate);
        }
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

    public async ValueTask DisposeAsync()
    {
        if (canvasControl != null)
        {
            canvasControl.Draw -= CanvasDrawDirect3dSurface;
            canvasControl.Draw -= CanvasDrawFallback;
            await MainThread.InvokeOnMainThreadAsync(canvasControl.Invalidate);
        }
        DrawMode = DrawMode.None;
        await StopAsync();
        latestFrame?.Dispose();
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
