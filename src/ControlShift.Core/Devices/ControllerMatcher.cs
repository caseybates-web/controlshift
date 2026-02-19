using System.Diagnostics;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// Links XInput slots to their HID devices and annotates with fingerprinting + vendor data.
/// </summary>
/// <remarks>
/// XInput ↔ HID matching — two passes:
///   Pass 1 (exact): each connected slot searches for "IG_0N" in HID paths (N = slot index).
///   Pass 2 (fallback): slots still unmatched after pass 1 scan ALL remaining unclaimed IG
///   paths. This handles the common BT scenario where Windows assigns a controller IG_00 when
///   it first connects, then XInput re-indexes it to slot 1 after a second controller appears
///   — the HID path still says IG_00 but XInput now reports it as slot 1.
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
        var results       = new MatchedController[xinputSlots.Count];

        // ── Debug: raw path dump ───────────────────────────────────────────────
        // Every device path is logged verbatim so BT format differences are visible.
        Debug.WriteLine($"[ControllerMatcher] {xinputSlots.Count} XInput slots, {hidDevices.Count} HID devices:");
        for (int d = 0; d < hidDevices.Count; d++)
        {
            var h = hidDevices[d];
            Debug.WriteLine($"  [{d}] VID={h.Vid} PID={h.Pid} name='{h.ProductName}'");
            Debug.WriteLine($"       path={h.DevicePath}");
        }

        // ── Populate disconnected slots immediately ─────────────────────────────
        for (int i = 0; i < xinputSlots.Count; i++)
        {
            if (!xinputSlots[i].IsConnected)
            {
                results[i] = new MatchedController(
                    xinputSlots[i].SlotIndex,
                    IsConnected:            false,
                    xinputSlots[i].ConnectionType,
                    BatteryPercent:         null,
                    Hid:                    null,
                    IsIntegratedGamepad:    false,
                    KnownDeviceName:        null,
                    IsKnownDeviceConfirmed: false,
                    VendorBrand:            null,
                    HidConnectionType:      HidConnectionType.Unknown);
            }
        }

        // ── Pass 1: exact IG_0N match ──────────────────────────────────────────
        // Track which HID device paths have been claimed so pass 2 never double-matches.
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < xinputSlots.Count; i++)
        {
            var slot = xinputSlots[i];
            if (!slot.IsConnected) continue;

            string igMarker = $"IG_0{slot.SlotIndex}";
            TryMatchSlot(slot, hidDevices, fingerprinted, igMarker, claimedPaths,
                         out var hid, out var fp);

            Debug.WriteLine($"  Pass1 Slot{slot.SlotIndex}: marker='{igMarker}' → " +
                            $"{(hid is not null ? $"MATCH VID={hid.Vid} PID={hid.Pid} conn={DetectHidConnectionType(hid.DevicePath)}" : "no match")}");

            if (hid is not null)
                results[i] = BuildResult(slot, hid, fp);
        }

        // ── Pass 2: fallback for unmatched connected slots ──────────────────────
        // Windows assigns IG_0N numbers when a device is first enumerated. If a
        // second controller (e.g. an integrated gamepad) later takes slot 0, XInput
        // will report the BT controller in slot 1 — but its HID path still says IG_00.
        // We try ALL other IG_0X markers (X ≠ slot index) for unclaimed paths.
        for (int i = 0; i < xinputSlots.Count; i++)
        {
            var slot = xinputSlots[i];
            if (!slot.IsConnected || results[i] is not null) continue; // already matched

            HidDeviceInfo?       hid = null;
            FingerprintedDevice? fp  = null;

            for (int x = 0; x < 4 && hid is null; x++)
            {
                if (x == slot.SlotIndex) continue; // already tried in pass 1
                string altMarker = $"IG_0{x}";
                TryMatchSlot(slot, hidDevices, fingerprinted, altMarker, claimedPaths,
                             out hid, out fp);

                if (hid is not null)
                    Debug.WriteLine($"  Pass2 Slot{slot.SlotIndex}: fallback marker='{altMarker}' → " +
                                    $"MATCH VID={hid.Vid} PID={hid.Pid} (IG index≠XInput slot)");
            }

            if (hid is null)
                Debug.WriteLine($"  Pass2 Slot{slot.SlotIndex}: NO MATCH in any IG_0X path");

            results[i] = hid is not null
                ? BuildResult(slot, hid, fp)
                : BuildNoHidResult(slot);
        }

        return results;
    }

    /// <summary>
    /// Searches <paramref name="hidDevices"/> for a path containing <paramref name="igMarker"/>
    /// that is not already in <paramref name="claimedPaths"/>. On success, adds the matched
    /// path to <paramref name="claimedPaths"/> and sets <paramref name="hid"/> and
    /// <paramref name="fp"/>; otherwise both are null.
    /// </summary>
    private static void TryMatchSlot(
        XInputSlotInfo              slot,
        IReadOnlyList<HidDeviceInfo> hidDevices,
        IReadOnlyList<FingerprintedDevice> fingerprinted,
        string igMarker,
        HashSet<string> claimedPaths,
        out HidDeviceInfo?       hid,
        out FingerprintedDevice? fp)
    {
        hid = null;
        fp  = null;

        foreach (var h in hidDevices)
        {
            if (claimedPaths.Contains(h.DevicePath)) continue;
            if (h.DevicePath.IndexOf(igMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hid = h;
                fp  = fingerprinted.FirstOrDefault(f => f.Device.DevicePath == h.DevicePath);
                claimedPaths.Add(h.DevicePath);
                return;
            }
        }
    }

    private MatchedController BuildResult(
        XInputSlotInfo slot, HidDeviceInfo hid, FingerprintedDevice? fp) =>
        new(slot.SlotIndex,
            IsConnected:            true,
            slot.ConnectionType,
            slot.BatteryPercent,
            hid,
            fp?.IsIntegratedGamepad  ?? false,
            fp?.KnownDeviceName,
            fp?.IsConfirmed          ?? false,
            _vendors.GetBrand(hid.Vid),
            DetectHidConnectionType(hid.DevicePath));

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
