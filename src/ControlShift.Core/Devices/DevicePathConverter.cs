namespace ControlShift.Core.Devices;

/// <summary>
/// Converts between HidSharp device paths and HidHide device instance IDs.
/// </summary>
/// <remarks>
/// DECISION: HidHide's AddBlockedInstanceId expects the device instance ID
/// (e.g. "HID\VID_045E&amp;PID_028E&amp;IG_00\7&amp;abc&amp;0&amp;0000"),
/// not the device interface path that HidSharp returns
/// (e.g. "\\?\HID#VID_045E&amp;PID_028E&amp;IG_00#7&amp;abc&amp;0&amp;0000#{guid}").
/// </remarks>
public static class DevicePathConverter
{
    /// <summary>
    /// Converts a HidSharp device path to a HidHide device instance ID.
    /// </summary>
    /// <example>
    /// Input:  \\?\HID#VID_045E&amp;PID_028E&amp;IG_00#7&amp;abc&amp;0&amp;0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
    /// Output: HID\VID_045E&amp;PID_028E&amp;IG_00\7&amp;abc&amp;0&amp;0000
    /// </example>
    public static string ToInstanceId(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        string path = devicePath;

        // Strip the \\?\ prefix.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        // Remove the trailing interface GUID suffix (e.g. #{4d1e55b2-...}).
        int guidStart = path.LastIndexOf('{');
        if (guidStart > 0)
            path = path[..guidStart].TrimEnd('#', '\\');

        // Replace # separators with \.
        path = path.Replace('#', '\\');

        return path;
    }
}
