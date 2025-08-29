#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace TripleG3.Camera.Maui;

public sealed class WindowsCameraManager(TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived) : CameraManager
{
    private MediaStreamer? mediaStreamer;
    public override async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncLock.WaitAsync(cancellationToken);
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken);
            CameraInfos = [.. devices.Select(d => new CameraInfo(d.Id, d.Name, d.EnclosureLocation?.Panel switch
            {
                Panel.Front => CameraFacing.Front,
                Panel.Back => CameraFacing.Back,
                _ => CameraFacing.Unknown
            }))];
            if (SelectedCamera == CameraInfo.Empty && CameraInfos.Count > 0)
                SelectedCamera = CameraInfos[0];
            if (SelectedCamera == CameraInfo.Empty)
                return;
            if (mediaStreamer != null)
                await mediaStreamer.DisposeAsync();
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
        if (IsStreaming)
            await StopAsync(cancellationToken);
        SelectedCamera = cameraInfo;
    }

    public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
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
        if (mediaStreamer == null)
            return;
        await SyncLock.WaitAsync(cancellationToken);
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

    public override async Task CleanupAsync()
    {
        if (mediaStreamer != null)
            await mediaStreamer.DisposeAsync();
        SelectedCamera = CameraInfo.Empty;
        mediaStreamer = null;
    }
}

#endif
