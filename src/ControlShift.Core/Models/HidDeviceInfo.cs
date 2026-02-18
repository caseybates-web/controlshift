namespace ControlShift.Core.Models;

/// <summary>
/// Represents a HID game controller discovered by HidSharp.
/// </summary>
public sealed record HidDeviceInfo
{
    /// <summary>Vendor ID in uppercase hex (e.g. "0B05").</summary>
    public required string Vid { get; init; }

    /// <summary>Product ID in uppercase hex (e.g. "1ABE").</summary>
    public required string Pid { get; init; }

    /// <summary>OS-level HID device path used for opening the device.</summary>
    public required string DevicePath { get; init; }

    /// <summary>Human-readable product name from the HID descriptor.</summary>
    public string? ProductName { get; init; }

    /// <summary>Serial number if reported by the device.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>BCD-encoded release number from the HID descriptor.</summary>
    public int? ReleaseNumber { get; init; }
}
