namespace ControlShift.Core.Models;

/// <summary>
/// Merged view of a controller combining XInput slot data, HID identity,
/// and fingerprint results. This is the primary model shown in the UI.
/// </summary>
public sealed record ControllerInfo
{
    /// <summary>XInput slot index (0–3).</summary>
    public required int SlotIndex { get; init; }

    /// <summary>Whether a controller is connected in this slot.</summary>
    public required bool IsConnected { get; init; }

    /// <summary>Display name from known-devices DB or HID product name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>How the controller is connected.</summary>
    public ConnectionType ConnectionType { get; init; }

    /// <summary>True if this is a known integrated gamepad (built into the handheld).</summary>
    public bool IsIntegratedGamepad { get; init; }

    /// <summary>Battery level: 0–3. Null if wired or unavailable.</summary>
    public byte? BatteryLevel { get; init; }

    /// <summary>Battery type string.</summary>
    public string? BatteryType { get; init; }

    /// <summary>HID device path, used for Phase 2 input forwarding.</summary>
    public string? DevicePath { get; init; }

    /// <summary>Vendor ID if identified via HID.</summary>
    public string? Vid { get; init; }

    /// <summary>Product ID if identified via HID.</summary>
    public string? Pid { get; init; }
}
