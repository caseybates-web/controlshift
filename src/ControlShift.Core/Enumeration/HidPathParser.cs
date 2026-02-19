namespace ControlShift.Core.Enumeration;

/// <summary>
/// Parses VID and PID from Bluetooth Classic (BTHENUM) HID device paths where
/// HidSharp's <c>VendorID</c>/<c>ProductID</c> properties may be unreliable.
/// </summary>
/// <remarks>
/// USB and Bluetooth Classic paths encode VID/PID differently:
///
///   USB:      …VID_045E&amp;PID_028E…   — underscore separator, 4-digit hex each
///   BTHENUM:  …VID&amp;0002045e…        — ampersand separator, 8-digit hex (subcode:4 + VID:4)
///             …PID&amp;02e0…            — ampersand separator, 4-digit hex
///
/// The subcode "0002" in the VID segment is Windows' Bluetooth company-type field
/// (0001 = SIG-assigned range, 0002 = vendor-specific / standard IEEE assignment).
/// The actual 4-digit vendor ID is in the last four characters of the 8-digit segment.
///
/// HidSharp calls <c>HidD_GetAttributes</c> to retrieve VendorID; for some BTHENUM
/// devices Windows returns the raw 8-char segment value, causing HidSharp to report
/// the subcode (0x0002) as the VendorID rather than the true VID (0x045E).
/// This class provides a reliable fallback by extracting directly from the path string.
/// </remarks>
public static class HidPathParser
{
    /// <summary>
    /// Extracts the true 4-digit uppercase hex vendor ID from a BTHENUM device path.
    /// The BTHENUM format is <c>VID&amp;{subcode:4}{vid:4}</c> (e.g. <c>VID&amp;0002045e</c>).
    /// Returns <c>null</c> when the path contains no <c>VID&amp;</c> segment or the segment
    /// is shorter than 8 hex digits.
    /// </summary>
    /// <example><c>"...VID&amp;0002045e..."</c> → <c>"045E"</c></example>
    public static string? ExtractBthVid(string devicePath)
    {
        int idx = devicePath.IndexOf("VID&", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        int start = idx + 4;      // position of first char after "VID&"
        if (start + 8 > devicePath.Length) return null;

        // Skip the 4-char subcode (e.g. "0002"), take the next 4 = actual VID.
        return devicePath.Substring(start + 4, 4).ToUpperInvariant();
    }

    /// <summary>
    /// Extracts the true 4-digit uppercase hex product ID from a BTHENUM device path.
    /// The BTHENUM format is <c>PID&amp;{pid:4}</c> (e.g. <c>PID&amp;02e0</c>).
    /// Returns <c>null</c> when the path contains no <c>PID&amp;</c> segment or the segment
    /// is shorter than 4 hex digits.
    /// </summary>
    /// <example><c>"...PID&amp;02e0..."</c> → <c>"02E0"</c></example>
    public static string? ExtractBthPid(string devicePath)
    {
        int idx = devicePath.IndexOf("PID&", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        int start = idx + 4;      // position of first char after "PID&"
        if (start + 4 > devicePath.Length) return null;

        // PID has no subcode prefix — take 4 chars directly.
        return devicePath.Substring(start, 4).ToUpperInvariant();
    }
}
