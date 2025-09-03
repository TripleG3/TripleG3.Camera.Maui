using TripleG3.Skeye.Models.Abstractions;

namespace TripleG3.Camera.Maui.ManualTestApp.Services;

public sealed class LocalBroadcastState : IBroadcastState
{
    public bool IsBroadcasting { get; private set; }
    public string? CurrentPrompt { get; private set; }
    public event EventHandler? BroadcastStateChanged;

    public Task StartBroadcastAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (IsBroadcasting) return Task.CompletedTask;
        IsBroadcasting = true;
        CurrentPrompt = prompt;
        BroadcastStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task StopBroadcastAsync(CancellationToken cancellationToken = default)
    {
        if (!IsBroadcasting) return Task.CompletedTask;
        IsBroadcasting = false;
        CurrentPrompt = null;
        BroadcastStateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
