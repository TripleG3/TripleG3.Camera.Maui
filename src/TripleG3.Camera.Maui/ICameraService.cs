namespace TripleG3.Camera.Maui;

/// <summary>
/// Describes a camera device that can be used for preview.
/// </summary>
/// <param name="Id">Platform specific unique device identifier.</param>
/// <param name="Name">Friendly display name.</param>
public sealed record CameraInfo(string Id, string Name)
{
    public override string ToString() => Name; // helpful for debugging / Picker fallback
}

public interface ICameraService
{
    /// <summary>
    /// Returns the list of available video capture devices (cameras).
    /// </summary>
    Task<IReadOnlyList<CameraInfo>> GetCamerasAsync();
}
