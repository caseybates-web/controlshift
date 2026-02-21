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
    private const string XboxVid = "045E";

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

    /// <summary>
    /// ViGEm virtual Xbox 360 controllers report VID=045E PID=028E.
    /// Exclude them from HID matching — both active virtual controllers AND stale
    /// ghost device nodes left behind by previous ViGEm sessions (PnP Status=Unknown).
    /// These ghost nodes persist in the HID device tree after ViGEm disconnects and
    /// cause phantom "Unknown Controller" cards in the UI if not filtered.
    /// </summary>
    private static bool IsViGEmVirtualController(HidDeviceInfo hid) =>
        string.Equals(hid.Vid, "045E", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(hid.Pid, "028E", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<MatchedController> Match(
        IReadOnlyList<XInputSlotInfo> xinputSlots,
        IReadOnlyList<HidDeviceInfo>  hidDevices)
    {
        // Filter out ViGEm virtual controllers (045E:028E) — they're our own virtual
        // devices and must not appear in the UI as real controllers.
        var filteredHidDevices = hidDevices.Where(h => !IsViGEmVirtualController(h)).ToList();

        var fingerprinted = _fingerprinter.Fingerprint(filteredHidDevices);
        var results       = new MatchedController[xinputSlots.Count];

        // ── Debug: raw HID device dump ─────────────────────────────────────────
        Debug.WriteLine($"[ControllerMatcher] {xinputSlots.Count} XInput slots, " +
                        $"{hidDevices.Count} HID devices ({hidDevices.Count - filteredHidDevices.Count} ViGEm filtered):");
        for (int d = 0; d < filteredHidDevices.Count; d++)
        {
            var h = filteredHidDevices[d];
            Debug.WriteLine($"  HID[{d}] VID={h.Vid} PID={h.Pid} name='{h.ProductName}'");
            Debug.WriteLine($"          path={h.DevicePath}");
        }

        // ── Phase 0: pre-classify VID=045E HID devices ────────────────────────
        // When two Xbox controllers are connected, both may expose ig_00 in their
        // HID paths regardless of which XInput slot they occupy. IG_0N matching
        // alone cannot disambiguate them. We pre-classify all 045E devices by
        // bus type (Usb vs Bluetooth) so Phase 1 can route each XInput slot to
        // the correctly-typed device using slot.ConnectionType (XINPUT_CAPS_WIRELESS).
        var busTypeCache    = new Dictionary<string, BusType>(StringComparer.OrdinalIgnoreCase);
        var xboxUsbPool     = new List<HidDeviceInfo>();
        var xboxWirelessPool = new List<HidDeviceInfo>();

        foreach (var hid in filteredHidDevices)
        {
            if (!string.Equals(hid.Vid, "045E", StringComparison.OrdinalIgnoreCase))
                continue;

            var bt = _busDetector.DetectBusType(hid.DevicePath);
            busTypeCache[hid.DevicePath] = bt;

            if (bt == BusType.Usb)
                xboxUsbPool.Add(hid);
            else if (bt is BusType.BluetoothLE or BusType.BluetoothClassic or BusType.XboxWirelessAdapter)
                xboxWirelessPool.Add(hid);
        }

        bool hasXboxPools = xboxUsbPool.Count > 0 || xboxWirelessPool.Count > 0;
        Debug.WriteLine($"[ControllerMatcher] Xbox pools: USB={xboxUsbPool.Count} " +
                        $"wireless={xboxWirelessPool.Count} hasXboxPools={hasXboxPools}");

        // ── Phase 1: per-slot matching ─────────────────────────────────────────
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < xinputSlots.Count; i++)
        {
            var slot = xinputSlots[i];

            if (!slot.IsConnected)
            {
                Debug.WriteLine($"  Slot{slot.SlotIndex}: disconnected");
                results[i] = BuildDisconnectedResult(slot);
                continue;
            }

            string igMarker    = $"IG_0{slot.SlotIndex}";
            bool slotIsWireless = slot.ConnectionType == XInputConnectionType.Wireless;

            Debug.WriteLine($"  Slot{slot.SlotIndex}: connected wireless={slotIsWireless} " +
                            $"igMarker={igMarker}");

            // Collect unmatched devices whose path contains this slot's IG marker.
            var igCandidates = filteredHidDevices
                .Where(h => !usedPaths.Contains(h.DevicePath)
                         && h.DevicePath.IndexOf(igMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            HidDeviceInfo? hid = null;

            if (igCandidates.Count == 1)
            {
                // Unambiguous IG_0N match — use it directly.
                hid = igCandidates[0];
                Debug.WriteLine($"    unambiguous IG match: VID={hid.Vid} PID={hid.Pid}");
            }
            else if (igCandidates.Count > 1
                  && igCandidates.All(h => string.Equals(h.Vid, XboxVid, StringComparison.OrdinalIgnoreCase)))
            {
                // Multiple VID=045E candidates share the same IG_0N (real-world Xbox
                // behavior). Use slot connection type to pick from the appropriate pool.
                var pool = slotIsWireless ? xboxWirelessPool : xboxUsbPool;
                hid = pool.FirstOrDefault(h => !usedPaths.Contains(h.DevicePath)
                                            && igCandidates.Contains(h));
                // Pool exhausted (e.g. bus type unknown for all pool members): fall
                // back to first available candidate.
                hid ??= igCandidates.FirstOrDefault(h => !usedPaths.Contains(h.DevicePath));
                Debug.WriteLine($"    pool disambiguate ({(slotIsWireless ? "wireless" : "wired")}): " +
                                $"VID={hid?.Vid ?? "none"} PID={hid?.Pid ?? "none"}");
            }
            else if (igCandidates.Count > 1)
            {
                // Mixed VIDs with the same IG_0N (unusual). Prefer non-045E first since
                // 045E is likely already claimed by pool logic in another slot.
                hid = igCandidates
                    .OrderByDescending(h => !string.Equals(h.Vid, XboxVid, StringComparison.OrdinalIgnoreCase))
                    .First(h => !usedPaths.Contains(h.DevicePath));
                Debug.WriteLine($"    mixed-VID fallback: VID={hid?.Vid} PID={hid?.Pid}");
            }
            else // igCandidates.Count == 0
            {
                // No IG_0N match. For VID=045E controllers whose drivers always expose
                // ig_00 regardless of slot, try the appropriate bus-type pool.
                var pool = slotIsWireless ? xboxWirelessPool : xboxUsbPool;
                hid = pool.FirstOrDefault(h => !usedPaths.Contains(h.DevicePath));
                Debug.WriteLine($"    no IG match — pool fallback ({(slotIsWireless ? "wireless" : "wired")}): " +
                                $"VID={hid?.Vid ?? "none"} PID={hid?.Pid ?? "none"}");
            }

            if (hid is null)
            {
                Debug.WriteLine($"  Slot{slot.SlotIndex}: NO MATCH");
                results[i] = BuildNoHidResult(slot);
            }
            else
            {
                usedPaths.Add(hid.DevicePath);
                var fp = fingerprinted.FirstOrDefault(f => f.Device.DevicePath == hid.DevicePath);
                if (!busTypeCache.TryGetValue(hid.DevicePath, out BusType busType))
                    busType = _busDetector.DetectBusType(hid.DevicePath);
                results[i] = BuildResult(slot, hid, fp, busType);
            }
        }

        return results;
    }

    // ── Result builders ────────────────────────────────────────────────────────

    private MatchedController BuildResult(
        XInputSlotInfo slot, HidDeviceInfo hid, FingerprintedDevice? fp, BusType busType)
    {
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
