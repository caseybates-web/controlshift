namespace ControlShift.Core.Enumeration;

public interface IHidEnumerator
{
    /// <summary>Enumerates all HID devices currently visible to the OS.</summary>
    IReadOnlyList<HidDeviceInfo> GetDevices();
}
