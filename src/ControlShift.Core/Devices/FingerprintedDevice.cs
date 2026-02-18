using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// A HID device annotated with fingerprinting results.
/// </summary>
public sealed record FingerprintedDevice(
    HidDeviceInfo Device,
    /// <summary>true if this device matched a VID+PID in known-devices.json.</summary>
    bool IsIntegratedGamepad,
    /// <summary>
    /// Friendly name from known-devices.json, or null if the device was not recognised.
    /// </summary>
    string? KnownDeviceName,
    /// <summary>
    /// Mirrors the "confirmed" flag from known-devices.json.
    /// false when IsIntegratedGamepad is false, or when the entry is unconfirmed.
    /// </summary>
    bool IsConfirmed
);
