using System.Collections.Concurrent;

namespace TripleG3.Camera.Maui;

internal static class CameraLifecycleManager
{
    static readonly ConcurrentDictionary<INewCameraViewHandler, byte> _handlers = new();
    static int _initialized;

    public static void Register(INewCameraViewHandler handler)
    {
        _handlers[handler] = 0;
        EnsureAppHook();
    }

    public static void Unregister(INewCameraViewHandler handler)
    {
        _handlers.TryRemove(handler, out _);
    }

    static void EnsureAppHook()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        var app = Application.Current;
        if (app != null)
        {
            app.HandlerChanging += AppOnHandlerChanging; // lifecycle teardown
        }
    }

    static async void AppOnHandlerChanging(object? sender, HandlerChangingEventArgs e)
    {
        if (e.NewHandler == null) // app is being torn down
        {
            await ShutdownAllAsync();
        }
    }

    public static async Task ShutdownAllAsync()
    {
        var list = _handlers.Keys.ToArray();
        foreach (var h in list)
        {
            try { await h.StopAsync(); } catch { }
            try { await h.DisposeAsync(); } catch { }
        }
        _handlers.Clear();
    }
}
