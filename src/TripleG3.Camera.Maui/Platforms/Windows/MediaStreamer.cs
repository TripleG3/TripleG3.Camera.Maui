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
    private bool isStreaming;
    private bool isDisposed;

    public event Action<bool> IsStreamingChanged = delegate { };
    public static async Task<MediaStreamer> CreateAsync(TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived, Action<MediaCaptureInitializationSettings> configureSettings)
    {
        var settings = new MediaCaptureInitializationSettings();
        configureSettings(settings);
        var mediaCapture = new MediaCapture();
        await mediaCapture.InitializeAsync(settings);
        var mediaFrameSource = await SelectFrameSourceAsync(mediaCapture);
        var mediaFrameReader = await mediaCapture.CreateFrameReaderAsync(mediaFrameSource);
        mediaFrameReader.FrameArrived += frameReceived;

        return new MediaStreamer(mediaFrameReader, frameReceived);
    }

    private MediaStreamer(MediaFrameReader mediaFrameReader, TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameReceived)
    {
        this.mediaFrameReader = mediaFrameReader;
        this.frameReceived = frameReceived;
    }

    public bool IsStreaming
    {
        get => isStreaming;
        private set
        {
            if (isStreaming == value) return;
            isStreaming = value;
            IsStreamingChanged(isStreaming);
        }
    }

    public IAsyncOperation<MediaFrameReaderStartStatus> StartAsync()
    {
        ObjectDisposedException.ThrowIf(isDisposed, nameof(MediaStreamer));
        IsStreaming = true;
        return mediaFrameReader.StartAsync();
    }

    public IAsyncAction StopAsync()
    {
        ObjectDisposedException.ThrowIf(isDisposed, nameof(MediaStreamer));
        var result = mediaFrameReader.StopAsync();
        IsStreaming = false;
        return result;
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

    public async ValueTask DisposeAsync()
    {
        if (isDisposed) return;
        isDisposed = true;
        if (mediaFrameReader != null)
        {
            mediaFrameReader.FrameArrived -= frameReceived;
            try { await mediaFrameReader.StopAsync(); } catch { }
            mediaFrameReader.Dispose();
        }
    }
}

#endif
