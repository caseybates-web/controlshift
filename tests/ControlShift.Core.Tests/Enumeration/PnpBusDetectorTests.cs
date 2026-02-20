using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Tests.Enumeration;

/// <summary>
/// Unit tests for <see cref="PnpBusDetector"/>.
/// <para>
/// Only the static, pure-string methods are tested here — <see cref="PnpBusDetector.ClassifyInstanceId"/>
/// and <see cref="PnpBusDetector.DevicePathToInstanceId"/> (internal).
/// The CfgMgr32 tree-walk in <see cref="PnpBusDetector.DetectBusType"/> requires a real
/// Windows device tree and is covered by manual testing on hardware.
/// </para>
/// </summary>
public class PnpBusDetectorTests
{
    // ── ClassifyInstanceId — BluetoothLE ─────────────────────────────────────

    [Theory]
    [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}\AC8EBD3139AA_C")]
    [InlineData(@"bthledevice\something")]
    [InlineData(@"BTHLEDevice\{foo}\bar")]
    public void ClassifyInstanceId_BthleDevice_ReturnsBluetoothLE(string id)
        => Assert.Equal(BusType.BluetoothLE, PnpBusDetector.ClassifyInstanceId(id));

    [Theory]
    [InlineData(@"BTHLE\{00001812-0000-1000-8000-00805f9b34fb}\bar")]
    [InlineData(@"bthle\something")]
    public void ClassifyInstanceId_BthlePrefix_ReturnsBluetoothLE(string id)
        => Assert.Equal(BusType.BluetoothLE, PnpBusDetector.ClassifyInstanceId(id));

    [Theory]
    [InlineData(@"BTH\something")]
    [InlineData(@"bth\anything")]
    [InlineData(@"BTH_LE_FALLBACK\foo")]
    public void ClassifyInstanceId_BthPrefix_ReturnsBluetoothLE(string id)
        => Assert.Equal(BusType.BluetoothLE, PnpBusDetector.ClassifyInstanceId(id));

    // ── ClassifyInstanceId — BluetoothClassic ────────────────────────────────

    [Theory]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_LOCALMFG&0000\7&abc")]
    [InlineData(@"bthenum\something")]
    [InlineData(@"HID\BTHENUM\nested")]
    public void ClassifyInstanceId_BthEnum_ReturnsBluetoothClassic(string id)
        => Assert.Equal(BusType.BluetoothClassic, PnpBusDetector.ClassifyInstanceId(id));

    // ── ClassifyInstanceId — XboxWirelessAdapter ─────────────────────────────

    [Theory]
    [InlineData(@"USB\VID_045E&PID_02FE&MI_00\7&abc&0&0000")]
    [InlineData(@"USB\VID_045e&PID_02fe\7&abc")]
    public void ClassifyInstanceId_XboxWirelessAdapter02FE_ReturnsXboxWirelessAdapter(string id)
        => Assert.Equal(BusType.XboxWirelessAdapter, PnpBusDetector.ClassifyInstanceId(id));

    [Theory]
    [InlineData(@"USB\VID_045E&PID_02E6&MI_00\7&abc&0&0000")]
    [InlineData(@"USB\VID_045e&PID_02e6\something")]
    public void ClassifyInstanceId_XboxWirelessAdapter02E6_ReturnsXboxWirelessAdapter(string id)
        => Assert.Equal(BusType.XboxWirelessAdapter, PnpBusDetector.ClassifyInstanceId(id));

    [Fact]
    public void ClassifyInstanceId_XboxVidButDifferentPid_DoesNotReturnXboxWirelessAdapter()
    {
        // VID 045E + non-WA PID → should fall through to USB check, not XboxWirelessAdapter.
        const string id = @"USB\VID_045E&PID_028E&MI_00\7&abc";
        var result = PnpBusDetector.ClassifyInstanceId(id);
        Assert.NotEqual(BusType.XboxWirelessAdapter, result);
        Assert.Equal(BusType.Usb, result);
    }

    // ── ClassifyInstanceId — USB ─────────────────────────────────────────────

    [Theory]
    [InlineData(@"USB\VID_045E&PID_028E&MI_00\7&abc&0&0000")]
    [InlineData(@"USB\VID_054C&PID_05C4&MI_00\7&def")]
    [InlineData(@"usb\vid_0079&pid_0006\instance")]
    public void ClassifyInstanceId_UsbParentNode_ReturnsUsb(string id)
        => Assert.Equal(BusType.Usb, PnpBusDetector.ClassifyInstanceId(id));

    // ── ClassifyInstanceId — Unknown ─────────────────────────────────────────

