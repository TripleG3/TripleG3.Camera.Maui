#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace TripleG3.Camera.Maui;

public sealed class WindowsCameraManager : CameraManager
{
    private MediaStreamer mediaStreamer;
    private readonly TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived;
    private readonly DeviceWatcher deviceWatcher;

    public WindowsCameraManager(TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived)
    {
        this.frameReceived = frameReceived;
        deviceWatcher = DeviceInformation.CreateWatcher();
        deviceWatcher.Added += DeviceWatcher_Added;
        mediaStreamer = MediaStreamer.CreateAsync(frameReceived, settings =>
        {
            settings.StreamingCaptureMode = StreamingCaptureMode.Video;
            settings.MemoryPreference = MediaCaptureMemoryPreference.Cpu;
            settings.SharingMode = MediaCaptureSharingMode.ExclusiveControl;
            settings.VideoDeviceId = SelectedCamera.Id;
        }).Result;
        mediaStreamer.IsStreamingChanged += MediaStreamer_IsStreamingChanged;
    }

    public override async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (SelectedCamera == CameraInfo.Empty)
                return;
            if (IsStreaming)
                await StopAsync(cancellationToken);
            await SyncLock.WaitAsync(cancellationToken);
            mediaStreamer = await MediaStreamer.CreateAsync(frameReceived, settings =>
            {
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                settings.MemoryPreference = MediaCaptureMemoryPreference.Cpu;
                settings.SharingMode = MediaCaptureSharingMode.ExclusiveControl;
                settings.VideoDeviceId = SelectedCamera.Id;
            });
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public override async ValueTask SelectCameraAsync(CameraInfo cameraInfo, CancellationToken cancellationToken = default)
    {
        if (SelectedCamera == cameraInfo)
            return;
        var isCurrentlyStreaming = IsStreaming;
        if (IsStreaming)
            await StopAsync(cancellationToken);
        SelectedCamera = cameraInfo;
        if (isCurrentlyStreaming && SelectedCamera != CameraInfo.Empty)
            await StartAsync(cancellationToken);
    }

    public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsStreaming)
            await StopAsync(cancellationToken);
        await LoadAsync(cancellationToken);
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (mediaStreamer == null)
                throw new InvalidOperationException("MediaStreamer is not initialized.");
            await mediaStreamer.StartAsync();
            IsStreaming = true;
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public override async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        if (mediaStreamer == null)
            return;
        try
        {
            await mediaStreamer.StopAsync();
            await mediaStreamer.DisposeAsync();
            IsStreaming = false;
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private void MediaStreamer_IsStreamingChanged(bool isStreaming) => IsStreaming = isStreaming;

    private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
    {
        await SyncLock.WaitAsync();
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            CameraInfos = [.. devices.Select(d => new CameraInfo(d.Id, d.Name, d.EnclosureLocation?.Panel switch
            {
                Panel.Front => CameraFacing.Front,
                Panel.Back => CameraFacing.Back,
                _ => CameraFacing.Unknown
            }))];
            if (SelectedCamera == CameraInfo.Empty && CameraInfos.Count > 0)
                SelectedCamera = CameraInfos[0];
        }
        finally
        {
            SyncLock.Release();
        }
    }

    public override async Task CleanupAsync()
    {
        if (mediaStreamer != null)
            await mediaStreamer.DisposeAsync();
        SelectedCamera = CameraInfo.Empty;
    }
}

#endif
