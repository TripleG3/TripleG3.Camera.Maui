namespace TripleG3.Camera.Maui.Streaming;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers stub RTP sender. Replace with real implementation later.
    /// </summary>
    public static IServiceCollection AddRtpVideoStub(this IServiceCollection services, string host, int port)
    {
        services.AddSingleton<IVideoRtpSender>(_ => new VideoRtpSenderStub(host, port));
        return services;
    }
}
