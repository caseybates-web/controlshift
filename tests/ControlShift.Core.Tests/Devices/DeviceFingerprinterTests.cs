using FluentAssertions;
using ControlShift.Core.Devices;
using ControlShift.Core.Models;

namespace ControlShift.Core.Tests.Devices;

public class DeviceFingerprinterTests
{
    private static string TestDataPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "known-devices-test.json");

    private DeviceFingerprinter CreateFingerprinter()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);
        return new DeviceFingerprinter(db);
    }

    [Fact]
    public void IdentifyControllers_MarksKnownDeviceAsIntegrated()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = true, BatteryType = "Wired" }
        };

        var hid = new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "0B05", Pid = "1ABE",
                DevicePath = @"\\?\HID#VID_0B05&PID_1ABE#0001",
                ProductName = "ROG Ally Gamepad"
            }
        };

        var result = fp.IdentifyControllers(xinput, hid);

        result.Should().ContainSingle(c => c.IsIntegratedGamepad);
        result[0].DisplayName.Should().Be("ASUS ROG Ally MCU Gamepad");
        result[0].ConnectionType.Should().Be(ConnectionType.Integrated);
    }

    [Fact]
    public void IdentifyControllers_DisconnectedSlotsAreEmpty()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = false },
            new() { SlotIndex = 1, IsConnected = false },
            new() { SlotIndex = 2, IsConnected = false },
            new() { SlotIndex = 3, IsConnected = false }
        };

        var result = fp.IdentifyControllers(xinput, new List<HidDeviceInfo>());

        result.Should().HaveCount(4);
        result.Should().OnlyContain(c => !c.IsConnected);
    }

    [Fact]
    public void IdentifyControllers_WiredBatteryMeansUsb()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = true, BatteryType = "Wired" }
        };

        var hid = new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "045E", Pid = "02FD",
                DevicePath = @"\\?\HID#VID_045E&PID_02FD#0001",
                ProductName = "Xbox Controller"
            }
        };

        var result = fp.IdentifyControllers(xinput, hid);

        result[0].ConnectionType.Should().Be(ConnectionType.Usb);
        result[0].IsIntegratedGamepad.Should().BeFalse();
    }

    [Fact]
    public void IdentifyControllers_WirelessBatteryMeansBluetooth()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 1, IsConnected = true, BatteryType = "Alkaline", BatteryLevel = 2 }
        };

        var hid = new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "045E", Pid = "02FD",
                DevicePath = @"\\?\HID#VID_045E&PID_02FD#0001",
                ProductName = "Xbox Wireless Controller"
            }
        };

        var result = fp.IdentifyControllers(xinput, hid);

        result.Should().Contain(c => c.ConnectionType == ConnectionType.Bluetooth);
    }

    [Fact]
    public void IdentifyControllers_UsesProductNameWhenNotKnown()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = true, BatteryType = "Wired" }
        };

        var hid = new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "054C", Pid = "0CE6",
                DevicePath = @"\\?\HID#VID_054C&PID_0CE6#0001",
                ProductName = "DualSense Wireless Controller"
            }
        };

        var result = fp.IdentifyControllers(xinput, hid);

        result[0].DisplayName.Should().Be("DualSense Wireless Controller");
        result[0].IsIntegratedGamepad.Should().BeFalse();
    }

    [Fact]
    public void IdentifyControllers_FallbackNameWhenNoHidMatch()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 2, IsConnected = true, BatteryType = "Wired" }
        };

        var result = fp.IdentifyControllers(xinput, new List<HidDeviceInfo>());

        result[0].DisplayName.Should().Be("Controller (Slot 2)");
    }

    [Fact]
    public void IdentifyControllers_MultipleControllers_MatchesCorrectly()
    {
        var fp = CreateFingerprinter();

        var xinput = new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = true, BatteryType = "Wired" },
            new() { SlotIndex = 1, IsConnected = true, BatteryType = "Alkaline", BatteryLevel = 3 }
        };

        var hid = new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "0B05", Pid = "1ABE",
                DevicePath = @"\\?\HID#VID_0B05&PID_1ABE#0001",
                ProductName = "ROG Ally Gamepad"
            },
            new()
            {
                Vid = "045E", Pid = "02FD",
                DevicePath = @"\\?\HID#VID_045E&PID_02FD#0001",
                ProductName = "Xbox Wireless Controller"
            }
        };

        var result = fp.IdentifyControllers(xinput, hid);

        result.Should().HaveCount(2);
        // Slot 0 should match the known integrated gamepad
        result[0].IsIntegratedGamepad.Should().BeTrue();
        result[0].DisplayName.Should().Be("ASUS ROG Ally MCU Gamepad");
        // Slot 1 should match the Xbox controller
        result[1].IsIntegratedGamepad.Should().BeFalse();
        result[1].DisplayName.Should().Contain("Xbox");
    }
}
