using System.Buffers;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using TripleG3.P2P.Video;

namespace TripleG3.Camera.Maui.Streaming;

/// <summary>
/// Direct adapter over TripleG3.P2P.Video RtpVideoSender.
/// Still synthesizes a fake IDR NAL from raw frame bytes until real encoding integrated.
/// </summary>
internal sealed class VideoRtpSenderStub : IVideoRtpSender, IDisposable
{
    readonly string _remoteHost;
    readonly int _remotePort;
    UdpClient? _udp;
    uint _timestampBase;
    VideoRtpSessionConfig? _config;
    RtpVideoSender? _sender;
    Timer? _srTimer;

    public VideoRtpSenderStub(string host, int port)
    {
        _remoteHost = host;
        _remotePort = port;
    }

    public Task InitializeAsync(VideoRtpSessionConfig config, CancellationToken ct = default)
    {
        _config = config;
        _udp = new UdpClient();
        _timestampBase = (uint)Environment.TickCount;
        _sender = new RtpVideoSender(
            ssrc: (uint)Random.Shared.Next(),
            mtu: 1200,
            cipher: new NoOpCipher(),
            datagramOut: d => { try { _udp?.Send(d.ToArray(), d.Length, _remoteHost, _remotePort); } catch { } },
            rtcpOut: d => { try { _udp?.Send(d.ToArray(), d.Length, _remoteHost, _remotePort); } catch { } }
        );
        _srTimer = new Timer(_ =>
        {
            try
            {
                var nowTicks = DateTime.UtcNow.Ticks;
                var ts90k = _timestampBase + (uint)(((nowTicks) / TimeSpan.TicksPerMillisecond) * 90);
                _sender.SendSenderReport(ts90k);
            }
            catch { }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public void SubmitRawFrame(CameraFrame frame)
    {
        if (_config == null || _sender == null) return;
        // slight initial delay heuristic if timestamp base not yet established
        if (_timestampBase == 0)
        {
            _timestampBase = (uint)Environment.TickCount;
        }
        var raw = frame.Data;
        // Layout: AnnexB start(4) + NAL(1) + width(int32) + height(int32) + timestampTicks(int64) + raw pixels
        var annexB = ArrayPool<byte>.Shared.Rent(raw.Length + 5 + 4 + 4 + 8);
        try
        {
            int o = 0;
            annexB[o++] = 0; annexB[o++] = 0; annexB[o++] = 0; annexB[o++] = 1; annexB[o++] = 0x65; // fake IDR NAL header
            BitConverter.GetBytes(frame.Width).CopyTo(annexB, o); o += 4;
            BitConverter.GetBytes(frame.Height).CopyTo(annexB, o); o += 4;
            BitConverter.GetBytes(frame.TimestampTicks).CopyTo(annexB, o); o += 8;
            Buffer.BlockCopy(raw, 0, annexB, o, raw.Length);
            uint ts = _timestampBase + (uint)(((frame.TimestampTicks) / TimeSpan.TicksPerMillisecond) * 90);
            using var au = new EncodedAccessUnit(new ReadOnlyMemory<byte>(annexB, 0, raw.Length + 5 + 4 + 4 + 8), true, ts, frame.TimestampTicks);
            _sender.Send(au);
        }
        catch { }
        finally { ArrayPool<byte>.Shared.Return(annexB); }
    }

    public void RequestKeyFrame() { /* keyframe request path not yet wired */ }

    public void Dispose()
    {
    try { _srTimer?.Dispose(); } catch { }
    try { _udp?.Dispose(); } catch { }
    try { _sender?.Dispose(); } catch { }
    }
}
