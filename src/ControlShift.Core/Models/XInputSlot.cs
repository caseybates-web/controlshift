namespace ControlShift.Core.Models;

/// <summary>
/// Represents the state of a single XInput slot (0–3).
/// </summary>
public sealed record XInputSlot
{
    /// <summary>XInput slot index: 0 (P1) through 3 (P4).</summary>
    public required int SlotIndex { get; init; }

    /// <summary>Whether a controller is connected in this slot.</summary>
    public required bool IsConnected { get; init; }

    /// <summary>Controller type reported by XInput (e.g. "Gamepad").</summary>
    public string? DeviceType { get; init; }

    /// <summary>Controller sub-type reported by XInput.</summary>
    public string? DeviceSubType { get; init; }

    /// <summary>Battery level: 0 = Empty, 1 = Low, 2 = Medium, 3 = Full. Null if wired or unavailable.</summary>
    public byte? BatteryLevel { get; init; }

    /// <summary>Battery type string: "Wired", "Alkaline", "NiMH", "Unknown".</summary>
    public string? BatteryType { get; init; }
}
