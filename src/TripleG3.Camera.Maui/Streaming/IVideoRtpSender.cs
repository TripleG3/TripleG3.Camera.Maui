namespace TripleG3.Camera.Maui.Streaming;

/// <summary>
/// Abstraction for sending encoded video access units via RTP/UDP (future TripleG3.P2P integration).
/// This is a stub until TripleG3.P2P exposes the necessary low-level channel APIs.
/// </summary>
public interface IVideoRtpSender
{
    /// <summary>
    /// Initialize / negotiate session (codec, resolution, bitrate, sps/pps) - stubbed.
    /// </summary>
    Task InitializeAsync(VideoRtpSessionConfig config, CancellationToken ct = default);

    /// <summary>
    /// Enqueue raw camera frame for encoding+send. For phase 0 we send uncompressed placeholder.
    /// </summary>
    void SubmitRawFrame(CameraFrame frame);

    /// <summary>
    /// Request a keyframe from encoder (once hardware encoder integrated).
    /// </summary>
    void RequestKeyFrame();
}

public record VideoRtpSessionConfig(int Width, int Height, int Fps, int Bitrate, string Codec = "H264");
