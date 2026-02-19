namespace ControlShift.Core.Enumeration;

public interface IXInputEnumerator
{
    /// <summary>Polls all four XInput slots and returns their current state.</summary>
    IReadOnlyList<XInputSlotInfo> GetSlots();
}
