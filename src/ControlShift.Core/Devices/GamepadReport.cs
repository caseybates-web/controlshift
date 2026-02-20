namespace ControlShift.Core.Devices;

/// <summary>
/// Full Xbox 360 gamepad state snapshot — the canonical interchange format
/// between the HID report parser and the ViGEm submit path.
/// </summary>
public readonly record struct GamepadReport(
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short ThumbLX,
    short ThumbLY,
    short ThumbRX,
    short ThumbRY);
