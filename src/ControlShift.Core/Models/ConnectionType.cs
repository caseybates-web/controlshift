namespace ControlShift.Core.Models;

/// <summary>
/// How a controller is physically connected to the system.
/// </summary>
public enum ConnectionType
{
    Unknown,
    Usb,
    Bluetooth,
    Integrated // Built-in gamepad on a gaming handheld (ROG Ally, Legion Go, etc.)
}
