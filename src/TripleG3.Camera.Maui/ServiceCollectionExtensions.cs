namespace TripleG3.Camera.Maui;

/// <summary>
/// Service registration helpers for the Camera MAUI library.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core camera services (enumeration + in-process frame broadcaster).
    /// Does not register any network / RTP components (see TripleG3.Camera.Maui.Streaming for that).
    /// </summary>
    public static IServiceCollection AddCameraMaui(this IServiceCollection services)
    {
        services.AddSingleton<ICameraService, CameraService>();
        services.AddSingleton<ICameraFrameBroadcaster, CameraFrameBroadcaster>();
        return services;
    }
}
