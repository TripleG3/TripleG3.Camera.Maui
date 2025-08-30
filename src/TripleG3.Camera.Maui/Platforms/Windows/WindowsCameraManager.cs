#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace TripleG3.Camera.Maui;

public sealed class WindowsCameraManager : CameraManager
{
    private MediaStreamer? mediaStreamer;
    private readonly TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived;
    private DeviceWatcher? deviceWatcher;
    public WindowsCameraManager(TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived)
    {
        this.frameReceived = frameReceived;
        _ = InitializeManager(1000);
    }

    private async Task InitializeManager(int delay = 0)
    {
        if (delay > 0)
            await Task.Delay(delay);
        if (deviceWatcher != null)
        {
            deviceWatcher.Added -= DeviceWatcher_Added;
            deviceWatcher.Removed -= DeviceWatcher_Removed;
        }
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        if (devices != null)
        {
            CameraInfos = [.. devices.Select(device => new CameraInfo(device.Id, device.Name, device.EnclosureLocation?.Panel switch
            {
                Panel.Front => CameraFacing.Front,
                Panel.Back => CameraFacing.Back,
                _ => CameraFacing.Unknown
            }))];
            SelectedCamera = CameraInfos.Count > 0 ? CameraInfos[0] : CameraInfo.Empty;
        }
        deviceWatcher = DeviceInformation.CreateWatcher();
        deviceWatcher.Added += DeviceWatcher_Added;
        deviceWatcher.Removed += DeviceWatcher_Removed;
        deviceWatcher.Start();
        mediaStreamer = await MediaStreamer.CreateAsync(frameReceived, settings =>
        {
            settings.StreamingCaptureMode = StreamingCaptureMode.Video;
            settings.MemoryPreference = MediaCaptureMemoryPreference.Cpu;
            settings.SharingMode = MediaCaptureSharingMode.ExclusiveControl;
            settings.VideoDeviceId = SelectedCamera.Id;
        });
        mediaStreamer.IsStreamingChanged += MediaStreamer_IsStreamingChanged;
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
            return;
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (mediaStreamer!.IsDisposed)
                await InitializeManager();
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
        try
        {
            await mediaStreamer!.StopAsync();
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
        var device = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture)).FirstOrDefault(d => d.Id == args.Id);
        if (device == null)
            return;
        var cameraInfo = new CameraInfo(device.Id, device.Name, device.EnclosureLocation?.Panel switch
        {
            Panel.Front => CameraFacing.Front,
            Panel.Back => CameraFacing.Back,
            _ => CameraFacing.Unknown
        });
        CameraInfos = CameraInfos.Add(cameraInfo);
        OnCameraInfoAdded(cameraInfo);
        if (SelectedCamera == CameraInfo.Empty)
            SelectedCamera = cameraInfo;
    }

    private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        var existing = CameraInfos.Find(c => c.Id == args.Id);
        if (existing == null)
            return;
        CameraInfos = CameraInfos.Remove(existing);
        if (SelectedCamera == existing)
        {
            if (IsStreaming)
                await StopAsync();
            SelectedCamera = CameraInfos.Count > 0 ? CameraInfos[0] : CameraInfo.Empty;
        }
        OnCameraInfoRemoved(existing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (mediaStreamer != null)
        {
            mediaStreamer.IsStreamingChanged -= MediaStreamer_IsStreamingChanged;
            await mediaStreamer.DisposeAsync();
        }
        deviceWatcher!.Stop();
        deviceWatcher.Added -= DeviceWatcher_Added;
        deviceWatcher.Removed -= DeviceWatcher_Removed;
        await base.DisposeAsync();
    }
}

#endif
