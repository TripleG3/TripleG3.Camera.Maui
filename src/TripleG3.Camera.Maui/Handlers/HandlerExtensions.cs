using Microsoft.Maui.Hosting;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Handlers;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
#if IOS || MACCATALYST
using UIKit;
using CoreFoundation;
#endif

namespace TripleG3.Camera.Maui;

/// <summary>
/// Extension methods to register TripleG3 camera / remote video handlers across platforms.
/// Call builder.UseTripleG3Camera() inside MauiProgram.CreateMauiApp.
/// </summary>
public static class HandlerExtensions
{
    /// <summary>
    /// Registers platform handlers for <see cref="CameraView"/> and <see cref="RemoteVideoView"/>.
    /// Also wires supporting singletons (e.g. frame broadcaster).
    /// </summary>
    public static MauiAppBuilder UseTripleG3Camera(this MauiAppBuilder builder)
    {
        builder.ConfigureMauiHandlers(h => h.AddTripleG3CameraHandlers());
        // Frame broadcaster (used by platform handlers to publish frames to interested subscribers).
        builder.Services.AddSingleton<ICameraFrameBroadcaster, CameraFrameBroadcaster>();
        return builder;
    }

    /// <summary>
    /// Low-level handler registration (exposed separately in case a host wants manual composition).
    /// </summary>
    public static IMauiHandlersCollection AddTripleG3CameraHandlers(this IMauiHandlersCollection handlers)
    {
#if ANDROID
        handlers.AddHandler(typeof(CameraView), typeof(AndroidCameraViewHandler));
        handlers.AddHandler(typeof(RemoteVideoView), typeof(AndroidRemoteVideoViewHandler));
#elif WINDOWS
        handlers.AddHandler(typeof(CameraView), typeof(WindowsCameraViewHandler));
        handlers.AddHandler(typeof(RemoteVideoView), typeof(WindowsRemoteVideoViewHandler));
#elif IOS || MACCATALYST
        handlers.AddHandler(typeof(CameraView), typeof(AppleCameraViewHandler));
        handlers.AddHandler(typeof(RemoteVideoView), typeof(AppleRemoteVideoViewHandler));
#endif
        return handlers;
    }
}

#if IOS || MACCATALYST
sealed class AppleCameraViewHandler : ViewHandler<CameraView, UIView>, INewCameraViewHandler
{
    public AppleCameraViewHandler() : base(new PropertyMapper<CameraView, AppleCameraViewHandler>(ViewHandler.ViewMapper)) { }
    protected override UIView CreatePlatformView()
    {
        var v = new UIView { BackgroundColor = UIColor.Black };
        VirtualView.NewCameraViewHandler = this;
        return v;
    }
    public bool IsRunning => false;
    public void OnCameraIdChanged(string? cameraId) { }
    public void OnHeightChanged(double height) { }
    public void OnWidthChanged(double height) { }
    public void OnMirrorChanged(bool isMirrored) { }
    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class AppleRemoteVideoViewHandler : ViewHandler<RemoteVideoView, UIView>, IRemoteVideoViewHandler
{
    readonly object _gate = new();
    byte[]? _latest; int _w; int _h;
    public AppleRemoteVideoViewHandler() : base(new PropertyMapper<RemoteVideoView, AppleRemoteVideoViewHandler>(ViewHandler.ViewMapper)) { }
    protected override UIView CreatePlatformView()
    {
        var v = new UIView { BackgroundColor = UIColor.Black };
        VirtualView.HandlerImpl = this;
        return v;
    }
    public void OnSizeChanged(double w, double h) => Redraw();
    public void UpdateFrame(CameraFrame frame)
    {
        // Placeholder: store latest frame; real implementation would render via CoreAnimation / Metal / CoreGraphics.
        if (frame.Format == CameraPixelFormat.BGRA32)
        {
            lock (_gate)
            {
                _latest = frame.Data; _w = frame.Width; _h = frame.Height;
            }
            Redraw();
        }
        else if (frame.Format == CameraPixelFormat.YUV420)
        {
            // Convert using existing helper (extension lives on handlers) if available.
            var rgba = new byte[frame.Width * frame.Height * 4];
            I420ToBGRA(frame.Data, frame.Width, frame.Height, rgba);
            lock (_gate) { _latest = rgba; _w = frame.Width; _h = frame.Height; }
            Redraw();
        }
    }

    static void I420ToBGRA(byte[] src, int width, int height, byte[] dest)
    {
        int yPlaneSize = width * height;
        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int uPlaneOffset = yPlaneSize;
        int vPlaneOffset = uPlaneOffset + uvWidth * uvHeight;
        int di = 0;
        for (int y = 0; y < height; y++)
        {
            int uvRow = y / 2;
            for (int x = 0; x < width; x++)
            {
                int yIndex = y * width + x;
                int uvCol = x / 2;
                int uIndex = uPlaneOffset + uvRow * uvWidth + uvCol;
                int vIndex = vPlaneOffset + uvRow * uvWidth + uvCol;
                int Y = src[yIndex];
                int U = src[uIndex] - 128;
                int V = src[vIndex] - 128;
                int c = Y - 16; if (c < 0) c = 0;
                int d = U;
                int e = V;
                int R = (298 * c + 409 * e + 128) >> 8;
                int G = (298 * c - 100 * d - 208 * e + 128) >> 8;
                int B = (298 * c + 516 * d + 128) >> 8;
                if (R < 0) R = 0; else if (R > 255) R = 255;
                if (G < 0) G = 0; else if (G > 255) G = 255;
                if (B < 0) B = 0; else if (B > 255) B = 255;
                dest[di++] = (byte)B;
                dest[di++] = (byte)G;
                dest[di++] = (byte)R;
                dest[di++] = 255;
            }
        }
    }

    void Redraw()
    {
        var view = PlatformView; if (view == null) return;
        // For now just flash background when a frame arrives to show activity during development.
        if (_latest != null)
        {
            view.BackgroundColor = UIColor.DarkGray;
            DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, 30_000_000), () =>
            {
                if (view != null) view.BackgroundColor = UIColor.Black;
            });
        }
    }
}
#endif
