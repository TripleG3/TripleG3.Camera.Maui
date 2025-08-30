using System.Collections.Immutable;

namespace TripleG3.Camera.Maui;

/// <summary>
/// Cross-platform CameraManager abstraction.
/// </summary>
public abstract partial class CameraManager : ICameraManager
{
    protected readonly SemaphoreSlim SyncLock = new(1, 1);
    private ImmutableList<CameraInfo> cameraInfos = [];
    private CameraInfo selectedCamera = CameraInfo.Empty;
    private bool isStreaming;

    public event Action<ImmutableList<CameraInfo>> CameraInfosChanged = delegate { };
    public event Action<CameraInfo> SelectedCameraChanged = delegate { };
    public event Action<bool> IsStreamingChanged = delegate { };
    public event Action<CameraInfo> CameraInfoAdded = delegate { };
    public event Action<CameraInfo> CameraInfoRemoved = delegate { };
    public CameraInfo SelectedCamera
    {
        get => selectedCamera;
        protected set
        {
            if (selectedCamera == value)
                return;
            selectedCamera = value;
            SelectedCameraChanged.Invoke(selectedCamera);
        }
    }
    public ImmutableList<CameraInfo> CameraInfos
    {
        get => cameraInfos;
        protected set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (cameraInfos == value)
                return;
            cameraInfos = value;
            CameraInfosChanged.Invoke(cameraInfos);
        }
    }
    public bool IsStreaming
    {
        get => isStreaming;
        protected set
        {
            if (isStreaming == value)
                return;
            isStreaming = value;
            IsStreamingChanged(isStreaming);
        }
    }
    public bool IsDisposed { get; private set; }

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

    public abstract ValueTask SelectCameraAsync(CameraInfo cameraInfo, CancellationToken cancellationToken = default);
    public abstract ValueTask StartAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask StopAsync(CancellationToken cancellationToken = default);
    public virtual ValueTask DisposeAsync()
    {
        IsDisposed = true;
        IsStreaming = false;
        CameraInfos = [];
        SelectedCamera = CameraInfo.Empty;
        SyncLock.Dispose();
        return ValueTask.CompletedTask;
    }

    protected void OnCameraInfoAdded(CameraInfo added) => CameraInfoAdded(added);
    protected void OnCameraInfoRemoved(CameraInfo existing) => CameraInfoRemoved(existing);

    ~CameraManager()
    {
        DisposeAsync().AsTask().Wait();
    }
}
