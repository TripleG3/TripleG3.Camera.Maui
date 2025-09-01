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

### Planned Improvements

- Public `SubmitFrame(CameraFrame frame)` API on `RemoteVideoView`.
- Optional pixel format conversion utilities.
- Sample transport using `TripleG3.P2P`.

> Until those are added, treat `RemoteVideoView` as preview/experimental. API may change.

### Quick Start (Referencing From Another Project)

This assumes you consume the library from a separate MAUI app project (not the source repo).

1. Add the NuGet package reference (in your app `.csproj`):

    ```xml
    <ItemGroup>
      <PackageReference Include="TripleG3.Camera.Maui" Version="(latest)" />
    </ItemGroup>
    ```

1. Add the XML namespace in your XAML root element:

    ```xml
    xmlns:camera="clr-namespace:TripleG3.Camera.Maui;assembly=TripleG3.Camera.Maui"
    ```

1. Drop the view and configure network endpoint (UDP example listening on port 5005):

    ```xml
    <camera:RemoteVideoView WidthRequest="320"
                                    HeightRequest="240"
                                    IpAddress=""           <!-- blank = accept any source (UDP) -->
                                    Port="5005"
                                    Protocol="UDP" />
    ```

1. Start sending frames from a remote producer serialized with the documented wire format (see below). The view automatically begins a background receive loop when `Port` (and for TCP also `IpAddress`) are set.

For TCP you must specify `IpAddress` (the remote host) because it actively connects:

```xml
<camera:RemoteVideoView IpAddress="192.168.1.44"
                        Port="6000"
                        Protocol="TCP" />
```

### Wire Format (Current Prototype)

A single datagram (UDP) or framed record (TCP) contains one serialized frame:

```text
byte   Format                (enum CameraPixelFormat)
int32  Width (LE)
int32  Height (LE)
int64  TimestampTicks (LE)   (UTC or monotonic ticks – consumer treats as opaque)
byte   Mirrored (0/1)
int32  DataLength (LE)
byte[] PixelData (raw bytes in the declared pixel format)
```

Limits / Validation:

- `DataLength` must be > 0 and <= 64 MB.
- Entire payload for UDP must fit a single datagram (no fragmentation reassembly performed). For large frames prefer TCP or future RTP path.


### Minimal Sender Example (UDP)

```csharp
using System.Net.Sockets; using System.Buffers.Binary; using System.Net;

void SendFrame(CameraFrame frame, string host, int port)
{
    var data = frame.Data; // raw pixel bytes
    var buf = new byte[1 + 4 + 4 + 8 + 1 + 4 + data.Length];
    int o = 0;
    buf[o++] = (byte)frame.Format;
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), frame.Width); o += 4;
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), frame.Height); o += 4;
    BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(o), frame.TimestampTicks); o += 8;
    buf[o++] = (byte)(frame.Mirrored ? 1 : 0);
    BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), data.Length); o += 4;
    data.CopyTo(buf, o);
    using var udp = new UdpClient();
    udp.Send(buf, buf.Length, host, port);
}
```

### Manual Frame Injection (Loopback / Testing)

You can bypass networking entirely and drive the view directly:

```csharp
// Assume remoteView is x:Name of <camera:RemoteVideoView /> in XAML
var random = new Random();
var pixelData = new byte[320*240*4]; // BGRA32 example
random.NextBytes(pixelData);
var frame = new CameraFrame(CameraPixelFormat.BGRA32, 320, 240, DateTime.UtcNow.Ticks, mirrored:false, pixelData);
remoteView.SubmitFrame(frame); // Will marshal to UI thread when needed
```

This is useful for unit / integration tests. For headless tests you can set the static flag (internal scenarios) `RemoteVideoView.DisableDispatcherForTests = true;` to avoid UI dispatcher requirements.

### Middleware (Decryption / Transformation)

Implement `IRemoteVideoMiddleware` when you need to mutate or decrypt the raw serialized payload before the library parses it:

```csharp
public sealed class SimpleXorMiddleware : IRemoteVideoMiddleware
{
    readonly byte _key;
    public SimpleXorMiddleware(byte key) => _key = key;
    public byte[] Process(byte[] buffer)
    {
        var clone = new byte[buffer.Length];
        for (int i = 0; i < buffer.Length; i++) clone[i] = (byte)(buffer[i] ^ _key);
        return clone; // return empty array to drop frame
    }
}

// Usage (code-behind)
remoteView.Middleware = new SimpleXorMiddleware(0x5A);
```

