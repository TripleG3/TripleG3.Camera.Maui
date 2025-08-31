using System.Net.Sockets;
using System.Buffers.Binary;
using TripleG3.Camera.Maui;
using Xunit;

namespace TripleG3.Camera.Maui.IntegrationTests;

public class RemoteVideoViewTests
{
    static byte[] SerializeFrame(CameraFrame frame)
    {
        var buf = new byte[1 + 4 + 4 + 8 + 1 + 4 + frame.Data.Length];
        int o = 0;
        buf[o++] = (byte)frame.Format;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), frame.Width); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), frame.Height); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(o), frame.TimestampTicks); o += 8;
        buf[o++] = (byte)(frame.Mirrored ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), frame.Data.Length); o += 4;
        frame.Data.CopyTo(buf, o);
        return buf;
    }

    [Theory]
    [InlineData(RemoteVideoProtocol.UDP)]
    [InlineData(RemoteVideoProtocol.TCP)]
    public async Task RemoteVideoView_ReceivesFrame(RemoteVideoProtocol protocol)
    {
        RemoteVideoView.DisableDispatcherForTests = true;
        var width = 2;
        var height = 2;
        var pixelData = new byte[width * height * 4];
        for (int i = 0; i < pixelData.Length; i++) pixelData[i] = (byte)(i * 17);
        var frame = new CameraFrame(CameraPixelFormat.BGRA32, width, height, DateTime.UtcNow.Ticks, Mirrored: false, pixelData);

        int port = GetFreePort();
        var view = new RemoteVideoView { Port = port, Protocol = protocol };
        var captured = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.HandlerImpl = new TestHandler(f => captured.TrySetResult(f));

        // Force start receiver
        view.IpAddress = protocol == RemoteVideoProtocol.TCP ? "127.0.0.1" : null;
        // Allow receiver startup
        await Task.Delay(100);

        if (protocol == RemoteVideoProtocol.UDP)
        {
            using var udp = new UdpClient();
            var data = SerializeFrame(frame);
            await udp.SendAsync(data, data.Length, "127.0.0.1", port);
        }
        else
        {
            // Start server that sends exactly one frame then closes
            var serverTask = Task.Run(async () =>
            {
                var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                using var serverClient = await listener.AcceptTcpClientAsync();
                using var serverStream = serverClient.GetStream();
                var data = SerializeFrame(frame);
                await serverStream.WriteAsync(data);
                await serverStream.FlushAsync();
                await Task.Delay(50);
                listener.Stop();
            });
            // Allow server to bind
            await Task.Delay(50);
            // Trigger client connect by ensuring IpAddress already set (done above)
            await serverTask;
        }

        var received = await Task.WhenAny(captured.Task, Task.Delay(2000));
        Assert.True(received == captured.Task, "Frame not received in time");
        var rf = await captured.Task;
        Assert.Equal(frame.Width, rf.Width);
        Assert.Equal(frame.Height, rf.Height);
        Assert.Equal(frame.Format, rf.Format);
        Assert.Equal(frame.Data.Length, rf.Data.Length);
        Assert.True(rf.Data.SequenceEqual(frame.Data));
    }

    int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TestHandler : IRemoteVideoViewHandler
    {
        readonly Action<CameraFrame> _onFrame;
        public TestHandler(Action<CameraFrame> onFrame) => _onFrame = onFrame;
        public void OnSizeChanged(double w, double h) { }
        public void UpdateFrame(CameraFrame frame) => _onFrame(frame);
    }
}
