using System.Runtime.InteropServices;
using System.Text;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Classifies a HID device's transport type by combining:
///   1. Fast path — device path string heuristics (BT service GUIDs, BTHENUM, etc.)
///   2. PnP tree walk — CfgMgr32 CM_Locate_DevNodeW → CM_Get_Parent chain
///      to inspect the device's ancestor enumerator instance IDs.
/// </summary>
/// <remarks>
/// <para><b>ClassifyInstanceId precedence (exact order):</b></para>
/// <list type="number">
///   <item>Contains "BTHLEDEVICE" or "BTHLE" → <see cref="BusType.BluetoothLE"/></item>
///   <item>Contains "BTHENUM" → <see cref="BusType.BluetoothClassic"/></item>
///   <item>Starts with "BTH" → <see cref="BusType.BluetoothLE"/></item>
///   <item>VID_045E + PID_02FE or PID_02E6 → <see cref="BusType.XboxWirelessAdapter"/></item>
///   <item>Starts with "USB\VID_" → <see cref="BusType.Usb"/></item>
///   <item>Otherwise → <see cref="BusType.Unknown"/></item>
/// </list>
/// </remarks>
public sealed class PnpBusDetector : IPnpBusDetector
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int CR_SUCCESS        = 0;
    private const uint CM_FLAGS_NONE    = 0;
    private const int MAX_DEVICE_ID_LEN = 400;
    private const int MAX_PARENT_LEVELS = 12;

    // Bluetooth HID-over-GATT service GUIDs (Xbox Series X/S and BLE HID gamepads)
    private const string BleHogpGuid  = "{00001812-0000-1000-8000-00805f9b34fb}";
    // HID-over-GATT classic profile UUID (older Xbox One, most BT HID controllers)
    private const string BtHidGuid    = "{00001124-0000-1000-8000-00805f9b34fb}";

    // ── CfgMgr32 P/Invoke ─────────────────────────────────────────────────────

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst,
        [MarshalAs(UnmanagedType.LPWStr)] string pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(
        out uint pdnDevInst,
        uint dnDevInst,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(
        uint dnDevInst,
        StringBuilder Buffer,
        uint BufferLen,
        uint ulFlags);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public BusType DetectBusType(string hidDevicePath)
    {
        try
        {
            return DetectBusTypeCore(hidDevicePath);
        }
        catch
        {
            return BusType.Unknown;
        }
    }

    private BusType DetectBusTypeCore(string hidDevicePath)
    {
        // ── Fast path 1: BT service GUIDs embedded in the HID path ───────────
        // These GUIDs appear in BLE HID paths (the HOGP profile UUID is in the
        // path itself before the first #, making them reliable without a tree walk).
        if (hidDevicePath.IndexOf(BleHogpGuid, StringComparison.OrdinalIgnoreCase) >= 0 ||
            hidDevicePath.IndexOf(BtHidGuid,   StringComparison.OrdinalIgnoreCase) >= 0)
            return BusType.BluetoothLE;

        // ── Fast path 2: classify the path string directly ───────────────────
        var directResult = ClassifyInstanceId(hidDevicePath);
        if (directResult != BusType.Unknown)
            return directResult;

        // ── CfgMgr32 tree walk ────────────────────────────────────────────────
        string instanceId = DevicePathToInstanceId(hidDevicePath);

        if (CM_Locate_DevNodeW(out uint devNode, instanceId, CM_FLAGS_NONE) != CR_SUCCESS)
            return BusType.Unknown;

        for (int level = 0; level < MAX_PARENT_LEVELS; level++)
        {
            var sb = new StringBuilder(MAX_DEVICE_ID_LEN);
            if (CM_Get_Device_IDW(devNode, sb, (uint)sb.Capacity, CM_FLAGS_NONE) == CR_SUCCESS)
            {
                var result = ClassifyInstanceId(sb.ToString());
                if (result != BusType.Unknown)
                    return result;
            }

            // Walk up to parent.
            if (CM_Get_Parent(out uint parent, devNode, CM_FLAGS_NONE) != CR_SUCCESS)
                break;
            devNode = parent;
        }

        return BusType.Unknown;
    }

    // ── Path → Instance ID conversion ────────────────────────────────────────

    /// <summary>
    /// Converts an HID device path to a PnP device instance ID suitable for
    /// CM_Locate_DevNodeW.
    /// <example>
    ///   "\\?\hid#vid_045e&amp;pid_02ff&amp;ig_00#7&amp;286a539d&amp;1&amp;0000#{4d1e55b2-...}"
    ///   → "HID\VID_045E&amp;PID_02FF&amp;IG_00\7&amp;286A539D&amp;1&amp;0000"
    /// </example>
    /// </summary>
    internal static string DevicePathToInstanceId(string devicePath)
    {
        string s = devicePath;

        // Strip \\?\ prefix.
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal))
            s = s.Substring(4);

        // Remove trailing #{GUID} — the interface GUID appended by Windows.
        int lastHashBrace = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (lastHashBrace >= 0)
            s = s.Substring(0, lastHashBrace);

        // Replace '#' separators with '\' (Windows instance ID format).
        s = s.Replace('#', '\\');

        return s.ToUpperInvariant();
    }

    // ── ClassifyInstanceId ────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a single device instance ID (or path fragment) by its content.
    /// Checks are applied in strict precedence order as documented on the class.
    /// </summary>
    /// <remarks>
    /// This method is <see langword="public static"/> so unit tests can call it
    /// directly without needing P/Invoke or a real device tree.
    /// </remarks>
    public static BusType ClassifyInstanceId(string instanceId)
    {
        // 1. Bluetooth LE — BTHLEDEVICE or BTHLE enumerator prefix
        if (instanceId.IndexOf("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) >= 0 ||
            instanceId.IndexOf("BTHLE",       StringComparison.OrdinalIgnoreCase) >= 0)
            return BusType.BluetoothLE;

        // 2. Bluetooth Classic — BTHENUM enumerator
        if (instanceId.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) >= 0)
            return BusType.BluetoothClassic;

        // 3. Catch-all for remaining BTH-prefixed bus names → treat as BLE
        if (instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
            return BusType.BluetoothLE;

        // 4. Xbox Wireless Adapter (USB dongle) — VID 045E, PID 02FE or 02E6
        if (instanceId.IndexOf("VID_045E", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (instanceId.IndexOf("PID_02FE", StringComparison.OrdinalIgnoreCase) >= 0 ||
             instanceId.IndexOf("PID_02E6", StringComparison.OrdinalIgnoreCase) >= 0))
            return BusType.XboxWirelessAdapter;

        // 5. USB — device or parent starts with "USB\VID_"
        if (instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase))
            return BusType.Usb;

        return BusType.Unknown;
    }
}
