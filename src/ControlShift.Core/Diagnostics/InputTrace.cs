namespace ControlShift.Core.Diagnostics;

/// <summary>
/// Diagnostic trace logger for input path investigation.
/// Writes to %TEMP%\controlshift-input-trace.log.
/// Remove after root cause is identified.
/// </summary>
public static class InputTrace
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "controlshift-input-trace.log");

    private static readonly object Lock = new();

    /// <summary>Clears the trace log for a fresh session.</summary>
    public static void Init()
    {
        lock (Lock)
        {
            try
            {
                File.WriteAllText(LogPath,
                    $"=== Input Trace Started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
            }
            catch { /* must never throw */ }
        }
    }

    /// <summary>Writes a timestamped line to the trace log.</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* must never throw */ }
        }
    }

    /// <summary>Converts XInput button bitmask to human-readable button names.</summary>
    public static string ButtonNames(ushort mask)
    {
        if (mask == 0) return "none";

        var parts = new System.Text.StringBuilder(64);
        if ((mask & 0x0001) != 0) parts.Append("Up,");
        if ((mask & 0x0002) != 0) parts.Append("Down,");
        if ((mask & 0x0004) != 0) parts.Append("Left,");
        if ((mask & 0x0008) != 0) parts.Append("Right,");
        if ((mask & 0x0010) != 0) parts.Append("Start,");
        if ((mask & 0x0020) != 0) parts.Append("Back,");
        if ((mask & 0x0040) != 0) parts.Append("LThumb,");
        if ((mask & 0x0080) != 0) parts.Append("RThumb,");
        if ((mask & 0x0100) != 0) parts.Append("LShoulder,");
        if ((mask & 0x0200) != 0) parts.Append("RShoulder,");
        if ((mask & 0x0400) != 0) parts.Append("Guide,");
        if ((mask & 0x1000) != 0) parts.Append("A,");
        if ((mask & 0x2000) != 0) parts.Append("B,");
        if ((mask & 0x4000) != 0) parts.Append("X,");
        if ((mask & 0x8000) != 0) parts.Append("Y,");

        if (parts.Length > 0) parts.Length--; // trim trailing comma
        return parts.ToString();
    }
}
