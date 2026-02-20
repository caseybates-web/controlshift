using System.Management;

namespace ControlShift.Core.Profiles;

/// <summary>
/// Watches for process start/stop events using WMI ETW trace queries.
/// </summary>
/// <remarks>
/// DECISION: Uses Win32_ProcessStartTrace / Win32_ProcessStopTrace which fire
/// at CreateProcess() time, giving us a ~2-5 second window before anticheat
/// drivers scan for virtual XInput devices. Requires admin elevation.
/// </remarks>
public sealed class WmiProcessWatcher : IProcessWatcher
{
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private readonly List<string> _watchedExeNames = new();

    public bool IsWatching => _startWatcher is not null;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public void StartWatching(IReadOnlyList<string> exeNames)
    {
        StopWatching();

        if (exeNames.Count == 0) return;

        _watchedExeNames.Clear();
        _watchedExeNames.AddRange(exeNames);

        try
        {
            // Build WQL WHERE clause for multiple exe names.
            // e.g.: ProcessName = 'game1.exe' OR ProcessName = 'game2.exe'
            string whereClause = string.Join(" OR ",
                exeNames.Select(e => $"ProcessName = '{EscapeWql(e)}'"));

            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery($"SELECT * FROM Win32_ProcessStartTrace WHERE {whereClause}"));
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery($"SELECT * FROM Win32_ProcessStopTrace WHERE {whereClause}"));
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();
        }
        catch
        {
            // WMI may fail if not running as admin or if the WMI service is down.
            // Fail silently — process watching is best-effort.
            StopWatching();
        }
    }

    public void StopWatching()
    {
        // Unsubscribe event handlers before stopping to prevent callbacks during cleanup.
        UnsubscribeEvents();
        CleanupWatcher(ref _startWatcher);
        CleanupWatcher(ref _stopWatcher);
        _watchedExeNames.Clear();
    }

    private void UnsubscribeEvents()
    {
        try { if (_startWatcher is not null) _startWatcher.EventArrived -= OnProcessStarted; } catch { /* best effort */ }
        try { if (_stopWatcher is not null)  _stopWatcher.EventArrived -= OnProcessStopped; } catch { /* best effort */ }
    }

    private static void CleanupWatcher(ref ManagementEventWatcher? watcher)
    {
        try { watcher?.Stop(); } catch { /* best effort */ }
        try { watcher?.Dispose(); } catch { /* best effort */ }
        watcher = null;
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e) =>
        RaiseProcessEvent(e, ProcessStarted);

    private void OnProcessStopped(object sender, EventArrivedEventArgs e) =>
        RaiseProcessEvent(e, ProcessStopped);

    private void RaiseProcessEvent(EventArrivedEventArgs e, EventHandler<ProcessEventArgs>? handler)
    {
        try
        {
            string? name = e.NewEvent["ProcessName"]?.ToString();
            int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
            if (name is null) return;

            handler?.Invoke(this, new ProcessEventArgs
            {
                ProcessName = name,
                ProcessId = pid,
            });
        }
        catch { /* event handlers must never throw */ }
    }

    /// <summary>
    /// Escapes single quotes in WQL strings to prevent injection.
    /// WQL uses doubled single-quotes ('') as the escape sequence.
    /// </summary>
    internal static string EscapeWql(string value) =>
        value.Replace("'", "''");

    public void Dispose()
    {
        StopWatching();
    }
}
