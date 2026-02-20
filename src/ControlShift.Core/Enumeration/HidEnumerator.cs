using System.Diagnostics;
using HidSharp;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates HID devices via HidSharp and supplements with BLE HID devices
/// found via SetupAPI (HOGP profile GUID <c>{00001812-...}</c>).
/// </summary>
/// <remarks>
/// DECISION: We enumerate all HID devices rather than pre-filtering by usage page
/// (Generic Desktop / Gamepad). Filtering by usage page would require opening each
/// device descriptor, which can fail with access-denied on locked devices. The
/// DeviceFingerprinter in Step 4 does VID+PID matching anyway, so filtering here
/// would only be a performance optimization — not worth the fragility in v1.
///
/// ProductName retrieval is best-effort: some devices (keyboards, mice, internal
/// HID nodes) return nothing or throw. We swallow the exception and store null.
///
/// BLE HID supplement: HidSharp uses the standard HID interface GUID to enumerate
/// devices. Some BLE controllers on certain Windows builds only appear under the
/// HOGP service GUID {00001812-...}. BleHidEnumerator queries that GUID and any
/// results not already present (by normalized instance ID) are appended.
/// </remarks>
public sealed class HidEnumerator : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        var results = new List<HidDeviceInfo>();

        // ── HidSharp enumeration ──────────────────────────────────────────────
        foreach (HidDevice device in DeviceList.Local.GetHidDevices())
        {
            // For BTHENUM Bluetooth paths ("VID&" format), HidSharp may report the
            // 4-char subcode (e.g. 0x0002) as the VendorID instead of the true VID.
            // Use path-based extraction as the primary source when available; fall
            // back to the HidSharp integer API for USB and non-BTHENUM paths.
            string? pathVid = HidPathParser.ExtractBthVid(device.DevicePath);
            string? pathPid = HidPathParser.ExtractBthPid(device.DevicePath);

            string vid = pathVid ?? device.VendorID.ToString("X4");
            string pid = pathPid ?? device.ProductID.ToString("X4");

            string? productName = null;
            try { productName = device.GetProductName(); }
            catch { /* device may be inaccessible or not support the string descriptor */ }

            results.Add(new HidDeviceInfo(vid, pid, productName, device.DevicePath));
        }

        // ── BLE HID supplement via SetupAPI ───────────────────────────────────
        // Build a set of instance IDs already found by HidSharp for deduplication.
        // Instance ID = path stripped of the trailing #{GUID} interface suffix,
        // normalized to uppercase. Two paths that differ only in trailing GUID
        // (e.g. HidSharp uses {4d1e55b2-...}, SetupAPI uses {00001812-...}) map
        // to the same instance ID and represent the same physical device.
        var seenInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in results)
            seenInstanceIds.Add(NormalizeToInstanceId(h.DevicePath));

        var bleDevices = BleHidEnumerator.GetBleHidDevices();
        Debug.WriteLine($"[HidEnum] BleHidEnumerator returned {bleDevices.Count} device(s):");
        int bleAdded = 0;
        foreach (var bleDevice in bleDevices)
        {
            string norm = NormalizeToInstanceId(bleDevice.DevicePath);
            bool isNew  = seenInstanceIds.Add(norm);
            Debug.WriteLine($"[HidEnum]   BLE VID={bleDevice.Vid} PID={bleDevice.Pid} " +
                            $"new={isNew} normId={norm}");
            Debug.WriteLine($"[HidEnum]       path={bleDevice.DevicePath}");
            if (isNew)
            {
                results.Add(bleDevice);
                bleAdded++;
            }
        }
        Debug.WriteLine($"[HidEnum] {bleAdded} BLE device(s) added (not already in HidSharp results)");

        return results;
    }

    /// <summary>
    /// Strips the trailing <c>#{GUID}</c> interface suffix from a device path
    /// so paths for the same device but different interface GUIDs compare equal.
    /// </summary>
    private static string NormalizeToInstanceId(string devicePath)
    {
        // Strip \\?\
        string s = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? devicePath.Substring(4)
            : devicePath;

        // Remove trailing #{GUID}
        int lastHash = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (lastHash >= 0)
            s = s.Substring(0, lastHash);

        return s.ToUpperInvariant();
    }
}
