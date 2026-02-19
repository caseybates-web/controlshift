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
/// Bluetooth detection: A device path is Bluetooth if it contains ANY of:
///   "BTHENUM"                                — Classic Bluetooth HID
///   "BTHLEDevice"                            — Bluetooth LE (Xbox Wireless on Win10/11)
///   "{00001124-0000-1000-8000-00805f9b34fb}" — HID-over-GATT service UUID
///   "BLUETOOTHLEDEVICE"                      — Alternate BT LE enumerator string
///   Everything else is treated as USB.
/// </remarks>
public sealed class ControllerMatcher : IControllerMatcher
{
    private readonly IVendorDatabase      _vendors;
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
        var results       = new MatchedController[xinputSlots.Count];

        // ── Debug: raw HID device dump ─────────────────────────────────────────
        Debug.WriteLine($"[ControllerMatcher] {xinputSlots.Count} XInput slots, " +
                        $"{hidDevices.Count} HID devices:");
        for (int d = 0; d < hidDevices.Count; d++)
        {
            var h = hidDevices[d];
            var ct = DetectHidConnectionType(h.DevicePath);
            Debug.WriteLine($"  HID[{d}] VID={h.Vid} PID={h.Pid} name='{h.ProductName}' connType={ct}");
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

            HidDeviceInfo?       hid = null;
            FingerprintedDevice? fp  = null;

            foreach (var h in hidDevices)
            {
                if (h.DevicePath.IndexOf(igMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hid = h;
                    fp  = fingerprinted.FirstOrDefault(f => f.Device.DevicePath == h.DevicePath);
                    var ct = DetectHidConnectionType(h.DevicePath);
                    Debug.WriteLine($"  Slot{slot.SlotIndex}: MATCH VID={h.Vid} PID={h.Pid} " +
                                    $"name='{h.ProductName}' connType={ct} " +
                                    $"knownName='{fp?.KnownDeviceName ?? "(none)"}' " +
                                    $"integrated={fp?.IsIntegratedGamepad ?? false}");
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
        var connType = DetectHidConnectionType(hid.DevicePath);
        var brand    = _vendors.GetBrand(hid.Vid);
        Debug.WriteLine($"  BuildResult Slot{slot.SlotIndex}: brand='{brand ?? "(none)"}' " +
                        $"connType={connType} battery={slot.BatteryPercent?.ToString() ?? "null"}");
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
            connType);
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
            HidConnectionType:      HidConnectionType.Unknown);

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
            HidConnectionType:      HidConnectionType.Unknown);

    // ── Bluetooth detection ────────────────────────────────────────────────────

    private static HidConnectionType DetectHidConnectionType(string devicePath)
    {
        // Classic Bluetooth HID (most gamepads over BT)
        if (devicePath.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        // Bluetooth LE — Xbox Wireless Controller paired via BT on Win10/11
        if (devicePath.IndexOf("BTHLEDevice", StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        // HID-over-GATT service UUID (HoGP — also seen for some BT classic devices)
        if (devicePath.IndexOf("{00001124-0000-1000-8000-00805f9b34fb}",
                               StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        // Alternate BT LE enumerator string seen on some Windows builds
        if (devicePath.IndexOf("BLUETOOTHLEDEVICE", StringComparison.OrdinalIgnoreCase) >= 0)
            return HidConnectionType.Bluetooth;

        return HidConnectionType.Usb;
    }
}
