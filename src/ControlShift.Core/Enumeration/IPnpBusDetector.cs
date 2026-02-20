namespace ControlShift.Core.Enumeration;

/// <summary>
/// Classifies a HID device's transport type by examining its PnP device path
/// and (when possible) walking the PnP device tree via CfgMgr32.
/// </summary>
public interface IPnpBusDetector
{
    /// <summary>
    /// Returns the <see cref="BusType"/> for the device identified by
    /// <paramref name="hidDevicePath"/> (a path as returned by HidSharp or SetupAPI).
    /// Never throws — returns <see cref="BusType.Unknown"/> on any failure.
    /// </summary>
    BusType DetectBusType(string hidDevicePath);
}
