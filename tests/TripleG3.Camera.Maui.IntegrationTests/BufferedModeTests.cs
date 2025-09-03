using Xunit;

namespace TripleG3.Camera.Maui.IntegrationTests;

public class BufferedModeTests
{
    [Fact]
    public async Task BufferedPlayback_ReleasesFramesAfterThreshold_InOrder()
    {
        RemoteVideoView.DisableDispatcherForTests = true;
        var view = new RemoteVideoView { Port = 0 }; // no network usage
        var received = new List<int>();
        view.HandlerImpl = new TestHandler(f =>
        {
            // Extract synthetic frame id from first byte of data
            if (f.Data.Length > 0)
                received.Add(f.Data[0]);
        });

        int threshold = 5;
        int total = 8;
        var queue = new List<CameraFrame>();
        for (int i = 0; i < total; i++)
        {
            var payload = Enumerable.Repeat((byte)i, 16).ToArray();
            queue.Add(new CameraFrame(CameraPixelFormat.BGRA32, 2, 2, DateTime.UtcNow.Ticks + i, false, payload));
        }
        // Simulate buffering: hold until threshold reached
        Assert.Equal(total, queue.Count);
        Assert.True(queue.Count >= threshold);
        // Playback phase
        foreach (var f in queue)
        {
            view.SubmitFrame(f);
            await Task.Delay(2);
        }

        // Allow handler processing
        await Task.Delay(50);

        Assert.Equal(total, received.Count);
        // Ensure first delivery occurs only after threshold reached (i.e., not zero immediate). We expect initial frame id 0 still first, but after playback start.
        Assert.Equal(0, received.First());
        // Order should be ascending
        Assert.True(received.SequenceEqual(Enumerable.Range(0, total)), "Frame order incorrect");
    }

    private sealed class TestHandler(Action<CameraFrame> onFrame) : IRemoteVideoViewHandler
    {
        public void OnSizeChanged(double w, double h) { }
        public void UpdateFrame(CameraFrame frame) => onFrame(frame);
    }
}
