namespace TripleG3.Camera.Maui;

/// <summary>
/// Camera description.
/// </summary>
public sealed record CameraInfo(
    string Id,
    string Name,
    CameraFacing Facing,
    bool IsExternal
);
