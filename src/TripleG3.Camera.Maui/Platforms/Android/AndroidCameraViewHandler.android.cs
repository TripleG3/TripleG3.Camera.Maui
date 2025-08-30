#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Views;
using Android.Runtime;
using Android.App;
using System.Threading.Tasks;
using System.Threading; // Added missing using directive
using Microsoft.Maui;
using Microsoft.Maui.Handlers;

namespace TripleG3.Camera.Maui;

public sealed class AndroidCameraViewHandler : Microsoft.Maui.Handlers.ViewHandler<CameraView, TextureView>, INewCameraViewHandler
{
    static IPropertyMapper<CameraView, AndroidCameraViewHandler> Mapper = new PropertyMapper<CameraView, AndroidCameraViewHandler>(Microsoft.Maui.Handlers.ViewHandler.ViewMapper)
    {
        [nameof(CameraView.CameraId)] = MapCameraId,
        [nameof(CameraView.IsMirrored)] = MapIsMirrored
    };

    public AndroidCameraViewHandler() : base(Mapper) { }

    static void MapCameraId(AndroidCameraViewHandler handler, CameraView view) => handler.VirtualView?.NewCameraViewHandler?.OnCameraIdChanged(view.CameraId);
    static void MapIsMirrored(AndroidCameraViewHandler handler, CameraView view) => handler.VirtualView?.NewCameraViewHandler?.OnMirrorChanged(view.IsMirrored);

    TextureView? _texture;
    CameraDevice? _device;
    CameraCaptureSession? _session;
    CameraManager? _mgr;
    string? _cameraId;
    bool _started;
    bool _isMirrored;
    bool _requestedStart;

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

    Task StartInternalAsync()
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
            var state = new CameraStateCallback(this, surface);
            _mgr!.OpenCamera(_cameraId, state, null);
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
        return Task.CompletedTask;
    }

    Task RestartAsync()
    {
        StopAsync();
        StartAsync();
        return Task.CompletedTask;
    }

    void OnTextureAvailable()
    {
        if (_requestedStart)
            _ = StartInternalAsync();
    }

    void SetDevice(CameraDevice device, Surface surface)
    {
        _device = device;
    try
    {
        var targets = new List<Surface> { surface };
#pragma warning disable CA1422
        _device.CreateCaptureSession(targets, new CaptureStateCallback(this, surface), null);
#pragma warning restore CA1422
    }
    catch { }
    }

    void OnSessionReady(CameraCaptureSession session, Surface surface)
    {
        _session = session;
        try
        {
            var req = _device!.CreateCaptureRequest(CameraTemplate.Preview);
            req.AddTarget(surface);
            req.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
            _session.SetRepeatingRequest(req.Build(), null, null);
        }
        catch { }
    }

    public void OnHeightChanged(double height) { /* no-op */ }
    public void OnWidthChanged(double width) { /* no-op */ }
    public void OnMirrorChanged(bool isMirrored) { _isMirrored = isMirrored; if (_texture != null) _texture.ScaleX = isMirrored ? -1f : 1f; }

    // Helper inner classes
    class SimpleTextureListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        readonly AndroidCameraViewHandler _owner;
        public SimpleTextureListener(AndroidCameraViewHandler owner) { _owner = owner; }
        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => _owner.OnTextureAvailable();
        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }

    class CameraStateCallback : CameraDevice.StateCallback
    {
        readonly AndroidCameraViewHandler _owner;
        readonly Surface _surface;
        public CameraStateCallback(AndroidCameraViewHandler owner, Surface surface) { _owner = owner; _surface = surface; }
        public override void OnOpened(CameraDevice camera) => _owner.SetDevice(camera, _surface);
        public override void OnDisconnected(CameraDevice camera) { try { camera.Close(); } catch { } }
        public override void OnError(CameraDevice camera, CameraError error) { try { camera.Close(); } catch { } }
    }

    class CaptureStateCallback : CameraCaptureSession.StateCallback
    {
        readonly AndroidCameraViewHandler _owner;
        readonly Surface _surface;
        public CaptureStateCallback(AndroidCameraViewHandler owner, Surface surface) { _owner = owner; _surface = surface; }
        public override void OnConfigured(CameraCaptureSession session) => _owner.OnSessionReady(session, _surface);
        public override void OnConfigureFailed(CameraCaptureSession session) { }
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
