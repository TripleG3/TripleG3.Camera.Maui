#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Views;
using Android.OS;
using Android.Media;

namespace TripleG3.Camera.Maui;

public sealed class AndroidCameraViewHandler : Microsoft.Maui.Handlers.ViewHandler<CameraView, TextureView>, INewCameraViewHandler
{
    private static IPropertyMapper<CameraView, AndroidCameraViewHandler> Mapper = new PropertyMapper<CameraView, AndroidCameraViewHandler>(Microsoft.Maui.Handlers.ViewHandler.ViewMapper)
    {
        [nameof(CameraView.CameraId)] = MapCameraId,
        [nameof(CameraView.IsMirrored)] = MapIsMirrored
    };

    public AndroidCameraViewHandler() : base(Mapper) { }

    private static void MapCameraId(AndroidCameraViewHandler handler, CameraView view) => handler.VirtualView?.NewCameraViewHandler?.OnCameraIdChanged(view.CameraId);
    private static void MapIsMirrored(AndroidCameraViewHandler handler, CameraView view) => handler.VirtualView?.NewCameraViewHandler?.OnMirrorChanged(view.IsMirrored);

    private TextureView? _texture;
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private CameraManager? _mgr;
    private string? _cameraId;
    private bool _started;
    private bool _isMirrored;
    private bool _requestedStart;
    private ImageReader? _imageReader;
    private HandlerThread? _bgThread;
    private Android.OS.Handler? _bgHandler;

    protected override TextureView CreatePlatformView()
    {
    _texture = new TextureView(Android.App.Application.Context!);
        _texture.SurfaceTextureListener = new SimpleTextureListener(this);
        VirtualView.NewCameraViewHandler = this;
    _mgr = (CameraManager?)Android.App.Application.Context!.GetSystemService(Context.CameraService);
        return _texture;
    }

    public bool IsRunning => _started;

    public void OnCameraIdChanged(string? cameraId)
    {
        _cameraId = cameraId;
        if (_started)
            _ = RestartAsync();
    }

    public Task StartAsync()
    {
        if (_texture == null || _texture.IsAvailable == false)
        {
            _requestedStart = true;
            return Task.CompletedTask;
        }
        _requestedStart = false;
        return StartInternalAsync();
    }

