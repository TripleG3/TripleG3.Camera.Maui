#if WINDOWS
using Windows.Devices.Enumeration;

namespace TripleG3.Camera.Maui;

public static class DeviceInformationExtensions
{
    public static CameraInfo ToCameraInfo(this DeviceInformation deviceInformation) =>
        new(deviceInformation.Id, deviceInformation.Name, deviceInformation.ToCameraFacing());

    public static CameraFacing ToCameraFacing(this DeviceInformation deviceInformation) => deviceInformation.EnclosureLocation?.Panel switch
    {
        Panel.Front => CameraFacing.Front,
        Panel.Back => CameraFacing.Back,
        _ => CameraFacing.Unknown
    };
}

#endif
