using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Tests.Devices;

public class ControllerMatcherTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static XInputSlotInfo ConnectedSlot(
        int index,
        XInputConnectionType conn = XInputConnectionType.Wired,
        int? battery = null)
        => new(index, IsConnected: true, XInputDeviceType.Gamepad, conn, battery);

    private static XInputSlotInfo DisconnectedSlot(int index)
        => new(index, IsConnected: false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null);

    /// <summary>USB device path with the IG_0{slotIndex} marker.</summary>
    private static HidDeviceInfo UsbDevice(string vid, string pid, int slotIndex, string? productName = null)
        => new(vid, pid, productName,
               $@"\\?\HID#VID_{vid}&PID_{pid}&IG_0{slotIndex}#7&abc&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}");

    /// <summary>Bluetooth device path containing the BTHENUM marker AND an IG_0{slotIndex} marker.</summary>
    private static HidDeviceInfo BtDevice(string vid, string pid, int slotIndex)
        => new(vid, pid, null,
               $@"\\?\BTHENUM#{{00001124-0000-1000-8000-00805f9b34fb}}&IG_0{slotIndex}&VID_{vid}&PID_{pid}");

    /// <summary>Bluetooth LE device path containing BTHLEDevice AND an IG_0{slotIndex} marker.</summary>
    private static HidDeviceInfo BthleDevice(string vid, string pid, int slotIndex)
        => new(vid, pid, null,
               $@"\\?\BTHLEDevice#{{00001124-0000-1000-8000-00805f9b34fb}}&IG_0{slotIndex}&VID_{vid}&PID_{pid}");

    /// <summary>Alternate BT LE path containing BLUETOOTHLEDEVICE AND an IG_0{slotIndex} marker.</summary>
    private static HidDeviceInfo BluetoothLeDevice(string vid, string pid, int slotIndex)
        => new(vid, pid, null,
               $@"\\?\BLUETOOTHLEDEVICE#{{00001124-0000-1000-8000-00805f9b34fb}}&IG_0{slotIndex}&VID_{vid}&PID_{pid}");

    private static IDeviceFingerprinter EmptyFingerprinter()
    {
        var mock = new Mock<IDeviceFingerprinter>();
        mock.Setup(f => f.Fingerprint(It.IsAny<IReadOnlyList<HidDeviceInfo>>()))
            .Returns(new List<FingerprintedDevice>());
        return mock.Object;
    }

    private static ControllerMatcher MakeMatcher(
        IDeviceFingerprinter? fp = null,
        params KnownVendorEntry[] vendors)
        => new(new VendorDatabase(vendors), fp ?? EmptyFingerprinter());

    // ── disconnected slots ────────────────────────────────────────────────────

    [Fact]
    public void Match_DisconnectedSlot_IsConnectedFalse()
    {
        var matcher = MakeMatcher();
        var result = matcher.Match([DisconnectedSlot(0)], []);

        Assert.False(result[0].IsConnected);
    }

    [Fact]
    public void Match_DisconnectedSlot_HidAndBrandAreNull()
    {
        var matcher = MakeMatcher();
        var result = matcher.Match([DisconnectedSlot(0)], []);

        Assert.Null(result[0].Hid);
        Assert.Null(result[0].VendorBrand);
    }

    [Fact]
    public void Match_DisconnectedSlot_ConnectionTypeIsUnknown()
    {
        var matcher = MakeMatcher();
        var result = matcher.Match([DisconnectedSlot(0)], []);

        Assert.Equal(HidConnectionType.Unknown, result[0].HidConnectionType);
    }

    // ── IG_0X matching ────────────────────────────────────────────────────────

    [Fact]
    public void Match_ConnectedSlot_MatchesByIgMarker()
    {
        var matcher = MakeMatcher();
        var hid = UsbDevice("045E", "028E", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(hid, result[0].Hid);
    }

    // Exact-only matching: a HID device whose path contains IG_01 does NOT match XInput slot 0.
    [Fact]
    public void Match_WrongIgMarker_HidIsNull()
    {
        var matcher = MakeMatcher();
        // Only a HID device with IG_01 exists — XInput slot 0 requires IG_00.
        var hid = UsbDevice("045E", "028E", slotIndex: 1); // IG_01 path, XInput slot 0
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        // No fallback — exact match only, so slot 0 gets no HID device.
        Assert.Null(result[0].Hid);
    }

    [Fact]
    public void Match_NoIgMarkerAtAll_HidIsNull()
    {
        var matcher = MakeMatcher();
        // Device path contains no IG_0X marker at all — neither exact nor fallback can match.
        var hid = new HidDeviceInfo("045E", "028E", "Xbox Wireless Controller",
                                   @"\\?\HID#VID_045E&PID_028E#7&abc&0&0000#{4d1e55b2}");
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Null(result[0].Hid);
    }

    [Fact]
    public void Match_NoHidDevices_HidIsNull()
    {
        var matcher = MakeMatcher();
        var result = matcher.Match([ConnectedSlot(0)], []);

        Assert.Null(result[0].Hid);
        Assert.Equal(HidConnectionType.Unknown, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_MultipleSlots_EachMatchedByIgMarker()
    {
        var matcher = MakeMatcher();
        var hid0 = UsbDevice("045E", "028E", slotIndex: 0);
        var hid1 = UsbDevice("054C", "05C4", slotIndex: 1);
        var slots = new[] { ConnectedSlot(0), ConnectedSlot(1), DisconnectedSlot(2), DisconnectedSlot(3) };

        var result = matcher.Match(slots, [hid0, hid1]);

        Assert.Equal("045E", result[0].Hid?.Vid);
        Assert.Equal("054C", result[1].Hid?.Vid);
        Assert.Null(result[2].Hid);
        Assert.Null(result[3].Hid);
    }

    // ── vendor brand ─────────────────────────────────────────────────────────

    [Fact]
    public void Match_KnownVendorVid_VendorBrandPopulated()
    {
        var matcher = MakeMatcher(vendors: new KnownVendorEntry("045E", "Xbox"));
        var hid = UsbDevice("045E", "028E", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal("Xbox", result[0].VendorBrand);
    }

    [Fact]
    public void Match_UnknownVendorVid_VendorBrandNull()
    {
        var matcher = MakeMatcher(); // no vendor entries
        var hid = UsbDevice("FFFF", "0001", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Null(result[0].VendorBrand);
    }

    [Fact]
    public void Match_NoMatchingHid_VendorBrandNull()
    {
        var matcher = MakeMatcher(vendors: new KnownVendorEntry("045E", "Xbox"));
        var result = matcher.Match([ConnectedSlot(0)], []); // no HID devices

        Assert.Null(result[0].VendorBrand);
    }

    // ── HID connection type detection ─────────────────────────────────────────

    [Fact]
    public void Match_UsbDevicePath_ReturnsUsb()
    {
        var matcher = MakeMatcher();
        var hid = UsbDevice("045E", "028E", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Usb, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_BthenumInPath_ReturnsBluetooth()
    {
        var matcher = MakeMatcher();
        var hid = BtDevice("045E", "02E0", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Bluetooth, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_BtServiceUuidInPath_ReturnsBluetooth()
    {
        var matcher = MakeMatcher();
        var hid = new HidDeviceInfo("045E", "02E0", null,
            @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}&IG_00&VID_045E");
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Bluetooth, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_BthleDeviceInPath_ReturnsBluetooth()
    {
        var matcher = MakeMatcher();
        var hid = BthleDevice("045E", "02E0", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Bluetooth, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_BluetoothledeviceInPath_ReturnsBluetooth()
    {
        var matcher = MakeMatcher();
        var hid = BluetoothLeDevice("045E", "02E0", slotIndex: 0);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Bluetooth, result[0].HidConnectionType);
    }

    [Fact]
    public void Match_XboxSeriesXBtGuidInPath_ReturnsBluetooth()
    {
        // Xbox Series X/S controllers use HOGP profile UUID {00001812-...}
        // rather than the classic HID-over-BT UUID {00001124-...}
        var matcher = MakeMatcher();
        var hid = new HidDeviceInfo("045E", "0B13", null,
            @"\\?\HID#{00001812-0000-1000-8000-00805f9b34fb}&IG_00&VID_045E");
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal(HidConnectionType.Bluetooth, result[0].HidConnectionType);
    }

    // ── fingerprinting integration ────────────────────────────────────────────

    [Fact]
    public void Match_IntegratedGamepad_IsIntegratedGamepadTrue()
    {
        var hid = UsbDevice("0B05", "1ABE", slotIndex: 0, productName: "ROG Ally MCU");
        var fpDevice = new FingerprintedDevice(hid,
            IsIntegratedGamepad: true,
            KnownDeviceName: "ASUS ROG Ally MCU Gamepad",
            IsConfirmed: true);

        var fpMock = new Mock<IDeviceFingerprinter>();
        fpMock.Setup(f => f.Fingerprint(It.IsAny<IReadOnlyList<HidDeviceInfo>>()))
            .Returns([fpDevice]);

        var matcher = MakeMatcher(fpMock.Object);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.True(result[0].IsIntegratedGamepad);
        Assert.Equal("ASUS ROG Ally MCU Gamepad", result[0].KnownDeviceName);
        Assert.True(result[0].IsKnownDeviceConfirmed);
    }

    [Fact]
    public void Match_NonIntegratedGamepad_IsIntegratedGamepadFalse()
    {
        var hid = UsbDevice("045E", "028E", slotIndex: 0);
        var fpDevice = new FingerprintedDevice(hid,
            IsIntegratedGamepad: false,
            KnownDeviceName: null,
            IsConfirmed: false);

        var fpMock = new Mock<IDeviceFingerprinter>();
        fpMock.Setup(f => f.Fingerprint(It.IsAny<IReadOnlyList<HidDeviceInfo>>()))
            .Returns([fpDevice]);

        var matcher = MakeMatcher(fpMock.Object);
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.False(result[0].IsIntegratedGamepad);
        Assert.Null(result[0].KnownDeviceName);
    }

    // ── product name passthrough ───────────────────────────────────────────────

    [Fact]
    public void Match_HidProductName_PreservedOnMatchedController()
    {
        var matcher = MakeMatcher();
        var hid = UsbDevice("045E", "028E", slotIndex: 0, productName: "Xbox 360 Controller");
        var result = matcher.Match([ConnectedSlot(0)], [hid]);

        Assert.Equal("Xbox 360 Controller", result[0].Hid?.ProductName);
    }

    // ── result count ─────────────────────────────────────────────────────────

    [Fact]
    public void Match_FourSlots_AlwaysReturnsFour()
    {
        var matcher = MakeMatcher();
        var slots = Enumerable.Range(0, 4).Select(i => DisconnectedSlot(i)).ToArray();
        var result = matcher.Match(slots, []);

        Assert.Equal(4, result.Count);
    }
}
