using System.Diagnostics;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// Links XInput slots to their HID devices and annotates with fingerprinting + vendor data.
/// </summary>
/// <remarks>
/// XInput ↔ HID matching — single pass:
///   For each connected slot N, search for a HID device whose path contains "IG_0N".
///   No fallback passes — if the exact marker is absent, no match is made.
///
/// Connection type: determined by <see cref="IPnpBusDetector"/>.DetectBusType,
///   which checks BT service GUIDs in the path and walks the PnP device tree via
///   CfgMgr32. Falls back to <see cref="BusType.Unknown"/> if the tree walk fails.
/// </remarks>
public sealed class ControllerMatcher : IControllerMatcher
{
    private readonly IVendorDatabase      _vendors;
    private readonly IDeviceFingerprinter _fingerprinter;
    private readonly IPnpBusDetector      _busDetector;

    public ControllerMatcher(
        IVendorDatabase      vendors,
        IDeviceFingerprinter fingerprinter,
        IPnpBusDetector      busDetector)
    {
        _vendors       = vendors;
        _fingerprinter = fingerprinter;
        _busDetector   = busDetector;
    }

    public IReadOnlyList<MatchedController> Match(
        IReadOnlyList<XInputSlotInfo> xinputSlots,
        IReadOnlyList<HidDeviceInfo>  hidDevices)
    {
        var fingerprinted = _fingerprinter.Fingerprint(hidDevices);
        var results       = new MatchedController[xinputSlots.Count];

        // ── Debug: raw HID device dump ─────────────────────────────────────────
        Debug.WriteLine($"[ControllerMatcher] {xinputSlots.Count} XInput slots, " +
                        $"{hidDevices.Count} HID devices:");
        for (int d = 0; d < hidDevices.Count; d++)
        {
            var h = hidDevices[d];
            Debug.WriteLine($"  HID[{d}] VID={h.Vid} PID={h.Pid} name='{h.ProductName}'");
            Debug.WriteLine($"          path={h.DevicePath}");
        }

        // ── Single-pass exact IG_0N match ──────────────────────────────────────
        for (int i = 0; i < xinputSlots.Count; i++)
        {
            var slot = xinputSlots[i];

            if (!slot.IsConnected)
            {
                Debug.WriteLine($"  Slot{slot.SlotIndex}: disconnected");
                results[i] = BuildDisconnectedResult(slot);
                continue;
            }

            string igMarker = $"IG_0{slot.SlotIndex}";
            Debug.WriteLine($"  Slot{slot.SlotIndex}: connected — searching for '{igMarker}' " +
                            $"in {hidDevices.Count} HID devices");

            HidDeviceInfo?       hid    = null;
            FingerprintedDevice? fp     = null;
            int                  hidIdx = -1;

            for (int d = 0; d < hidDevices.Count; d++)
            {
                var h = hidDevices[d];
                if (h.DevicePath.IndexOf(igMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hid    = h;
                    hidIdx = d;
                    fp     = fingerprinted.FirstOrDefault(f => f.Device.DevicePath == h.DevicePath);
                    Debug.WriteLine($"  Slot{slot.SlotIndex}: MATCH hidIndex={d} VID={h.Vid} PID={h.Pid} " +
                                    $"name='{h.ProductName}'");
                    Debug.WriteLine($"    path={h.DevicePath}");
                    break;
                }
            }

            if (hid is null)
            {
                Debug.WriteLine($"  Slot{slot.SlotIndex}: NO MATCH for '{igMarker}' — " +
                                $"checked {hidDevices.Count} device(s)");
                results[i] = BuildNoHidResult(slot);
            }
            else
            {
                results[i] = BuildResult(slot, hid, fp);
            }
        }

        return results;
    }

    // ── Result builders ────────────────────────────────────────────────────────

    private MatchedController BuildResult(
        XInputSlotInfo slot, HidDeviceInfo hid, FingerprintedDevice? fp)
    {
        var busType  = _busDetector.DetectBusType(hid.DevicePath);
        var connType = ToHidConnectionType(busType);
        var brand    = _vendors.GetBrand(hid.Vid);
        Debug.WriteLine($"  BuildResult Slot{slot.SlotIndex}: brand='{brand ?? "(none)"}' " +
                        $"busType={busType} connType={connType} " +
                        $"battery={slot.BatteryPercent?.ToString() ?? "null"}");
        return new MatchedController(
            slot.SlotIndex,
            IsConnected:            true,
            slot.ConnectionType,
            slot.BatteryPercent,
            hid,
            fp?.IsIntegratedGamepad  ?? false,
            fp?.KnownDeviceName,
            fp?.IsConfirmed          ?? false,
            brand,
            connType,
            busType);
    }

    private static MatchedController BuildDisconnectedResult(XInputSlotInfo slot) =>
        new(slot.SlotIndex,
            IsConnected:            false,
            slot.ConnectionType,
            BatteryPercent:         null,
            Hid:                    null,
            IsIntegratedGamepad:    false,
            KnownDeviceName:        null,
            IsKnownDeviceConfirmed: false,
            VendorBrand:            null,
            HidConnectionType:      HidConnectionType.Unknown,
            BusType:                BusType.Unknown);

    private static MatchedController BuildNoHidResult(XInputSlotInfo slot) =>
        new(slot.SlotIndex,
            IsConnected:            true,
            slot.ConnectionType,
            slot.BatteryPercent,
            Hid:                    null,
            IsIntegratedGamepad:    false,
            KnownDeviceName:        null,
            IsKnownDeviceConfirmed: false,
            VendorBrand:            null,
            HidConnectionType:      HidConnectionType.Unknown,
            BusType:                BusType.Unknown);

    // ── Bus type → HID connection type mapping ─────────────────────────────────

    private static HidConnectionType ToHidConnectionType(BusType busType) => busType switch
    {
        BusType.BluetoothLE         => HidConnectionType.Bluetooth,
        BusType.BluetoothClassic    => HidConnectionType.Bluetooth,
        BusType.XboxWirelessAdapter => HidConnectionType.Bluetooth,
        BusType.Usb                 => HidConnectionType.Usb,
        _                           => HidConnectionType.Unknown,
    };
}
