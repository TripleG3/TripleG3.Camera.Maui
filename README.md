# TripleG3.Camera.Maui

Cross-platform .NET MAUI camera view control providing:

- Enumeration & auto-start of default camera
- Live preview with aspect-fill rendering (Android & Windows implemented)
- Frame abstraction (BGRA32 / YUV420) with in-process broadcaster
- Mirroring (default enabled)
- Live vs Buffered feed pipeline (delayed playback) and remote view scaffolding
- Remote frame rendering view (letterboxed fit currently)

> iOS / MacCatalyst capture handlers are placeholders; contributions welcome.

## Getting Started

Install from NuGet (after first publish):

```powershell
Install-Package TripleG3.Camera.Maui
```

In your `MauiProgram`:

```csharp
builder.UseMauiApp<App>()
       .ConfigureMauiHandlers(handlers =>
       {
           handlers.AddCameraViewHandler(); // extension you will expose or map
       });
```

Add the view in XAML:

```xml
<camera:CameraView WidthRequest="300" HeightRequest="400" />
```

## Remote Video View (Experimental)

The library includes an experimental `RemoteVideoView` for displaying frames received from another source (e.g., network / P2P). It renders `CameraFrame` objects pushed through an `IRemoteFrameDistributor`.

### Interfaces

```csharp
public interface IRemoteFrameDistributor
{
    void RegisterSink(Action<CameraFrame> sink);
    void UnregisterSink(Action<CameraFrame> sink);
    void Push(CameraFrame frame); // Call this when a new remote frame arrives
}
```

`RemoteVideoView` (simplified):

```csharp
public sealed class RemoteVideoView : View
{
    // Internal rendering handler; public feed API will be added in a later release.
}
```

Because the actual rendering hook is currently internal, the public surface for feeding frames will be expanded later. For now you can prototype by creating your own distributor and (inside the same assembly or a friend assembly) registering a sink that dispatches frames to the view's handler.

### XAML Usage

```xml
<camera:RemoteVideoView x:Name="RemoteView"
                        WidthRequest="300"
                        HeightRequest="400" />
```

### Basic Loopback Example (Show local frames in remote view)

If you already have a local broadcaster (e.g., from your camera pipeline) you can loop frames into the remote distributor to simulate a remote feed with buffering / mode switching.

```csharp
// Pseudo interfaces you likely already have:
public interface ICameraFrameBroadcaster
{
    IDisposable Subscribe(Action<CameraFrame> sink);
}

public sealed class LoopbackRemoteFrameDistributor : IRemoteFrameDistributor
{
    private readonly object _gate = new();
    private readonly List<Action<CameraFrame>> _sinks = new();
    public void RegisterSink(Action<CameraFrame> sink) { lock (_gate) _sinks.Add(sink); }
    public void UnregisterSink(Action<CameraFrame> sink) { lock (_gate) _sinks.Remove(sink); }
    public void Push(CameraFrame frame)
    {
        Action<CameraFrame>[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var s in sinks) { try { s(frame); } catch { } }
    }
}

// Wiring (e.g., in a page / view model):
var remoteDistributor = new LoopbackRemoteFrameDistributor();
IDisposable? sub = broadcaster.Subscribe(f => remoteDistributor.Push(f));

// Register remote view sink (internal API currently — future release will expose a public method):
remoteDistributor.RegisterSink(frame =>
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        // In current version the internal handler updates the drawing surface.
        // When a public API becomes available replace this with: RemoteView.SubmitFrame(frame);
    });
});
```

### Receiving Real Remote Frames

1. Receive bytes from network (e.g., via TripleG3.P2P or sockets).
2. Deserialize / reconstruct pixel buffer + metadata into a `CameraFrame` (matching expected formats BGRA32 or YUV420 planar).
3. Call `remoteDistributor.Push(frame)`.

### Planned Improvements

- Public `SubmitFrame(CameraFrame frame)` API on `RemoteVideoView`.
- Optional pixel format conversion utilities.
- Sample transport using `TripleG3.P2P`.

> Until those are added, treat `RemoteVideoView` as preview/experimental. API may change.

## License

GPL-3.0-only. See `LICENSE` for details.

## Publishing

GitHub Actions workflow auto-builds & publishes the package when `main` is updated.
Set the repository secret `NUGET_API_KEY` with a NuGet.org API key.

## Roadmap

- Network transport using TripleG3.P2P
- iOS / MacCatalyst implementation
- Encoding / compression & adaptive quality
- Diagnostics & metrics (latency, FPS)
