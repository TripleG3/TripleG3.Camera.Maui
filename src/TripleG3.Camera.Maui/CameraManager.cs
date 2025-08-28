namespace TripleG3.Camera.Maui;

/// <summary>
/// Cross-platform CameraManager abstraction.
/// </summary>
public abstract partial class CameraManager : ICameraManager, IAsyncDisposable
{
    public CameraInfo SelectedCamera { get; protected set; } = CameraInfo.Empty;
    public bool IsStreaming { get; protected set; }

    protected Func<CameraFrame, ValueTask> FrameCallback = _ => ValueTask.CompletedTask;
    protected readonly SemaphoreSlim SyncLock = new(1,1);

    public abstract ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default);
    public abstract ValueTask StartAsync(Func<CameraFrame, ValueTask> frameCallback, CancellationToken cancellationToken = default);
    public abstract ValueTask StopAsync(CancellationToken cancellationToken = default);

    protected virtual ValueTask OnFrameAsync(CameraFrame frame) => FrameCallback(frame);

    public virtual async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch { /* ignore */ }
        SyncLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public static CameraManager Create()
    {
#if ANDROID
        return new AndroidCameraManager();
#elif IOS
        return new IosCameraManager();
#elif WINDOWS
        return new WindowsCameraManager();
#else
        throw new PlatformNotSupportedException();
#endif
    }
}