    private Task StartInternalAsync()
    {
        if (_started) return Task.CompletedTask;
    if (_mgr == null) _mgr = (CameraManager?)Android.App.Application.Context!.GetSystemService(Context.CameraService);
        if (string.IsNullOrEmpty(_cameraId))
        {
            var ids = _mgr?.GetCameraIdList();
            _cameraId = ids?.FirstOrDefault();
        }
    if (_cameraId == null) return Task.CompletedTask;
        try
        {
            var t = _texture!;
            var st = t.SurfaceTexture;
            var surface = new Surface(st);
            // Setup image reader for frame extraction (YUV420)
            _imageReader = ImageReader.NewInstance(t.Width > 0 ? t.Width : 640, t.Height > 0 ? t.Height : 480, ImageFormatType.Yuv420888, 2);
            _bgThread = new HandlerThread("cam_bg");
            _bgThread.Start();
            _bgHandler = new Android.OS.Handler(_bgThread.Looper!);
            _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(this), _bgHandler);
            var state = new CameraStateCallback(this, surface);
            _mgr!.OpenCamera(_cameraId, state, _bgHandler);
            // CameraDevice open will call OnOpened -> create session
            _started = true;
        }
    catch { }
    return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_started) return Task.CompletedTask;
        _started = false;
        try
        {
            _session?.StopRepeating();
            _session?.Close();
            _session = null;
        }
        catch { }
        try { _device?.Close(); } catch { }
        _device = null;
    try { _imageReader?.Close(); } catch { }
    _imageReader = null;
    try { _bgThread?.QuitSafely(); _bgThread?.Join(); } catch { }
    _bgThread = null; _bgHandler = null;
        return Task.CompletedTask;
    }

    private Task RestartAsync()
    {
        StopAsync();
        StartAsync();
        return Task.CompletedTask;
    }

    private void OnTextureAvailable()
    {
        if (_requestedStart)
            _ = StartInternalAsync();
    }

    private void SetDevice(CameraDevice device, Surface surface)
    {
        _device = device;
    try
    {
            var targets = new List<Surface> { surface };
            if (_imageReader != null)
                targets.Add(_imageReader.Surface!);
#pragma warning disable CA1422
        _device.CreateCaptureSession(targets, new CaptureStateCallback(this, surface), null);
#pragma warning restore CA1422
    }
    catch { }
    }

    private void OnSessionReady(CameraCaptureSession session, Surface surface)
    {
        _session = session;
        try
        {
            var req = _device!.CreateCaptureRequest(CameraTemplate.Preview);
            req.AddTarget(surface);
            req.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
            _session.SetRepeatingRequest(req.Build(), null, null);
            // Attempt aspect fill once preview sizes known (use texture size fallback)
            ApplyAspectFill(_texture?.Width ?? 0, _texture?.Height ?? 0);
        }
        catch { }
    }

    public void OnHeightChanged(double height) { /* no-op */ }
    public void OnWidthChanged(double width) { /* no-op */ }
    public void OnMirrorChanged(bool isMirrored) { _isMirrored = isMirrored; if (_texture != null) _texture.ScaleX = isMirrored ? -1f : 1f; }

    private void ApplyAspectFill(int frameW, int frameH)
    {
        var tv = _texture; if (tv == null) return;
        if (tv.Width == 0 || tv.Height == 0 || frameW == 0 || frameH == 0) return;
        float viewW = tv.Width;
        float viewH = tv.Height;
        float scale = Math.Max(viewW / frameW, viewH / frameH);
        float scaledW = frameW * scale;
        float scaledH = frameH * scale;
        float dx = (viewW - scaledW) / 2f;
        float dy = (viewH - scaledH) / 2f;
        var matrix = new Android.Graphics.Matrix();
        matrix.PostScale(scale, scale, 0, 0);
        matrix.PostTranslate(dx, dy);
        tv.SetTransform(matrix);
    }

    // Helper inner classes
    private class SimpleTextureListener(AndroidCameraViewHandler owner) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => owner.OnTextureAvailable();
        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }

    private class CameraStateCallback(AndroidCameraViewHandler owner, Surface surface) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.SetDevice(camera, surface);
        public override void OnDisconnected(CameraDevice camera) { try { camera.Close(); } catch { } }
        public override void OnError(CameraDevice camera, CameraError error) { try { camera.Close(); } catch { } }
    }

    private class CaptureStateCallback(AndroidCameraViewHandler owner, Surface surface) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.OnSessionReady(session, surface);
        public override void OnConfigureFailed(CameraCaptureSession session) { }
    }

    private class ImageAvailableListener(AndroidCameraViewHandler owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (!owner._started) return;
            if (reader == null) return;
            Android.Media.Image? img = null;
            try
            {
                img = reader.AcquireLatestImage();
                if (img == null) return;
                var planes = img.GetPlanes();
                if (planes == null || planes.Length < 3) return;
                int w = img.Width;
                int h = img.Height;
                // Convert YUV_420_888 to I420
                var yPlane = planes[0];
                var uPlane = planes[1];
                var vPlane = planes[2];
                int ySize = w * h;
                int uvW = w / 2;
                int uvH = h / 2;
                int uSize = uvW * uvH;
                int vSize = uSize;
                var data = new byte[ySize + uSize + vSize];
                // Copy Y
                CopyPlane(yPlane, w, h, data, 0, 1);
                // U and V (stride-aware copy)
                CopyPlane(uPlane, uvW, uvH, data, ySize, 1);
                CopyPlane(vPlane, uvW, uvH, data, ySize + uSize, 1);
                owner.BroadcastAndroidYuv(data, w, h);
            }
            catch { }
            finally { try { img?.Close(); } catch { } }
        }

        private static void CopyPlane(Android.Media.Image.Plane plane, int width, int height, byte[] dest, int destOffset, int pixelStrideExpected)
        {
            var buf = plane.Buffer!; // guaranteed non-null by Android binding
            int rowStride = plane.RowStride;
            int pixelStride = plane.PixelStride;
            var row = new byte[rowStride];
            int di = destOffset;
            for (int y = 0; y < height; y++)
            {
                buf.Position(y * rowStride);
                buf.Get(row, 0, rowStride);
                // Extract width bytes considering pixel stride
                if (pixelStride == 1)
                {
                    Buffer.BlockCopy(row, 0, dest, di, width);
                }
                else
                {
                    for (int x = 0; x < width; x++)
                        dest[di + x] = row[x * pixelStride];
                }
                di += width;
            }
        }
    }

    private void BroadcastAndroidYuv(byte[] i420, int w, int h)
    {
        if (VirtualView?.Handler?.MauiContext == null) return;
        var broadcaster = VirtualView.Handler.MauiContext.Services.GetService(typeof(ICameraFrameBroadcaster)) as ICameraFrameBroadcaster;
        if (broadcaster == null) return;
        var copy = new byte[i420.Length];
        Buffer.BlockCopy(i420, 0, copy, 0, i420.Length);
        var frame = new CameraFrame(CameraPixelFormat.YUV420, w, h, DateTime.UtcNow.Ticks, _isMirrored, copy);
        broadcaster.Submit(frame);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _ = StopAsync();
        }
        catch { }
        return ValueTask.CompletedTask;
    }
}

#endif
