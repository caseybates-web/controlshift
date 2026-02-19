namespace ControlShift.Core.Enumeration;

public enum XInputDeviceType
{
    Unknown = 0,
    Gamepad = 1,
}

public enum XInputConnectionType
{
    Wired,
    Wireless,
}

public sealed record XInputSlotInfo(
    int SlotIndex,           // 0–3
    bool IsConnected,
    XInputDeviceType DeviceType,
    XInputConnectionType ConnectionType,
    /// <summary>Battery level 0–100, or null if wired / unavailable.</summary>
    int? BatteryPercent
);
