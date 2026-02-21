using System.Diagnostics;

namespace ControlShift.Core.Diagnostics;

/// <summary>
/// Static debug logger that writes timestamped entries to %TEMP%\controlshift-debug.log.
/// Rotates at 5MB. Thread-safe. Never throws.
/// </summary>
public static class DebugLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024; // 5MB

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "controlshift-debug.log");

    private static readonly object Lock = new();

    /// <summary>Writes a timestamped line to the debug log.</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* log writes must never throw */ }
        }
    }

    /// <summary>Logs an exception with full stack trace.</summary>
    public static void Exception(string context, Exception ex)
    {
        Log($"[EXCEPTION] {context}: {ex}");
    }

    /// <summary>Logs app startup.</summary>
    public static void Startup(string version = "")
    {
        Log($"=== APP STARTUP === {(string.IsNullOrEmpty(version) ? "" : $"v{version}")}".TrimEnd());
    }

    /// <summary>Logs app shutdown.</summary>
    public static void Shutdown(string reason = "normal")
    {
        Log($"=== APP SHUTDOWN === reason={reason}");
    }

    /// <summary>Logs a controller connect/disconnect event.</summary>
    public static void ControllerChange(string action, int slot, string vidPid, string busType)
    {
        Log($"[Controller] {action} slot={slot} vidPid={vidPid} busType={busType}");
    }

    /// <summary>Logs a ViGEm slot assignment.</summary>
    public static void ViGEmSlotAssigned(int physicalSlot, int virtualSlot)
    {
        Log($"[ViGEm] Assigned physical={physicalSlot} → virtual={virtualSlot}");
    }

    /// <summary>Logs a HidHide hide/unhide operation.</summary>
    public static void HidHide(string action, string instanceId)
    {
        Log($"[HidHide] {action} instanceId={instanceId}");
    }

    /// <summary>Logs a WM_DEVICECHANGE event with device counts.</summary>
    public static void DeviceChange(int countBefore, int countAfter)
    {
        Log($"[WM_DEVICECHANGE] devices before={countBefore} after={countAfter}");
    }

    /// <summary>Logs a slot map change.</summary>
    public static void SlotMapChange(int[] before, int[] after)
    {
        Log($"[SlotMap] before=[{string.Join(",", before)}] after=[{string.Join(",", after)}]");
    }

    /// <summary>Logs a NavTimer focus change (not every tick).</summary>
    public static void FocusChange(string elementName, int cardIndex)
    {
        Log($"[Focus] element={elementName} cardIndex={cardIndex}");
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length >= MaxFileBytes)
            {
                var rotatedPath = LogPath + ".old";
                File.Delete(rotatedPath);
                File.Move(LogPath, rotatedPath);
            }
        }
        catch { /* rotation failure is non-fatal */ }
    }
}
