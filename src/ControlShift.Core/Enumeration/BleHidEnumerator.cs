using System.Runtime.InteropServices;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates BLE HID devices via SetupAPI using the HOGP profile interface GUID
/// <c>{00001812-0000-1000-8000-00805F9B34FB}</c>.
/// <para>
/// HidSharp enumerates HID devices via the standard HID interface GUID
/// <c>{4d1e55b2-...}</c>. Some BLE HID devices on certain Windows builds only
/// expose the HOGP interface GUID to SetupDiGetClassDevs, and would be missed
/// by HidSharp alone. This enumerator supplements HidSharp's results.
/// </para>
/// </summary>
internal static class BleHidEnumerator
{
    // HOGP (HID-over-GATT Profile) service UUID — used by BLE HID controllers.
    private static readonly Guid BleHogpGuid =
        new Guid("00001812-0000-1000-8000-00805F9B34FB");

    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    private const uint DIGCF_PRESENT         = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    // ── SetupAPI P/Invoke ─────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint   cbSize;
        public Guid   InterfaceClassGuid;
        public uint   Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid,
        IntPtr   Enumerator,
        IntPtr   hwndParent,
        uint     Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet,
        IntPtr DeviceInfoData,
        ref Guid InterfaceClassGuid,
        uint   MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr   DeviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr   DeviceInterfaceDetailData,
        uint     DeviceInterfaceDetailDataSize,
        out uint RequiredSize,
        IntPtr   DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all HID devices exposed via the BLE HOGP interface GUID.
    /// Returns an empty list on any failure — never throws.
    /// </summary>
    internal static IReadOnlyList<HidDeviceInfo> GetBleHidDevices()
    {
        var results = new List<HidDeviceInfo>();
        try
        {
            EnumerateBleHidDevicesInto(results);
        }
        catch { /* SetupAPI failures must not propagate */ }
        return results;
    }

    private static void EnumerateBleHidDevicesInto(List<HidDeviceInfo> results)
    {
        var guid = BleHogpGuid;
        IntPtr devInfoSet = SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (devInfoSet == INVALID_HANDLE_VALUE)
            return;

        try
        {
            uint index = 0;
            var  ifaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            while (SetupDiEnumDeviceInterfaces(
                devInfoSet, IntPtr.Zero, ref guid, index++, ref ifaceData))
            {
                // First call: get required buffer size.
                SetupDiGetDeviceInterfaceDetail(
                    devInfoSet, ref ifaceData,
                    IntPtr.Zero, 0, out uint required, IntPtr.Zero);

                if (required == 0) continue;

                // Second call: get actual device path.
                IntPtr detailBuf = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA cbSize:
                    //   32-bit → 6 (DWORD cbSize + first WCHAR)
                    //   64-bit → 8 (alignment padding after DWORD)
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);

                    if (!SetupDiGetDeviceInterfaceDetail(
                            devInfoSet, ref ifaceData,
                            detailBuf, required, out _, IntPtr.Zero))
                        continue;

                    // Device path starts at offset 4 (after DWORD cbSize field).
                    string? devicePath = Marshal.PtrToStringUni(
                        IntPtr.Add(detailBuf, 4));

                    if (string.IsNullOrEmpty(devicePath)) continue;

                    // Extract VID/PID from VID_xxxx&PID_xxxx pattern in the path.
                    string? vid = ExtractFourHexAfter(devicePath, "VID_");
                    string? pid = ExtractFourHexAfter(devicePath, "PID_");
                    if (vid is null || pid is null) continue;

                    results.Add(new HidDeviceInfo(
                        vid, pid, ProductName: null, devicePath));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds <paramref name="marker"/> in <paramref name="path"/> and returns the
    /// 4 characters that follow it (uppercased), or null if not found / too short.
    /// </summary>
    private static string? ExtractFourHexAfter(string path, string marker)
    {
        int idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || idx + marker.Length + 4 > path.Length)
            return null;
        return path.Substring(idx + marker.Length, 4).ToUpperInvariant();
    }
}
