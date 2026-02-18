using ControlShift.Core.Models;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Polls XInput slots 0–3 and returns their current state.
/// </summary>
public interface IXInputEnumerator
{
    IReadOnlyList<XInputSlot> EnumerateSlots();
}