The method must be fast and allocation-aware. Throwing or returning an empty array drops the frame silently.

### Threading Notes

- Network receive loop runs on a background task; frames are dispatched onto the UI thread via `Dispatcher.Dispatch`.
- `SubmitFrame` handles dispatch automatically; you can call it from any thread.

### Stability / Future Changes

- The current wire format is a stop-gap for early testing; once RTP receiver integration lands this path may be deprecated.
- `SubmitFrame` is expected to remain, but its visibility / parameters could evolve (e.g., accepting a span or encoded frame).
- Middleware interface may gain cancellation or span-based overloads for reduced allocations.

Track release notes for breaking changes before upgrading in production apps.

## RTP Video Streaming (Experimental)

Starting with version referencing `TripleG3.P2P` >= 1.1.15 this library integrates directly with the experimental real-time video RTP pipeline (no reflection fast‑path). A lightweight adapter (`VideoRtpSenderStub`) wraps `RtpVideoSender` and currently synthesizes a fake IDR NAL from raw BGRA/YUV frame bytes so end‑to‑end packetization/RTCP flows can be exercised before a real hardware/software H.264 encoder is plugged in.

### Current Capabilities

- Direct use of `RtpVideoSender` (H.264 Annex B access unit packetization, FU‑A fragmentation handled by TripleG3.P2P).
- Periodic RTCP Sender Reports (every 2s) for basic timing / future RTT stats.
- Synthetic access units built from raw frame data (NOT decodable video – placeholder for pipeline validation).

### Limitations (Planned Work)

- No real encoding yet (hardware encoder hook pending per-platform).
- No `RtpVideoReceiver` wiring into `RemoteVideoView` (next step).
- No keyframe request or negotiation manager integration (Offer/Answer TBD).
- No adaptive bitrate, loss recovery (NACK/FEC), or encryption (cipher currently `NoOpCipher`).

### Sample (Temporary Synthetic Sender)

```csharp
// In MauiProgram after services.AddRtpVideoStub(host, port) or equivalent registration
var sender = services.GetRequiredService<IVideoRtpSender>();
await sender.InitializeAsync(new VideoRtpSessionConfig(width:1280, height:720, Fps:30, Bitrate:2_000_000));

// Each camera frame (raw BGRA/YUV) forward to RTP (internally wrapped in synthetic Annex B AU)
_ = broadcaster.Subscribe(f => sender.SubmitRawFrame(f));
```
 
When a real encoder is added the synthetic wrapping will be removed and `SubmitRawFrame` will hand encoded Annex B frames to `RtpVideoSender` (or a new `SubmitEncodedFrame`).

### Planned Receiver Flow

1. UDP socket receives RTP/RTCP datagrams.
2. `RtpVideoReceiver.ProcessRtp/ProcessRtcp` reconstructs `EncodedAccessUnit`.
3. Decode (hardware) -> raw pixels -> `RemoteVideoView.SubmitFrame`.

### Migration Notes

If you were depending on the previous reflection fallback there is nothing to change; the direct path supersedes it. Once real encoding lands the synthetic IDR construction will be removed (a minor version bump will note the change).

## License

GPL-3.0-only. See `LICENSE` for details.

## Publishing

GitHub Actions workflow auto-builds & publishes the package when `main` is updated.
Set the repository secret `NUGET_API_KEY` with a NuGet.org API key.

## Roadmap

### Short‑term

- Real H.264 encoding (Android MediaCodec / Windows MF / platform equivalents) feeding `RtpVideoSender`.
- `RtpVideoReceiver` integration + decode path into `RemoteVideoView`.
- Public `RemoteVideoView.SubmitFrame` API surface stabilization.

### Mid‑term

- Negotiation manager (Offer/Answer + keyframe request) over TripleG3.P2P control channel.
- Keyframe request plumbing & on‑demand IDR generation.
- Basic stats overlay (bitrate, fps, loss, RTT).

### Longer‑term

- Adaptive bitrate / congestion control.
- Encryption (SRTP / secure cipher integration).
- iOS / MacCatalyst capture implementation.
- Diagnostics & metrics (latency, FPS, jitter).
