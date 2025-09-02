using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Reflection;

namespace TripleG3.Camera.Maui;

public enum RemoteVideoProtocol
{
    UDP,
    TCP,
    RTP // RTP over UDP using TripleG3.P2P synthetic AU format
}

public interface IRemoteVideoMiddleware
{
    /// <summary>
    /// Allows caller to decrypt / transform raw network buffer before frame parsing.
    /// Return the (possibly new) buffer to be parsed. Throwing will drop the frame.
    /// </summary>
    byte[] Process(byte[] buffer);
}

/// <summary>
/// View capable of rendering remote or manually fed <see cref="CameraFrame"/> instances.
/// Use <see cref="SubmitFrame(CameraFrame)"/> for direct injection (e.g., from a P2P decoder) or
/// configure an endpoint (IpAddress/Port/Protocol) for the built-in lightweight listeners.
/// </summary>
public sealed class RemoteVideoView : View, IDisposable
{
    internal IRemoteVideoViewHandler? HandlerImpl { get; set; }
    // For headless integration tests we bypass dispatcher marshalling.
    internal static bool DisableDispatcherForTests { get; set; }

    public static readonly BindableProperty IpAddressProperty = BindableProperty.Create(
        nameof(IpAddress), typeof(string), typeof(RemoteVideoView), default(string), propertyChanged: OnEndpointChanged);

    public static readonly BindableProperty PortProperty = BindableProperty.Create(
        nameof(Port), typeof(int), typeof(RemoteVideoView), 0, propertyChanged: OnEndpointChanged);

    public static readonly BindableProperty ProtocolProperty = BindableProperty.Create(
        nameof(Protocol), typeof(RemoteVideoProtocol), typeof(RemoteVideoView), RemoteVideoProtocol.UDP, propertyChanged: OnEndpointChanged);

    public static readonly BindableProperty MiddlewareProperty = BindableProperty.Create(
        nameof(Middleware), typeof(IRemoteVideoMiddleware), typeof(RemoteVideoView), default(IRemoteVideoMiddleware));

    public string? IpAddress
    {
        get => (string?)GetValue(IpAddressProperty);
        set => SetValue(IpAddressProperty, value);
    }
    public int Port
    {
        get => (int)GetValue(PortProperty);
        set => SetValue(PortProperty, value);
    }
    public RemoteVideoProtocol Protocol
    {
        get => (RemoteVideoProtocol)GetValue(ProtocolProperty);
        set => SetValue(ProtocolProperty, value);
    }
    public IRemoteVideoMiddleware? Middleware
    {
        get => (IRemoteVideoMiddleware?)GetValue(MiddlewareProperty);
        set => SetValue(MiddlewareProperty, value);
    }

    CancellationTokenSource? _cts;
    Task? _recvTask;
    readonly object _lifecycleGate = new();

    public RemoteVideoView()
    {
        Loaded += (_, _) => { HandlerImpl?.OnSizeChanged(Width, Height); TryStart(); };
        SizeChanged += (_, _) => HandlerImpl?.OnSizeChanged(Width, Height);
        Unloaded += (_, _) => Stop();
    }

    /// <summary>
    /// Manually supplies a frame (e.g. local loopback, external decoder pipeline).
    /// Thread-safe; marshals to UI thread if required.
    /// </summary>
    public void SubmitFrame(CameraFrame frame)
    {
        try
        {
            if (DisableDispatcherForTests || Dispatcher is null)
            {
                HandlerImpl?.UpdateFrame(frame);
            }
            else if (Dispatcher.IsDispatchRequired)
            {
                Dispatcher.Dispatch(() => HandlerImpl?.UpdateFrame(frame));
            }
            else
            {
                HandlerImpl?.UpdateFrame(frame);
            }
        }
        catch { }
    }

    static void OnEndpointChanged(BindableObject bindable, object? oldValue, object? newValue)
    {
        if (bindable is RemoteVideoView v) v.RestartIfRunning();
    }

    void RestartIfRunning()
    {
        lock (_lifecycleGate)
        {
            // Always restart when endpoint/protocol changes
            Stop_Internal();
            TryStart_Internal();
        }
    }

    void TryStart() => RestartIfRunning();

    void TryStart_Internal()
    {
        if (_recvTask != null) return;
        if (Port <= 0) return; // not configured
        // Allow empty IpAddress for passive UDP listen / TCP connect requirement
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _recvTask = Task.Run(() => ReceiverLoopAsync(token), token);
    }

    public void Stop()
    {
        lock (_lifecycleGate) Stop_Internal();
    }

    void Stop_Internal()
    {
        try { _cts?.Cancel(); } catch { }
        try { _recvTask?.Wait(250); } catch { }
        _cts?.Dispose(); _cts = null; _recvTask = null;
    }

