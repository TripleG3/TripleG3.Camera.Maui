namespace TripleG3.Camera.Maui;

public interface ICameraManager
{
    ValueTask<IReadOnlyList<CameraInfo>> GetCamerasAsync(CancellationToken cancellationToken = default);
    ValueTask SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default);
    ValueTask StartAsync(Func<CameraFrame, ValueTask> frameCallback, CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
    // Never null. Use CameraInfo.Empty to represent 'no selection'.
    CameraInfo SelectedCamera { get; }
    bool IsStreaming { get; }
}
