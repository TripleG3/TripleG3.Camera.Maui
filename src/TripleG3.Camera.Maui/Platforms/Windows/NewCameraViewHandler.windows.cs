#if WINDOWS
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Devices.Enumeration;
using Windows.Graphics.DirectX.Direct3D11;
using TripleG3.Camera.Maui.Controls;

namespace TripleG3.Camera.Maui;

public sealed partial class NewCameraViewHandler : ViewHandler<NewCameraView, CanvasControl>, INewCameraViewHandler
{
    public static IPropertyMapper<NewCameraView, NewCameraViewHandler> Mapper = new PropertyMapper<NewCameraView, NewCameraViewHandler>(ViewHandler.ViewMapper)
    {
        [nameof(NewCameraView.CameraId)] = MapCameraId
    };

    public NewCameraViewHandler() : base(Mapper) { }

    static void MapCameraId(NewCameraViewHandler handler, NewCameraView view)
    {
        handler.VirtualView?.HandlerImpl?.OnCameraIdChanged(view.CameraId);
    }

    CanvasControl? _canvas;
    MediaCapture? _mediaCapture;
    MediaFrameReader? _reader;
    IDirect3DSurface? _latestSurface;
    readonly object _surfaceLock = new();
    string? _cameraId;
    bool _started;

    protected override CanvasControl CreatePlatformView()
    {
        _canvas = new CanvasControl();
        //_canvas.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(new Windows.UI.Color() { A = 255, R = 150, G = 150, B = 150 });
        VirtualView.HandlerImpl = this;
        _canvas.Invalidate();
        _canvas.Draw += Canvas_Draw;
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
        _canvas?.Invalidate();
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

        var targetFormat = frameSource.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720)
            .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
            .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();
        if (targetFormat != null)
        {
            try { await frameSource.SetFormatAsync(targetFormat); } catch { }
        }

        _reader = await _mediaCapture.CreateFrameReaderAsync(frameSource);
        _reader.FrameArrived += Reader_FrameArrived;
        await _reader.StartAsync();
    }

    void Reader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_started) return;
        using var frame = sender.TryAcquireLatestFrame();
        var surface = frame?.VideoMediaFrame?.Direct3DSurface;
        if (surface == null) return;
        lock (_surfaceLock)
        {
            _latestSurface = surface;
        }
        MainThread.InvokeOnMainThreadAsync(() => _canvas?.Invalidate());
    }

    void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        IDirect3DSurface? surface;
        lock (_surfaceLock)
            surface = _latestSurface;
        if (surface == null) return;
        try
        {
            using var bmp = CanvasBitmap.CreateFromDirect3D11Surface(sender.Device, surface);
            var ds = args.DrawingSession;
            var scaleX = sender.ActualWidth / bmp.SizeInPixels.Width;
            var scaleY = sender.ActualHeight / bmp.SizeInPixels.Height;
            var scale = Math.Min(scaleX, scaleY);
            var drawWidth = bmp.SizeInPixels.Width * scale;
            var drawHeight = bmp.SizeInPixels.Height * scale;
            var x = (sender.ActualWidth - drawWidth) / 2;
            var y = (sender.ActualHeight - drawHeight) / 2;
            ds.DrawImage(bmp, new System.Numerics.Vector2((float)x, (float)y));
        }
        catch { }
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
        lock (_surfaceLock) _latestSurface = null;
    }

    protected override void DisconnectHandler(CanvasControl platformView)
    {
        _ = CleanupAsync();
        if (_canvas != null)
            _canvas.Draw -= Canvas_Draw;
        base.DisconnectHandler(platformView);
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        GC.SuppressFinalize(this);
    }
}
#endif
