namespace TripleG3.Camera.Maui;

public enum CameraPixelFormat : byte
{
    BGRA32 = 1,
    YUV420 = 2 // Packed as planar I420: Y plane (W*H) then U (W/2*H/2) then V (W/2*H/2)
}

public readonly record struct CameraFrame(CameraPixelFormat Format,
                                          int Width,
                                          int Height,
                                          long TimestampTicks,
                                          bool Mirrored,
                                          byte[] Data);

public interface ICameraFrameSink
{
    void Submit(CameraFrame frame);
}
