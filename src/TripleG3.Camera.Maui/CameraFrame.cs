namespace TripleG3.Camera.Maui;

/// <summary>
/// A frame of image data.
/// </summary>
public sealed class CameraFrame
{
    public required string CameraId { get; init; }
    public required long TimestampUtcTicks { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required CameraPixelFormat PixelFormat { get; init; }
    public required byte[] Data { get; init; } // Raw buffer, caller interprets per PixelFormat
}
