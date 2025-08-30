using Microsoft.Extensions.Logging;

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
#endif
            });

    // Services
    builder.Services.AddSingleton<ICameraService, CameraService>();

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
