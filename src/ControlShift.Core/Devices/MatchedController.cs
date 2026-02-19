using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// All available identity information for one XInput player slot,
/// combining XInput poll data with the matched HID device (if found).
/// </summary>
public sealed record MatchedController(
    int SlotIndex,
    bool IsConnected,
    XInputConnectionType XInputConnectionType,
    int? BatteryPercent,
    /// <summary>Matched HID device, or null if no IG_0X marker was found.</summary>
    HidDeviceInfo? Hid,
    bool IsIntegratedGamepad,
    string? KnownDeviceName,
    bool IsKnownDeviceConfirmed,
    /// <summary>Brand name from known-vendors.json, or null if the VID is not listed.</summary>
    string? VendorBrand,
    HidConnectionType HidConnectionType
);
