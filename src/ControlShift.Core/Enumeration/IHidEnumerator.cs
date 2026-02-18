using ControlShift.Core.Models;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates HID game controllers (gamepads and joysticks) currently connected to the system.
/// </summary>
public interface IHidEnumerator
{
    IReadOnlyList<HidDeviceInfo> EnumerateGameControllers();
}
