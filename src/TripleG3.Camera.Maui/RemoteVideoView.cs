namespace TripleG3.Camera.Maui;

public sealed class RemoteVideoView : View
{
    internal IRemoteVideoViewHandler? HandlerImpl { get; set; }
    public RemoteVideoView()
    {
        Loaded += (_, _) => HandlerImpl?.OnSizeChanged(Width, Height);
        SizeChanged += (_, _) => HandlerImpl?.OnSizeChanged(Width, Height);
    }
}

public interface IRemoteFrameDistributor
{
    void RegisterSink(Action<CameraFrame> sink);
    void UnregisterSink(Action<CameraFrame> sink);
    void Push(CameraFrame frame);
}

internal sealed class RemoteFrameDistributor : IRemoteFrameDistributor
{
    readonly object _gate = new();
    readonly List<Action<CameraFrame>> _sinks = new();
    public void RegisterSink(Action<CameraFrame> sink)
    {
        lock (_gate) _sinks.Add(sink);
    }
    public void UnregisterSink(Action<CameraFrame> sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }
    public void Push(CameraFrame frame)
    {
        Action<CameraFrame>[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var s in sinks)
        {
            try { s(frame); } catch { }
        }
    }
}

internal interface IRemoteVideoViewHandler
{
    void OnSizeChanged(double w, double h);
    void UpdateFrame(CameraFrame frame);
}
