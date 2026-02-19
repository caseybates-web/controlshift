using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

public interface IDeviceFingerprinter
{
    /// <summary>
    /// Annotates each HID device with fingerprinting results — whether it matches
    /// a known integrated gamepad, and if so, its friendly name and confirmation status.
    /// </summary>
    IReadOnlyList<FingerprintedDevice> Fingerprint(IReadOnlyList<HidDeviceInfo> devices);
}
