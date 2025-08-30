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

namespace TripleG3.Camera.Maui;

public sealed class WindowsCameraViewHandler : ViewHandler<CameraView, CanvasControl>, INewCameraViewHandler
{
    public static IPropertyMapper<CameraView, WindowsCameraViewHandler> Mapper =
        new PropertyMapper<CameraView, WindowsCameraViewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(CameraView.CameraId)] = MapCameraId,
            [nameof(CameraView.Height)] = MapHeight,
            [nameof(CameraView.Width)] = MapWidth
        };

    public WindowsCameraViewHandler() : base(Mapper) { }

    static void MapCameraId(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnCameraIdChanged(view.CameraId);

    static void MapHeight(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnHeightChanged(view.Height);

    static void MapWidth(WindowsCameraViewHandler handler, CameraView view) =>
        handler.VirtualView?.NewCameraViewHandler?.OnWidthChanged(view.Height);

    CanvasControl? _canvas;
    MediaCapture? _mediaCapture;
    MediaFrameReader? _reader;
    IDirect3DSurface? _latestSurface;
    readonly object _surfaceLock = new();
    string? _cameraId;
    bool _started;

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
        VirtualView.NewCameraViewHandler = this;
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
        if (string.IsNullOrWhiteSpace(_cameraId))
            _cameraId = await PickFirstCameraAsync();
        await InitializeCaptureAsync();
        _started = true;
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;
        await CleanupAsync();
        _ = MainThread.InvokeOnMainThreadAsync(() => _canvas?.Invalidate());
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

        // Marshal invalidate to UI thread
        _ = MainThread.InvokeOnMainThreadAsync(() => _canvas?.Invalidate());
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
                DrawScaled(sender, args.DrawingSession, bmp);
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
                DrawScaled(sender, args.DrawingSession, bmp);
            }
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

    async Task CleanupAsync()
    {
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
        _ = CleanupAsync();
        if (_canvas != null) _canvas.Draw -= Canvas_Draw;
        base.DisconnectHandler(platformView);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
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
}
#endif
