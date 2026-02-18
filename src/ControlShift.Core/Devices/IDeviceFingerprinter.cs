using ControlShift.Core.Models;

namespace ControlShift.Core.Devices;

/// <summary>
/// Matches XInput slots and HID devices against the known-devices database
/// to produce a unified list of identified controllers.
/// </summary>
public interface IDeviceFingerprinter
{
    IReadOnlyList<ControllerInfo> IdentifyControllers(
        IReadOnlyList<XInputSlot> xinputSlots,
        IReadOnlyList<HidDeviceInfo> hidDevices);
}
