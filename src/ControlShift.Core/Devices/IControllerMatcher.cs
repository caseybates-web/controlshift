using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

public interface IControllerMatcher
{
    /// <summary>
    /// Matches each XInput slot to its HID device counterpart (via IG_0X path marker),
    /// annotates with fingerprinting and vendor lookup results, and returns one
    /// <see cref="MatchedController"/> per XInput slot.
    /// </summary>
    IReadOnlyList<MatchedController> Match(
        IReadOnlyList<XInputSlotInfo> xinputSlots,
        IReadOnlyList<HidDeviceInfo>  hidDevices);
}
