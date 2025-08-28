#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace TripleG3.Camera.Maui;

internal sealed class WindowsCameraManager : CameraManager
{
    MediaCapture? _mediaCapture;
    MediaFrameReader? _frameReader;

    public override async ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
        var list = devices.Select(d => new CameraInfo(d.Id, d.Name, d.EnclosureLocation?.Panel switch
        {
            Windows.Devices.Enumeration.Panel.Front => CameraFacing.Front,
            Windows.Devices.Enumeration.Panel.Back => CameraFacing.Back,
            _ => CameraFacing.Unknown
        }, d.EnclosureLocation?.Panel == null)).ToList();
        return list;
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
        if (IsStreaming) return;
        if (SelectedCamera == CameraInfo.Empty) throw new InvalidOperationException("Select a camera first.");
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (IsStreaming) return;
            FrameCallback = frameCallback;
            _mediaCapture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = SelectedCamera.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            };
            await _mediaCapture.InitializeAsync(settings).AsTask(cancellationToken);
            var frameSource = _mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) ?? _mediaCapture.FrameSources.Values.First();
            _frameReader = await _mediaCapture.CreateFrameReaderAsync(frameSource, MediaEncodingSubtypes.Bgra8).AsTask(cancellationToken);
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

    private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!IsStreaming) return;
        using var frame = sender.TryAcquireLatestFrame();
        var vf = frame?.VideoMediaFrame;
        var sb = vf?.SoftwareBitmap;
        if (vf == null || sb == null) return;
        SoftwareBitmap converted = sb;
        if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        var bytes = new byte[4 * converted.PixelWidth * converted.PixelHeight];
        converted.CopyToBuffer(bytes.AsBuffer());
        var frameObj = new CameraFrame
        {
            CameraId = SelectedCamera.Id,
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            Width = converted.PixelWidth,
            Height = converted.PixelHeight,
            PixelFormat = CameraPixelFormat.Bgra32,
            Data = bytes
        };
        _ = OnFrameAsync(frameObj);
    }
}
#endif
