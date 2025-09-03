using TripleG3.Skeye.Models.Abstractions;

namespace TripleG3.Camera.Maui.ManualTestApp.Services;

public sealed class LocalUserContext : IUserContext
{
    private const string UserIdKey = "manual_user_id";
    private static string? _cached;
    public Task<string> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_cached)) return Task.FromResult(_cached!);
        // Simplified in-memory id (no Preferences dependency for manual test)
        _cached = Guid.NewGuid().ToString("N");
        return Task.FromResult(_cached!);
    }
}
