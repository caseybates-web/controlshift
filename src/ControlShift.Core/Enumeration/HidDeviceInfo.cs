namespace ControlShift.Core.Enumeration;

/// <summary>
/// Snapshot of a single HID device as reported by HidSharp.
/// VID and PID are uppercase 4-digit hex strings (e.g. "0B05", "1ABE")
/// to match the format used in known-devices.json.
/// </summary>
public sealed record HidDeviceInfo(
    string Vid,
    string Pid,
    /// <summary>Product name string from the device descriptor, or null if unavailable.</summary>
    string? ProductName,
    string DevicePath
);
