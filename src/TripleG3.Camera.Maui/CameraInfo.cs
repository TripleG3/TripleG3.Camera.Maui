namespace TripleG3.Camera.Maui;

/// <summary>
/// Camera description.
/// </summary>
public sealed record CameraInfo(string Id, string Name, CameraFacing CameraFacing)
{ 
    public static CameraInfo Empty { get; } = new CameraInfo(string.Empty, string.Empty, CameraFacing.Unknown);
    public override string ToString() => Name;
}