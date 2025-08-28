#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace TripleG3.Camera.Maui;

public sealed class WindowsCameraManager : CameraManager
{
    MediaCapture? _mediaCapture;
    MediaFrameReader? _frameReader;
    byte[] _buffer = [];
    VideoMediaFrameFormat? _activeFormat;

    public Action<string>? Logger { get; set; }
    public bool FastPreview { get; set; }
    public Action<long>? TimestampObserver { get; set; }

    public override async ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
        return devices.Select(d => new CameraInfo(d.Id, d.Name, d.EnclosureLocation?.Panel switch
        {
            Windows.Devices.Enumeration.Panel.Front => CameraFacing.Front,
            Windows.Devices.Enumeration.Panel.Back => CameraFacing.Back,
            _ => CameraFacing.Unknown
        }, d.EnclosureLocation?.Panel == null)).ToList();
    }

    public override async ValueTask SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (SelectedCamera.Id == cameraId) return;
            var cams = await GetCamerasAsync(cancellationToken);
            SelectedCamera = cams.FirstOrDefault(c => c.Id == cameraId) ?? throw new ArgumentException("Camera not found", nameof(cameraId));
            if (IsStreaming)
                await StopInternalAsync();
        }
        finally { SyncLock.Release(); }
    }

    public override async ValueTask StartAsync(Func<CameraFrame, ValueTask> frameCallback, CancellationToken cancellationToken = default)
    {
        if (IsStreaming)
        {
            SetFrameCallback(frameCallback);
            Logger?.Invoke("Callback switched without restart.");
            return;
        }
        if (SelectedCamera == CameraInfo.Empty) throw new InvalidOperationException("Select a camera first.");
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStreaming) { SetFrameCallback(frameCallback); return; }
            FrameCallback = frameCallback;
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = SelectedCamera.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };
            await _mediaCapture.InitializeAsync(settings).AsTask(cancellationToken);
            var frameSource = _mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ?? _mediaCapture.FrameSources.Values.First();

            var formats = frameSource.SupportedFormats.Select(f => new
            {
                Format = f,
                Sub = f.Subtype,
                W = f.VideoFormat.Width,
                H = f.VideoFormat.Height,
                Fps = (double)f.FrameRate.Numerator / f.FrameRate.Denominator
            }).ToList();
            Logger?.Invoke("Formats:" + string.Join(';', formats.Select(f => $"{f.Sub}:{f.W}x{f.H}@{f.Fps:F1}")));

            var preferred = formats.Where(f => f.W <= 640 && f.H <= 480)
                .OrderByDescending(f => f.Fps).ThenByDescending(f => f.W * f.H).FirstOrDefault()
                ?? formats.Where(f => f.W <= 1280 && f.H <= 720)
                    .OrderByDescending(f => f.Fps).ThenByDescending(f => f.W * f.H).FirstOrDefault()
                ?? formats.OrderByDescending(f => f.Fps).ThenBy(f => f.W * f.H).FirstOrDefault();

            if (preferred != null)
            {
                try
                {
                    await frameSource.SetFormatAsync(preferred.Format).AsTask(cancellationToken);
                    _activeFormat = preferred.Format.VideoFormat;
                    Logger?.Invoke($"FormatChosen subtype={preferred.Sub} width={preferred.W} height={preferred.H} fps={preferred.Fps:F2}");
                }
                catch (Exception ex) { Logger?.Invoke($"FormatSetFailed: {ex.Message}"); }
            }

            _frameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource).AsTask(cancellationToken);
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            await _frameReader.StartAsync().AsTask(cancellationToken);
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
        IsStreaming = false;
        if (_frameReader != null)
        {
            try { await _frameReader.StopAsync().AsTask(); } catch { }
            _frameReader.FrameArrived -= FrameReader_FrameArrived;
            _frameReader.Dispose();
            _frameReader = null;
        }
        _mediaCapture?.Dispose();
        _mediaCapture = null;
    }

    private async void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!IsStreaming) return;
        using var frame = sender.TryAcquireLatestFrame();
        var vf = frame?.VideoMediaFrame;
        if (vf == null) return;
        long ts = DateTime.UtcNow.Ticks;
        if (FastPreview)
        {
            TimestampObserver?.Invoke(ts);
            return;
        }

        SoftwareBitmap? sb = vf.SoftwareBitmap;
        bool copied = false;
        if (sb == null && vf.Direct3DSurface != null)
        {
            try { sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(vf.Direct3DSurface, BitmapAlphaMode.Premultiplied).AsTask(); copied = true; }
            catch { return; }
        }
        if (sb == null) return;

        SoftwareBitmap usable = sb;
        SoftwareBitmap? converted = null;
        if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            try { converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied); usable = converted; }
            catch { if (copied) sb.Dispose(); return; }
        }
        int needed = 4 * usable.PixelWidth * usable.PixelHeight;
        if (_buffer.Length != needed) _buffer = new byte[needed];
        try
        {
            usable.CopyToBuffer(_buffer.AsBuffer());
            var frameObj = new CameraFrame
            {
                CameraId = SelectedCamera.Id,
                TimestampUtcTicks = ts,
                Width = usable.PixelWidth,
                Height = usable.PixelHeight,
                PixelFormat = CameraPixelFormat.Bgra32,
                Data = _buffer
            };
            _ = OnFrameAsync(frameObj);
        }
        finally
        {
            converted?.Dispose();
            if (copied) sb.Dispose();
        }
    }
}
#endif
