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
            var lensFacingObj = chars.Get(CameraCharacteristics.LensFacing);
            int? facing = lensFacingObj is Java.Lang.Integer jint2 ? jint2.IntValue() : null;
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
            if (SelectedCamera.Id == cameraId) return;
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
        if (SelectedCamera == CameraInfo.Empty) throw new InvalidOperationException("Select a camera first.");
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStreaming) return;
            FrameCallback = frameCallback;
            _streamCts = new();
            var tcs = new TaskCompletionSource();
            // Use a modest default preview size; in production query supported sizes.
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
        if (planes == null || planes.Length < 3) return;

        var yPlane = planes[0];
        var uPlane = planes[1];
        var vPlane = planes[2];
        var yBuf = yPlane.Buffer; var uBuf = uPlane.Buffer; var vBuf = vPlane.Buffer;
        if (yBuf == null || uBuf == null || vBuf == null) return;

        int width = image.Width;
        int height = image.Height;
        int yRowStride = yPlane.RowStride;
        int yPixelStride = yPlane.PixelStride;
        int uRowStride = uPlane.RowStride;
        int uPixelStride = uPlane.PixelStride;
        int vRowStride = vPlane.RowStride;
        int vPixelStride = vPlane.PixelStride;

        var rgba = new byte[4 * width * height];
        // Convert YUV420 to BGRA32 (fast but not vectorized)
        for (int y = 0; y < height; y++)
        {
            int yRow = y * yRowStride;
            int uvRow = (y / 2) * uRowStride;
            for (int x = 0; x < width; x++)
            {
                int yIndex = yRow + x * yPixelStride;
                int uvIndex = uvRow + (x / 2) * uPixelStride;
                int vIndex = (y / 2) * vRowStride + (x / 2) * vPixelStride;
                int Y = (yBuf.Get(yIndex) & 0xFF); // Java.Nio access via Get(int)
                int U = (uBuf.Get(uvIndex) & 0xFF) - 128;
                int V = (vBuf.Get(vIndex) & 0xFF) - 128;
                int C = Y - 16; if (C < 0) C = 0;
                int R = (298 * C + 409 * V + 128) >> 8;
                int G = (298 * C - 100 * U - 208 * V + 128) >> 8;
                int B = (298 * C + 516 * U + 128) >> 8;
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;
                int idx = 4 * (y * width + x);
                rgba[idx + 0] = (byte)B; // BGRA
                rgba[idx + 1] = (byte)G;
                rgba[idx + 2] = (byte)R;
                rgba[idx + 3] = 255;
            }
        }

        var frame = new CameraFrame
        {
            CameraId = SelectedCamera.Id,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Width = width,
            Height = height,
            PixelFormat = CameraPixelFormat.Bgra32,
            Data = rgba
        };
        _ = OnFrameAsync(frame);
    }

    sealed class ImageListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        readonly Action<ImageReader> _cb;
        public ImageListener(Action<ImageReader> cb) => _cb = cb;
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is null) return; _cb(reader);
        }
    }

    sealed class StateCallback(AndroidCameraManager owner, TaskCompletionSource tcs) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            owner._cameraDevice = camera;
            try
            {
                var imageReader = owner._imageReader;
                if (imageReader is null)
                {
                    tcs.TrySetException(new InvalidOperationException("ImageReader not initialized"));
                    return;
                }
                if (imageReader.Surface is not Surface validSurface)
                {
                    tcs.TrySetException(new InvalidOperationException("ImageReader surface not available"));
                    return;
                }
                var surfaces = new List<Surface> { validSurface };
                camera.CreateCaptureSession(surfaces, new SessionCallback(owner, tcs), null);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        public override void OnDisconnected(CameraDevice camera) => tcs.TrySetException(new IOException("Camera disconnected"));
        public override void OnError(CameraDevice camera, CameraError error) => tcs.TrySetException(new IOException($"Camera error: {error}"));
    }

    sealed class SessionCallback(AndroidCameraManager owner, TaskCompletionSource tcs) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            owner._session = session;
            try
            {
                var cameraDevice = owner._cameraDevice;
                var imageReader = owner._imageReader;
                if (cameraDevice is null || imageReader is null)
                {
                    tcs.TrySetException(new InvalidOperationException("Camera not fully initialized"));
                    return;
                }
                if (imageReader.Surface is not Surface surface)
                {
                    tcs.TrySetException(new InvalidOperationException("ImageReader surface not available"));
                    return;
                }
                var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                builder.AddTarget(surface);
                var controlModeKey = CaptureRequest.ControlMode;
                if (controlModeKey is not null)
                    builder.Set(controlModeKey, (int)ControlMode.Auto);
                session.SetRepeatingRequest(builder.Build(), null, null);
                tcs.TrySetResult();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        public override void OnConfigureFailed(CameraCaptureSession session) => tcs.TrySetException(new IOException("Configure failed"));
    }
}
#endif
