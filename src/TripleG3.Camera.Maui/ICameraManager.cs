using System.Collections.Immutable;

namespace TripleG3.Camera.Maui;

public interface ICameraManager
{
    ValueTask LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SelectCameraAsync(CameraInfo cameraInfo, CancellationToken cancellationToken = default);
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    ValueTask StopAsync(CancellationToken cancellationToken = default);
    CameraInfo SelectedCamera { get; }
    ImmutableList<CameraInfo> CameraInfos { get; }
    bool IsStreaming { get; }
}
