namespace ControlShift.Core.Profiles;

/// <summary>
/// Watches for game processes starting and stopping.
/// </summary>
public interface IProcessWatcher : IDisposable
{
    /// <summary>
    /// Starts watching for the specified executable names.
    /// Replaces any previous watch list.
    /// </summary>
    void StartWatching(IReadOnlyList<string> exeNames);

    /// <summary>Stops all active watches.</summary>
    void StopWatching();

    /// <summary>Whether the watcher is currently active.</summary>
    bool IsWatching { get; }

    /// <summary>Fires when a watched process starts.</summary>
    event EventHandler<ProcessEventArgs>? ProcessStarted;

    /// <summary>Fires when a watched process exits.</summary>
    event EventHandler<ProcessEventArgs>? ProcessStopped;
}

/// <summary>
/// Event data for process start/stop events.
/// </summary>
public sealed class ProcessEventArgs : EventArgs
{
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
}
