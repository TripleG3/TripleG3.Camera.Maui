#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Devices.Enumeration;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Graphics.DirectX;
using Windows.Media.MediaProperties;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;

namespace TripleG3.Camera.Maui;

public sealed partial class WindowsCameraViewHandler : ViewHandler<CameraView, CanvasControl>, INewCameraViewHandler
{
    public static IPropertyMapper<CameraView, WindowsCameraViewHandler> Mapper =
        new PropertyMapper<CameraView, WindowsCameraViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(CameraView.CameraId)] = MapCameraId,
            [nameof(CameraView.Height)] = MapHeight,
            [nameof(CameraView.Width)] = MapWidth,
            [nameof(CameraView.IsMirrored)] = MapIsMirrored
        };

    public WindowsCameraViewHandler() : base(Mapper) { }

    static void MapCameraId(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnCameraIdChanged(view.CameraId);

    static void MapHeight(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnHeightChanged(view.Height);

    static void MapWidth(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnWidthChanged(view.Height);

     static void MapIsMirrored(WindowsCameraViewHandler handler, CameraView view) =>
         handler.VirtualView?.NewCameraViewHandler?.OnMirrorChanged(view.IsMirrored);

    CanvasControl? _canvas;
    MediaCapture? _mediaCapture;
    MediaFrameReader? _reader;
    IDirect3DSurface? _latestSurface;
    readonly object _surfaceLock = new();
    string? _cameraId;
    bool _started;
    bool _isMirrored;
    bool _disposing; // only true during final app disposal; not for normal stop/switch
    DispatcherQueue? _dispatcherQueue;

    // Fallback (when BGRA8 surface not provided)
    bool _fallbackConversion;
    byte[] _pixelBuffer = Array.Empty<byte>();
    int _fbWidth;
    int _fbHeight;

    protected override CanvasControl CreatePlatformView()
    {
        _canvas = new CanvasControl();
        _canvas.Draw += Canvas_Draw;
        _canvas.Loaded += (_, _) => _canvas.Invalidate();
    _dispatcherQueue = _canvas.DispatcherQueue;
        VirtualView.NewCameraViewHandler = this;
    CameraLifecycleManager.Register(this);
        // If a start was requested before handler creation, honor it now
        if (VirtualView.RequestedStart && !_started)
        {
            _ = StartAsync();
        }
        return _canvas;
    }

    public bool IsRunning => _started;

    public void OnCameraIdChanged(string? cameraId)
    {
        _cameraId = cameraId;
        if (_started)
            _ = RestartAsync();
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _disposing = false; // allow invalidations again after a normal restart
        if (string.IsNullOrWhiteSpace(_cameraId))
            _cameraId = await PickFirstCameraAsync();
        await InitializeCaptureAsync();
        _started = true;
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        await CleanupAsync(final: false);
    InvalidateOnUI();
    }

    async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    async Task<string?> PickFirstCameraAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices.FirstOrDefault()?.Id;
    }

    async Task InitializeCaptureAsync()
    {
        if (_cameraId == null) return;

        _mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings
        {
            VideoDeviceId = _cameraId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl
        };
        await _mediaCapture.InitializeAsync(settings);

        var frameSource = _mediaCapture.FrameSources.Values
            .FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ?? _mediaCapture.FrameSources.Values.First();

        // Prefer BGRA8 highest FPS <=1280x720
        var formats = frameSource.SupportedFormats.ToList();

        var bgra = formats
            .Where(f => f.Subtype == MediaEncodingSubtypes.Bgra8 && f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720)
            .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
            .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();

        var chosen = bgra;
        if (chosen == null)
        {
            // fallback to any <=1280x720
            chosen = formats
                .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720)
                .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .FirstOrDefault();
            _fallbackConversion = true; // likely NV12 or something unsupported directly
        }
        else
        {
            _fallbackConversion = false;
        }

        if (chosen != null)
        {
            try { await frameSource.SetFormatAsync(chosen); }
            catch { }
        }

        _reader = await _mediaCapture.CreateFrameReaderAsync(frameSource);
        _reader.FrameArrived += Reader_FrameArrived;
        await _reader.StartAsync();
    }

    async void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_started) return;
        using var frame = sender.TryAcquireLatestFrame();
        var vmf = frame?.VideoMediaFrame;
        if (vmf == null) return;

        if (!_fallbackConversion)
        {
            var surface = vmf.Direct3DSurface;
            if (surface == null) return;
            lock (_surfaceLock)
                _latestSurface = surface;
            // Extract BGRA frame for broadcast (copy small region only when subscribers exist later optimization)
            if (surface != null)
            {
                try
                {
                    using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface, BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
                    BroadcastSoftwareBitmap(sb);
                }
                catch { }
            }
        }
        else
        {
            // Fallback path: get SoftwareBitmap in BGRA8
            SoftwareBitmap? sb = null;
            try
            {
                // Try existing software bitmap first
                sb = vmf.SoftwareBitmap;

                if (sb == null && vmf.Direct3DSurface != null)
                {
                    // IMPORTANT: use Ignore, not Premultiplied, for formats without alpha (e.g. NV12)
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
                if (_pixelBuffer.Length != needed)
                    _pixelBuffer = new byte[needed];

                sb.CopyToBuffer(_pixelBuffer.AsBuffer());

                lock (_surfaceLock)
                {
                    _fbWidth = w;
                    _fbHeight = h;
                }
                BroadcastPixelBuffer(_pixelBuffer, w, h);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fallback conversion error: 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                sb?.Dispose();
            }
        }

    // Marshal invalidate to UI thread (guard for shutdown)
    InvalidateOnUI();
    }

    void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            if (_fallbackConversion)
            {
                int w, h;
                byte[] local;
                lock (_surfaceLock)
                {
                    w = _fbWidth;
                    h = _fbHeight;
                    if (w == 0 || h == 0) { System.Diagnostics.Debug.WriteLine("Canvas_Draw: fallback no data"); return; }
                    local = _pixelBuffer;
                }
                using var bmp = CanvasBitmap.CreateFromBytes(sender, local, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                DrawScaled(sender, args.DrawingSession, bmp, _isMirrored);
            }
            else
            {
                IDirect3DSurface? surface;
                lock (_surfaceLock) surface = _latestSurface;
                if (surface == null)
                {
                    System.Diagnostics.Debug.WriteLine("Canvas_Draw: no surface");
                    return;
                }
                using var bmp = CanvasBitmap.CreateFromDirect3D11Surface(sender.Device, surface);
                DrawScaled(sender, args.DrawingSession, bmp, _isMirrored);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Canvas_Draw exception HRESULT=0x" + ex.HResult.ToString("X8") + " msg=" + ex.Message);
        }
    }

    static void DrawScaled(CanvasControl sender, CanvasDrawingSession ds, CanvasBitmap bmp, bool mirrored)
    {
        var scale = Math.Min(
            sender.ActualWidth / bmp.SizeInPixels.Width,
            sender.ActualHeight / bmp.SizeInPixels.Height);
        var drawW = bmp.SizeInPixels.Width * scale;
        var drawH = bmp.SizeInPixels.Height * scale;
        var x = (sender.ActualWidth - drawW) / 2;
        var y = (sender.ActualHeight - drawH) / 2;
        if (!mirrored)
        {
            ds.DrawImage(bmp, new System.Numerics.Vector2((float)x, (float)y));
        }
        else
        {
            var centerX = (float)(x + drawW / 2);
            var centerY = (float)(y + drawH / 2);
            var prev = ds.Transform;
            ds.Transform = System.Numerics.Matrix3x2.CreateScale(-1, 1, new System.Numerics.Vector2(centerX, centerY));
            ds.DrawImage(bmp, new System.Numerics.Vector2((float)x, (float)y));
            ds.Transform = prev;
        }
    }

    async Task CleanupAsync(bool final)
    {
        if (final)
            _disposing = true;
        if (_reader != null)
        {
            try { await _reader.StopAsync(); } catch { }
            _reader.FrameArrived -= Reader_FrameArrived;
            _reader.Dispose();
            _reader = null;
        }
        _mediaCapture?.Dispose();
        _mediaCapture = null;
        lock (_surfaceLock)
        {
            _latestSurface = null;
            _fbWidth = _fbHeight = 0;
        }
    }

    protected override void DisconnectHandler(CanvasControl platformView)
    {
    _ = CleanupAsync(final: true);
        if (_canvas != null) _canvas.Draw -= Canvas_Draw;
    CameraLifecycleManager.Unregister(this);
        base.DisconnectHandler(platformView);
    }

    public async ValueTask DisposeAsync()
    {
    await CleanupAsync(final: true);
    CameraLifecycleManager.Unregister(this);
        GC.SuppressFinalize(this);
    }

    public void OnHeightChanged(double height)
    {
        if (_canvas == null || height < 1)
            return;

        _canvas.Height = height;
    }

    public void OnWidthChanged(double width)
    {
        if (_canvas == null || width < 1)
            return;

        _canvas.Width = width;
    }

     public void OnMirrorChanged(bool isMirrored)
     {
         _isMirrored = isMirrored;
        InvalidateOnUI();
     }

    void InvalidateOnUI()
    {
        if (_disposing) return;
        try
        {
            var canvas = _canvas;
            if (canvas == null) return;
            if (MainThread.IsMainThread)
            {
                canvas.Invalidate();
                return;
            }
            if (_dispatcherQueue != null)
            {
                _ = _dispatcherQueue.TryEnqueue(() => canvas.Invalidate());
            }
            else
            {
                // As a last resort; may throw if main thread gone, swallow
                _ = MainThread.InvokeOnMainThreadAsync(() => canvas.Invalidate());
            }
        }
        catch { /* swallow during shutdown */ }
    }
}

