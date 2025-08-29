using System.Collections.Immutable;

namespace TripleG3.Camera.Maui;

/// <summary>
/// Cross-platform CameraManager abstraction.
/// </summary>
public abstract partial class CameraManager : ICameraManager, IAsyncDisposable
{
    protected readonly SemaphoreSlim SyncLock = new(1, 1);
    public CameraInfo SelectedCamera { get; protected set; } = CameraInfo.Empty;
    public ImmutableList<CameraInfo> CameraInfos { get; protected set; } = [];
    public bool IsStreaming { get; protected set; }

#if ANDROID
    public static CameraManager Create()
    {
        return new AndroidCameraManager();
    }
#endif

#if IOS
    public static CameraManager Create()
    {
        return new IosCameraManager();
    }
#endif

#if WINDOWS
    public static CameraManager Create(Windows.Foundation.TypedEventHandler<Windows.Media.Capture.Frames.MediaFrameReader, Windows.Media.Capture.Frames.MediaFrameArrivedEventArgs> typedEventHandler)
    {
        if (DeviceInfo.Platform == DevicePlatform.WinUI && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new WindowsCameraManager(typedEventHandler);
        throw new PlatformNotSupportedException("CameraManager is only supported on Windows 10 (2004) or later.");
    }
#endif

    public abstract ValueTask LoadAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask SelectCameraAsync(CameraInfo cameraInfo, CancellationToken cancellationToken = default);
    public abstract ValueTask StartAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask StopAsync(CancellationToken cancellationToken = default);
    public virtual async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* ignore */ }
        await CleanupAsync();
        SyncLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public abstract Task CleanupAsync();
}
