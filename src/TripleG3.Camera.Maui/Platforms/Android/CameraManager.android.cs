#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.Views;

namespace TripleG3.Camera.Maui;

internal sealed class AndroidCameraManager : CameraManager
{
    readonly Context _context;
    readonly Android.Hardware.Camera2.CameraManager? _system;
    CameraDevice? _cameraDevice;
    CameraCaptureSession? _session;
    ImageReader? _imageReader;
    CancellationTokenSource? _streamCts;

    public AndroidCameraManager()
    {
        _context = global::Microsoft.Maui.ApplicationModel.Platform.CurrentActivity ?? Android.App.Application.Context;
        _system = (Android.Hardware.Camera2.CameraManager?)_context.GetSystemService(Context.CameraService);
    }

    public override ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        if (_system is null) return ValueTask.FromResult<IReadOnlyList<CameraInfo>>(Array.Empty<CameraInfo>());
        var list = new List<CameraInfo>();
        foreach (var id in _system.GetCameraIdList())
        {
            var chars = _system.GetCameraCharacteristics(id);
            var facing = (int?)chars.Get(CameraCharacteristics.LensFacing);
            bool ext = facing == (int)LensFacing.External; // heuristic
            list.Add(new CameraInfo(id, $"Camera {id}", facing switch
            {
                (int)LensFacing.Front => CameraFacing.Front,
                (int)LensFacing.Back => CameraFacing.Back,
                (int)LensFacing.External => CameraFacing.External,
                _ => CameraFacing.Unknown
            }, ext));
        }
        return ValueTask.FromResult<IReadOnlyList<CameraInfo>>(list);
    }

    public override async ValueTask SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (SelectedCamera?.Id == cameraId) return;
            var cams = await GetCamerasAsync(cancellationToken);
            var cam = cams.FirstOrDefault(c => c.Id == cameraId) ?? throw new ArgumentException("Camera not found", nameof(cameraId));
            if (IsStreaming)
            {
                await StopInternalAsync();
            }
            SelectedCamera = cam;
        }
        finally { SyncLock.Release(); }
    }

    public override async ValueTask StartAsync(Func<CameraFrame, ValueTask> frameCallback, CancellationToken cancellationToken = default)
    {
        if (IsStreaming) return;
        if (SelectedCamera is null) throw new InvalidOperationException("Select a camera first.");
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStreaming) return;
            FrameCallback = frameCallback;
            _streamCts = new();
            var tcs = new TaskCompletionSource();
            _imageReader = ImageReader.NewInstance(1280, 720, ImageFormatType.Yuv420888, 2);
            _imageReader.SetOnImageAvailableListener(new ImageListener(OnImageAvailable), null);

            _system!.OpenCamera(SelectedCamera.Id, new StateCallback(this, tcs), null);
            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            await tcs.Task.ConfigureAwait(false);
            IsStreaming = true;
        }
        finally { SyncLock.Release(); }
    }

    public override async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try { await StopInternalAsync(); }
        finally { SyncLock.Release(); }
    }

    async Task StopInternalAsync()
    {
        await Task.Yield();
        IsStreaming = false;
        try { _streamCts?.Cancel(); } catch { }
        _streamCts?.Dispose();
        _streamCts = null;
        try { _session?.Close(); } catch { }
        _session?.Dispose(); _session = null;
        try { _cameraDevice?.Close(); } catch { }
        _cameraDevice?.Dispose(); _cameraDevice = null;
        _imageReader?.Close(); _imageReader?.Dispose(); _imageReader = null;
    }

    void OnImageAvailable(ImageReader reader)
    {
        if (!IsStreaming) return;
        using var image = reader.AcquireLatestImage();
        if (image == null) return;
        var planes = image.GetPlanes();
        if (planes == null || planes.Length == 0) return;
        int size = 0;
        foreach (var p in planes) size += p.Buffer?.Remaining() ?? 0;
        var data = new byte[size];
        int offset = 0;
        foreach (var p in planes)
        {
            var buf = p.Buffer;
            if (buf == null) continue;
            int len = buf.Remaining();
            buf.Get(data, offset, len);
            offset += len;
        }
        var frame = new CameraFrame
        {
            CameraId = SelectedCamera!.Id,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Width = image.Width,
            Height = image.Height,
            PixelFormat = CameraPixelFormat.Yuv420,
            Data = data
        };
        _ = OnFrameAsync(frame);
    }

    sealed class ImageListener(Action<ImageReader> cb) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader) => cb(reader);
    }

    sealed class StateCallback(AndroidCameraManager owner, TaskCompletionSource tcs) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            owner._cameraDevice = camera;
            try
            {
                var surfaces = new List<Surface> { owner._imageReader!.Surface };
                camera.CreateCaptureSession(surfaces, new SessionCallback(owner, tcs), null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }
        public override void OnDisconnected(CameraDevice camera)
        {
            tcs.TrySetException(new System.IO.IOException("Camera disconnected"));
        }
        public override void OnError(CameraDevice camera, CameraError error)
        {
            tcs.TrySetException(new System.IO.IOException($"Camera error: {error}"));
        }
    }

    sealed class SessionCallback(AndroidCameraManager owner, TaskCompletionSource tcs) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            owner._session = session;
            try
            {
                var builder = owner._cameraDevice!.CreateCaptureRequest(CameraTemplate.Preview);
                builder.AddTarget(owner._imageReader!.Surface);
                builder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
                session.SetRepeatingRequest(builder.Build(), null, null);
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        public override void OnConfigureFailed(CameraCaptureSession session)
        { tcs.TrySetException(new System.IO.IOException("Configure failed")); }
    }
}
#endif