partial class WindowsCameraViewHandler
{
    static readonly byte[] _scratchHeader = Array.Empty<byte>();
    void BroadcastSoftwareBitmap(SoftwareBitmap sb)
    {
        if (VirtualView?.Handler?.MauiContext == null) return;
        var broadcaster = VirtualView.Handler.MauiContext.Services.GetService(typeof(ICameraFrameBroadcaster)) as ICameraFrameBroadcaster;
        if (broadcaster == null) return;
        if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            return; // ensure BGRA8
        int w = sb.PixelWidth;
        int h = sb.PixelHeight;
        byte[] data = new byte[w * h * 4];
        try
        {
            sb.CopyToBuffer(data.AsBuffer());
            var frame = new CameraFrame(CameraPixelFormat.BGRA32, w, h, DateTime.UtcNow.Ticks, _isMirrored, data);
            broadcaster.Submit(frame);
        }
        catch { }
    }

    void BroadcastPixelBuffer(byte[] buffer, int w, int h)
    {
        if (VirtualView?.Handler?.MauiContext == null) return;
        var broadcaster = VirtualView.Handler.MauiContext.Services.GetService(typeof(ICameraFrameBroadcaster)) as ICameraFrameBroadcaster;
        if (broadcaster == null) return;
        // buffer is BGRA in fallback path after conversion
        var copy = new byte[w * h * 4];
        Buffer.BlockCopy(buffer, 0, copy, 0, copy.Length);
        var frame = new CameraFrame(CameraPixelFormat.BGRA32, w, h, DateTime.UtcNow.Ticks, _isMirrored, copy);
        broadcaster.Submit(frame);
    }
}
#endif