    [Theory]
    [InlineData(@"HID\VID_045E&PID_02FF&IG_00\7&286a539d&1&0000")]                 // HID node itself
    [InlineData(@"HID\{00001812-0000-1000-8000-00805F9B34FB}&DEV&VID_045E\9&abc")]  // BLE HID node
    [InlineData(@"ROOT\COMPOSITEBUS\0")]
    [InlineData("")]
    [InlineData("UNKNOWN_BUS\\something")]
    public void ClassifyInstanceId_UnrecognizedId_ReturnsUnknown(string id)
        => Assert.Equal(BusType.Unknown, PnpBusDetector.ClassifyInstanceId(id));

    // ── ClassifyInstanceId — precedence ──────────────────────────────────────

    [Fact]
    public void ClassifyInstanceId_BthleBeforeBthenum_ReturnsBluetoothLE()
    {
        // If both BTHLE and BTHENUM appear (unusual), BTHLE takes precedence (rule 1 before rule 2).
        const string id = "BTHLE\\BTHENUM\\something";
        Assert.Equal(BusType.BluetoothLE, PnpBusDetector.ClassifyInstanceId(id));
    }

    // ── DevicePathToInstanceId ────────────────────────────────────────────────

    [Fact]
    public void DevicePathToInstanceId_StandardUsbHidPath_ConvertsCorrectly()
    {
        const string path =
            @"\\?\hid#vid_045e&pid_02ff&ig_00#7&286a539d&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string expected =
            @"HID\VID_045E&PID_02FF&IG_00\7&286A539D&1&0000";

        Assert.Equal(expected, PnpBusDetector.DevicePathToInstanceId(path));
    }

    [Fact]
    public void DevicePathToInstanceId_BleHogpPath_ConvertsCorrectly()
    {
        const string path =
            @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}&dev&vid_045e&pid_0b13&rev_0509&ac8ebd3139aa&ig_00#9&3a22ae3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string expected =
            @"HID\{00001812-0000-1000-8000-00805F9B34FB}&DEV&VID_045E&PID_0B13&REV_0509&AC8EBD3139AA&IG_00\9&3A22AE3&0&0000";

        Assert.Equal(expected, PnpBusDetector.DevicePathToInstanceId(path));
    }

    [Fact]
    public void DevicePathToInstanceId_ResultIsUppercase()
    {
        const string path = @"\\?\hid#vid_045e&pid_02ff&ig_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var result = PnpBusDetector.DevicePathToInstanceId(path);
        Assert.Equal(result.ToUpperInvariant(), result);
    }

    [Fact]
    public void DevicePathToInstanceId_PoundSignsSeparatorsReplacedByBackslashes()
    {
        const string path = @"\\?\hid#abc#def#0#{guid}";
        var result = PnpBusDetector.DevicePathToInstanceId(path);
        // After stripping \\?\ and trailing #{guid}, we have hid#abc#def#0
        // After replacing # with \: HID\ABC\DEF\0
        Assert.Equal(@"HID\ABC\DEF\0", result);
    }

    [Fact]
    public void DevicePathToInstanceId_NoPrefix_StillWorks()
    {
        // Path without \\?\ — should still strip trailing #{GUID} and uppercase.
        const string path = @"hid#vid_045e&pid_028e#7&abc#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var result = PnpBusDetector.DevicePathToInstanceId(path);
        Assert.Equal(@"HID\VID_045E&PID_028E\7&ABC", result);
    }

    // ── ClassifyInstanceId — real-world parent chain examples ─────────────────

    [Theory]
    [InlineData(@"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\AC8EBD3139AA")]
    [InlineData(@"BTHLEDevice\{0000110e-0000-1000-8000-00805f9b34fb}\<mac>")]
    public void ClassifyInstanceId_RealWorldBleParentNodes_ReturnsBluetoothLE(string id)
        => Assert.Equal(BusType.BluetoothLE, PnpBusDetector.ClassifyInstanceId(id));

    [Theory]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&abc&0&A0B45678_00000001")]
    [InlineData(@"BTHENUM\DEV_A0B45678CDEF\7&abc&0&A0B45678_00000001")]
    public void ClassifyInstanceId_RealWorldBtClassicParentNodes_ReturnsBluetoothClassic(string id)
        => Assert.Equal(BusType.BluetoothClassic, PnpBusDetector.ClassifyInstanceId(id));

    [Theory]
    [InlineData(@"USB\VID_045E&PID_02FE&REV_0118&MI_00\7&abc&0&0000")]
    [InlineData(@"USB\VID_045E&PID_02E6&REV_0118&MI_00\7&def&0&0000")]
    public void ClassifyInstanceId_RealWorldXboxWirelessAdapterParents_ReturnsXboxWirelessAdapter(string id)
        => Assert.Equal(BusType.XboxWirelessAdapter, PnpBusDetector.ClassifyInstanceId(id));
}
