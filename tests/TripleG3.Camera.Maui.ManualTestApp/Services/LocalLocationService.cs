using TripleG3.Skeye.Models.Abstractions;

namespace TripleG3.Camera.Maui.ManualTestApp.Services;

public sealed class LocalLocationService : ILocationService
{
    public Task<(double latitude, double longitude)?> GetCurrentLocationAsync(CancellationToken cancellationToken = default)
    {
        // Return a fixed coordinate for manual test harness (Seattle)
        return Task.FromResult<(double, double)?>( (47.6062, -122.3321) );
    }
}
