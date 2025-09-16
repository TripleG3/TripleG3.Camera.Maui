using System.Collections.Concurrent;

namespace TripleG3.Camera.Maui;

public interface ICameraFrameBroadcaster : ICameraFrameSink
{
    Guid Subscribe(Action<CameraFrame> handler);
    void Unsubscribe(Guid token);
}

internal sealed class CameraFrameBroadcaster : ICameraFrameBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Action<CameraFrame>> _subs = new();
    private long _lastDeliveredTicks;
    public Guid Subscribe(Action<CameraFrame> handler)
    {
        var id = Guid.NewGuid();
        _subs[id] = handler;
        return id;
    }
    public void Unsubscribe(Guid token) => _subs.TryRemove(token, out _);

    // Simple throttle example: (optional) we can keep all frames for now.
    public void Submit(CameraFrame frame)
    {
        _lastDeliveredTicks = frame.TimestampTicks;
        foreach (var kvp in _subs)
        {
            try { kvp.Value(frame); } catch { }
        }
    }
}
