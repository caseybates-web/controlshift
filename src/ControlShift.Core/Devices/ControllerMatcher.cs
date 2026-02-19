using System.Diagnostics;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// Links XInput slots to their HID devices and annotates with fingerprinting + vendor data.
/// </summary>
/// <remarks>
/// XInput ↔ HID matching: every XInput-capable HID device exposes an interface whose Windows
/// device path contains the marker "IG_0N" where N is the XInput slot index (0–3). Searching
/// for this marker is the standard way to associate XInput slots with HID devices without
/// needing admin rights or registry access.
///
/// Bluetooth detection: Bluetooth HID device paths contain "BTHENUM" (classic BT), the
/// HID-over-GATT UUID "{00001124-0000-1000-8000-00805f9b34fb}", or "BTHLEDevice" (BT LE /
/// Xbox Wireless Controller on Windows 10/11). All other paths are treated as USB.
/// </remarks>
public sealed class ControllerMatcher : IControllerMatcher
{
    private readonly IVendorDatabase     _vendors;
    private readonly IDeviceFingerprinter _fingerprinter;

    public ControllerMatcher(IVendorDatabase vendors, IDeviceFingerprinter fingerprinter)
    {
        _vendors       = vendors;
        _fingerprinter = fingerprinter;
    }

    public IReadOnlyList<MatchedController> Match(
        IReadOnlyList<XInputSlotInfo> xinputSlots,
        IReadOnlyList<HidDeviceInfo>  hidDevices)
    {
        var fingerprinted = _fingerprinter.Fingerprint(hidDevices);
        var results = new MatchedController[xinputSlots.Count];

        // Debug: dump all HID paths so BT matching failures are visible in Output window.
        Debug.WriteLine($"[ControllerMatcher] {xinputSlots.Count} XInput slots, {hidDevices.Count} HID devices:");
        foreach (var h in hidDevices)
            Debug.WriteLine($"  HID VID={h.Vid} PID={h.Pid} name='{h.ProductName}' path={h.DevicePath}");

        for (int i = 0; i < xinputSlots.Count; i++)
        {
            var slot = xinputSlots[i];

            if (!slot.IsConnected)
            {
                results[i] = new MatchedController(
                    slot.SlotIndex,
                    IsConnected:          false,
                    slot.ConnectionType,
                    BatteryPercent:       null,
                    Hid:                  null,
                    IsIntegratedGamepad:  false,
                    KnownDeviceName:      null,
                    IsKnownDeviceConfirmed: false,
                    VendorBrand:          null,
                    HidConnectionType:    HidConnectionType.Unknown);
                continue;
            }

            // Find the HID device whose path contains the IG_0N marker for this slot.
            string igMarker = $"IG_0{slot.SlotIndex}";
            HidDeviceInfo?    hid = null;
            FingerprintedDevice? fp  = null;

            foreach (var h in hidDevices)
            {
                if (h.DevicePath.IndexOf(igMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hid = h;
                    fp  = fingerprinted.FirstOrDefault(f => f.Device.DevicePath == h.DevicePath);
                    break;
                }
            }

            Debug.WriteLine($"  Slot {slot.SlotIndex}: marker='{igMarker}' → {(hid is not null ? $"matched VID={hid.Vid} PID={hid.Pid} name='{hid.ProductName}'" : "NO HID MATCH")}");
            if (hid is not null)
                Debug.WriteLine($"    BT check → conn={DetectHidConnectionType(hid.DevicePath)}");

            results[i] = new MatchedController(
                slot.SlotIndex,
                IsConnected:            true,
                slot.ConnectionType,
                slot.BatteryPercent,
                hid,
                fp?.IsIntegratedGamepad  ?? false,
                fp?.KnownDeviceName,
                fp?.IsConfirmed          ?? false,
                hid is not null ? _vendors.GetBrand(hid.Vid) : null,
                hid is not null ? DetectHidConnectionType(hid.DevicePath) : HidConnectionType.Unknown);
        }

        return results;
    }

    private static HidConnectionType DetectHidConnectionType(string devicePath)
    {
        // Classic Bluetooth HID (all controllers except Xbox Wireless on Win10/11)
        if (devicePath.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        // HID-over-GATT service UUID (HoGP — some BT classic devices also appear here)
        if (devicePath.IndexOf("{00001124-0000-1000-8000-00805f9b34fb}",
                               StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        // Bluetooth LE / Xbox Wireless Controller paired via BT on Windows 10/11.
        // These devices use the BTHLEDevice enumerator rather than BTHENUM.
        if (devicePath.IndexOf("BTHLEDevice", StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        return HidConnectionType.Usb;
    }
}
