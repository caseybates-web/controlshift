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
/// Bluetooth detection: Bluetooth HID device paths contain "BTHENUM" or the HID-over-GATT
/// service UUID "{00001124-0000-1000-8000-00805f9b34fb}". All other paths are treated as USB.
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
        if (devicePath.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0 ||
            devicePath.IndexOf("{00001124-0000-1000-8000-00805f9b34fb}",
                               StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        return HidConnectionType.Usb;
    }
}
