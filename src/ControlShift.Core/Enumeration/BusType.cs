namespace ControlShift.Core.Enumeration;

/// <summary>
/// Granular transport/bus classification for a HID device,
/// as determined by the PnP device tree (CfgMgr32) or path heuristics.
/// </summary>
public enum BusType
{
    /// <summary>Bluetooth Low Energy — HOGP profile ({00001812-...}). Battery reporting via XInput is unreliable.</summary>
    BluetoothLE,
    /// <summary>Bluetooth Classic HID — BTHENUM enumerator ({00001124-...}).</summary>
    BluetoothClassic,
    /// <summary>Xbox Wireless Adapter — USB dongle (VID 045E, PID 02FE or 02E6), wireless protocol.</summary>
    XboxWirelessAdapter,
    /// <summary>USB cable — USB enumerator, direct wired connection.</summary>
    Usb,
    /// <summary>Could not determine the bus type from available information.</summary>
    Unknown,
}
