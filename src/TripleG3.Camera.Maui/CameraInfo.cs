namespace TripleG3.Camera.Maui;

/// <summary>
/// Camera description.
/// </summary>
public sealed record CameraInfo(string Id, string Name, CameraFacing Facing, bool IsExternal)
{ 
    public static CameraInfo Empty { get; } = new CameraInfo(string.Empty, string.Empty, CameraFacing.Unknown, false);
}

