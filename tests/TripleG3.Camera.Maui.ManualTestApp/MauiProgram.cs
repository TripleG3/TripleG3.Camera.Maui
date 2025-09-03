using TripleG3.Camera.Maui.Streaming;
using Microsoft.Extensions.Logging;
using TripleG3.Skeye.ViewModels; // view models only
using TripleG3.Skeye.Models.Abstractions;
using TripleG3.Camera.Maui.ManualTestApp.Services;

namespace TripleG3.Camera.Maui.ManualTestApp;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(h =>
            {
#if WINDOWS
                h.AddHandler<CameraView, WindowsCameraViewHandler>();
                h.AddHandler<RemoteVideoView, WindowsRemoteVideoViewHandler>();
#elif ANDROID
                h.AddHandler<CameraView, AndroidCameraViewHandler>();
                h.AddHandler<RemoteVideoView, AndroidRemoteVideoViewHandler>();
#endif
            });

        // Services
        builder.Services.AddSingleton<ICameraService, CameraService>();
        builder.Services.AddSingleton<ICameraFrameBroadcaster, CameraFrameBroadcaster>();
        // Temporary RTP stub registration (loopback localhost:50555 by default). Adjust as needed.
        builder.Services.AddRtpVideoStub("127.0.0.1", 50555);
        builder.Services.AddSingleton<IRemoteFrameDistributor, RemoteFrameDistributor>();

    // Simplified Skeye-related dependencies (local test doubles)
    builder.Services.AddSingleton<IUserContext, LocalUserContext>();
    builder.Services.AddSingleton<ILocationService, LocalLocationService>();
    builder.Services.AddSingleton<IBroadcastState, LocalBroadcastState>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

    var app = builder.Build();
    ServiceHelper.Services = app.Services; // expose for pages created via XAML (not constructor injected)
    return app;
    }
}

internal static class ServiceHelper
{
    public static IServiceProvider? Services { get; set; }
    public static T GetRequiredService<T>() where T : notnull => Services is null
    ? throw new InvalidOperationException("Services not initialized")
    : (T)Services.GetService(typeof(T))!;
}