    async Task ReceiverLoopAsync(CancellationToken ct)
    {
        try
        {
            if (Protocol == RemoteVideoProtocol.UDP)
            {
                using var udp = new UdpClient(Port);
                var filterIp = string.IsNullOrWhiteSpace(IpAddress) ? null : IPAddress.Parse(IpAddress!);
                while (!ct.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    if (filterIp != null && !result.RemoteEndPoint.Address.Equals(filterIp)) continue;
                    ProcessBuffer(result.Buffer);
                }
            }
            else if (Protocol == RemoteVideoProtocol.RTP)
            {
                using var udp = new UdpClient(Port);
                var filterIp = string.IsNullOrWhiteSpace(IpAddress) ? null : IPAddress.Parse(IpAddress!);
                using var rtp = new RtpReceiverAdapter(frame => SubmitFrame(frame));
                // Small readiness marker
                RemoteVideoViewDiagnostics.LastRtpFrameTicks = -1;
                while (!ct.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                    RemoteVideoViewDiagnostics.RtpPacketsReceived++;
                    if (filterIp != null && !result.RemoteEndPoint.Address.Equals(filterIp)) continue;
                    // Classify RTP vs RTCP (basic heuristic: RTCP PT 200-204). We'll feed both.
                    if (result.Buffer.Length >= 2 && (result.Buffer[0] >> 6) == 2)
                    {
                        var pt = (byte)(result.Buffer[1] & 0x7F);
                        if (pt >= 200 && pt <= 204)
                            rtp.ProcessRtcp(result.Buffer);
                        else
                            rtp.ProcessRtp(result.Buffer);
                    }
                }
            }
            else // TCP
            {
                if (string.IsNullOrWhiteSpace(IpAddress)) return; // need remote host for TCP
                using var client = new TcpClient();
                await client.ConnectAsync(IpAddress!, Port, ct).ConfigureAwait(false);
                using var stream = client.GetStream();
                var header = new byte[1 + 4 + 4 + 8 + 1 + 4];
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) break;
                    int offset = 0;
                    var format = (CameraPixelFormat)header[offset]; offset += 1;
                    int w = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset)); offset += 4;
                    int h = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset)); offset += 4;
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(offset)); offset += 8;
                    bool mirrored = header[offset] != 0; offset += 1;
                    int dataLen = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset)); offset += 4;
                    if (dataLen <= 0 || dataLen > 64 * 1024 * 1024) continue; // sanity
                    var data = new byte[dataLen];
                    if (!await ReadExactAsync(stream, data, ct).ConfigureAwait(false)) break;
                    var payload = BuildFrameBuffer(format, w, h, ticks, mirrored, data);
                    ProcessBuffer(payload);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (r == 0) return false;
            read += r;
        }
        return true;
    }

    static byte[] BuildFrameBuffer(CameraPixelFormat format, int w, int h, long ticks, bool mirrored, byte[] pixelData)
    {
        var buf = new byte[1 + 4 + 4 + 8 + 1 + 4 + pixelData.Length];
        int o = 0;
        buf[o++] = (byte)format;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), w); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), h); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(o), ticks); o += 8;
        buf[o++] = (byte)(mirrored ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), pixelData.Length); o += 4;
        pixelData.CopyTo(buf, o);
        return buf;
    }

    void ProcessBuffer(byte[] buffer)
    {
        try
        {
            if (Middleware != null)
            {
                buffer = Middleware.Process(buffer);
                if (buffer.Length == 0) return;
            }
            var frame = DeserializeFrame(buffer);
            if (frame.HasValue)
            {
                var f = frame.Value;
                SubmitFrame(f);
            }
        }
        catch { }
    }

    static CameraFrame? DeserializeFrame(byte[] buffer)
    {
        try
        {
            int o = 0;
            if (buffer.Length < 1 + 4 + 4 + 8 + 1 + 4) return null;
            var format = (CameraPixelFormat)buffer[o]; o += 1;
            int w = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(o)); o += 4;
            int h = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(o)); o += 4;
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(o)); o += 8;
            bool mirrored = buffer[o++] != 0;
            int dataLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(o)); o += 4;
            if (dataLen < 0 || o + dataLen > buffer.Length) return null;
            var data = new byte[dataLen];
            Buffer.BlockCopy(buffer, o, data, 0, dataLen);
            return new CameraFrame(format, w, h, ticks, mirrored, data);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

internal sealed class RtpReceiverAdapter : IDisposable
{
    private readonly TripleG3.P2P.Video.RtpVideoReceiver? _receiver;
    private readonly Action<CameraFrame> _onFrame;

    public RtpReceiverAdapter(Action<CameraFrame> onFrame)
    {
        _onFrame = onFrame;
        try
        {
            _receiver = new TripleG3.P2P.Video.RtpVideoReceiver(new TripleG3.P2P.Video.NoOpCipher());
            _receiver.AccessUnitReceived += au =>
            {
                try
                {
                    var memProp = au.GetType().GetProperty("Data") ?? au.GetType().GetProperty("Payload");
                    if (memProp == null) return;
                    var rom = (ReadOnlyMemory<byte>)memProp.GetValue(au)!;
                    if (rom.Length < 5 + 4 + 4 + 8) return;
                    var span = rom.Span;
                    int o = 5;
                    int w = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;
                    int h = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4)); o += 4;
                    long ticks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8)); o += 8;
                    var pixelLen = rom.Length - o;
                    var pixels = new byte[pixelLen];
                    span.Slice(o).CopyTo(pixels);
                    var frame = new CameraFrame(CameraPixelFormat.BGRA32, w, h, ticks, false, pixels);
                    _onFrame(frame);
                    RemoteVideoViewDiagnostics.LastRtpFrameTicks = ticks;
                }
                catch { }
            };
        }
        catch { }
    }

    public void ProcessRtp(byte[] datagram)
    {
        try { _receiver?.ProcessRtp(datagram); } catch { }
    }
    public void ProcessRtcp(byte[] datagram)
    {
        try { _receiver?.ProcessRtcp(datagram); } catch { }
    }
    public void Dispose()
    {
        try { _receiver?.Dispose(); } catch { }
    }
}

public static class RemoteVideoViewDiagnostics
{
    public static long LastRtpFrameTicks { get; set; }
    public static int RtpPacketsReceived { get; set; }
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
        lock (_gate) sinks = [.. _sinks];
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
