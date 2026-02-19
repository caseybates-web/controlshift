namespace ControlShift.Core.Enumeration;

public enum HidConnectionType
{
    /// <summary>Connected via USB cable.</summary>
    Usb,
    /// <summary>Connected via Bluetooth (detected from device path).</summary>
    Bluetooth,
    /// <summary>Could not determine connection type (e.g. no matching HID device found).</summary>
    Unknown,
}
