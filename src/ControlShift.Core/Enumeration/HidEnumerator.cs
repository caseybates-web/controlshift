using HidSharp;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates HID devices via HidSharp and returns a snapshot of each device's
/// VID, PID, product name, and device path.
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
/// </remarks>
public sealed class HidEnumerator : IHidEnumerator
{
    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        var results = new List<HidDeviceInfo>();

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

        return results;
    }
}
