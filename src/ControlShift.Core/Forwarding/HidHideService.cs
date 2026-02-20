using Nefarius.Drivers.HidHide;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Wraps the HidHide driver API for hiding physical controllers from games.
/// Always call <see cref="ClearAll"/> on startup and in all exit paths (normal, crash, exception).
/// </summary>
public sealed class HidHideService : IDisposable
{
    private readonly HidHideControlService _svc;
    private bool _disposed;

    public HidHideService()
    {
        _svc = new HidHideControlService();
    }

    /// <summary>True if HidHide driver is installed and operational.</summary>
    public bool IsAvailable => _svc.IsInstalled;

    /// <summary>Driver version string, or null if not installed.</summary>
    public string? DriverVersion
    {
        get
        {
            try { return _svc.LocalDriverVersion?.ToString(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Adds the given executable to the HidHide allowlist so it can still read physical controllers.
    /// </summary>
    public void AddToAllowlist(string exePath)
    {
        _svc.AddApplicationPath(exePath, throwIfInvalid: false);
    }

    /// <summary>
    /// Hides a device by its instance ID (e.g. HID\VID_045E&amp;PID_02FF&amp;IG_00\...).
    /// </summary>
    public void HideDevice(string instanceId)
    {
        _svc.AddBlockedInstanceId(instanceId);
    }

    /// <summary>Enables device hiding globally.</summary>
    public void Enable()
    {
        _svc.IsActive = true;
    }

    /// <summary>Disables device hiding globally.</summary>
    public void Disable()
    {
        _svc.IsActive = false;
    }

    /// <summary>
    /// Clears ALL HidHide state: disables hiding, removes all blocked devices, removes all allowed apps.
    /// CRITICAL: Call on startup AND in all exit/crash paths.
    /// </summary>
    public void ClearAll()
    {
        try
        {
            _svc.IsActive = false;
            _svc.ClearBlockedInstancesList();
            _svc.ClearApplicationsList();
        }
        catch
        {
            // Best-effort cleanup — must never throw during crash handling
        }
    }

    /// <summary>
    /// Converts a HID symbolic link path to a device instance ID for HidHide.
    /// \\?\hid#vid_045e&amp;pid_02ff&amp;ig_00#7&amp;286a539d&amp;1&amp;0000#{guid}
    /// → HID\VID_045E&amp;PID_02FF&amp;IG_00\7&amp;286A539D&amp;1&amp;0000
    /// </summary>
    public static string HidPathToInstanceId(string hidPath)
    {
        string s = hidPath;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        int guidStart = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidStart >= 0) s = s[..guidStart];
        return s.Replace('#', '\\').ToUpperInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAll();
    }
}
