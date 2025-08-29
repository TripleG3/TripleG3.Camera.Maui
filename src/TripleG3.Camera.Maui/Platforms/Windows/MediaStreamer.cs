#if WINDOWS
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace TripleG3.Camera.Maui;

public sealed partial class MediaStreamer : IAsyncDisposable
{
    private readonly MediaFrameReader mediaFrameReader;
    private readonly TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived;
    private readonly MediaCapture mediaCapture;

    public static async Task<MediaStreamer> CreateAsync(TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived, Action<MediaCaptureInitializationSettings> configureSettings)
    {
        var settings = new MediaCaptureInitializationSettings();
        configureSettings(settings);
        var mediaCapture = new MediaCapture();
        await mediaCapture.InitializeAsync(settings);
        MediaFrameSource frameSource = await SelectFrameSourceAsync(mediaCapture);

        var mediaFrameReader = await mediaCapture.CreateFrameReaderAsync(frameSource);
        mediaFrameReader.FrameArrived += frameReceived;

        return new MediaStreamer(mediaFrameReader, frameReceived, mediaCapture);
    }

    private static async Task<MediaFrameSource> SelectFrameSourceAsync(MediaCapture mediaCapture)
    {
        var frameSource = mediaCapture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color) 
                       ?? mediaCapture.FrameSources.Values.First();

        // Prefer BGRA8 highest FPS <=1280x720
        var formats = frameSource.SupportedFormats.ToList();

        var bgra = formats.Where(f => f.Subtype == MediaEncodingSubtypes.Bgra8 && f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720)
                          .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                          .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                          .FirstOrDefault();

        var chosen = bgra ?? formats.Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720)
                                    .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator)
                                    .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                                    .FirstOrDefault();
        if (chosen != null)
        {
            try { await frameSource.SetFormatAsync(chosen); }
            catch { }
        }

        return frameSource;
    }

    private MediaStreamer(MediaFrameReader mediaFrameReader, TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived, MediaCapture mediaCapture)
    {
        this.mediaFrameReader = mediaFrameReader;
        this.frameReceived = frameReceived;
        this.mediaCapture = mediaCapture;
    }

    public bool IsStarted { get; private set; }
    public IAsyncOperation<MediaFrameReaderStartStatus> StartAsync()
    {
        IsStarted = true;
        return mediaFrameReader.StartAsync();
    }
    public IAsyncAction StopAsync()
    {
        var result = mediaFrameReader.StopAsync();
        IsStarted = false;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (mediaFrameReader != null)
        {
            try { await mediaFrameReader.StopAsync(); } catch { }
            mediaFrameReader.FrameArrived -= frameReceived;
            mediaFrameReader.Dispose();
        }
        mediaCapture?.Dispose();
    }
}

#endif
