#if IOS
namespace TripleG3.Camera.Maui;

internal sealed class IosCameraManager : CameraManager
{
    public override ValueTask SelectCameraAsync(CameraInfo cameraInfo, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
#endif
