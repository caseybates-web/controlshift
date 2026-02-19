using Moq;
using FluentAssertions;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Devices;
using ControlShift.Core.Models;

namespace ControlShift.Core.Tests.Enumeration;

public class HidEnumeratorTests
{
    [Fact]
    public void MockedEnumerator_ReturnsGameControllers()
    {
        var mock = new Mock<IHidEnumerator>();
        mock.Setup(x => x.EnumerateGameControllers()).Returns(new List<HidDeviceInfo>
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
        });

        var result = mock.Object.EnumerateGameControllers();

        result.Should().HaveCount(2);
        result[0].Vid.Should().Be("0B05");
        result[0].ProductName.Should().Contain("ROG");
        result[1].Vid.Should().Be("045E");
        result[1].ProductName.Should().Contain("Xbox");
    }

    [Fact]
    public void MockedEnumerator_EmptyWhenNoControllers()
    {
        var mock = new Mock<IHidEnumerator>();
        mock.Setup(x => x.EnumerateGameControllers()).Returns(new List<HidDeviceInfo>());

        var result = mock.Object.EnumerateGameControllers();

        result.Should().BeEmpty();
    }

    [Fact]
    public void MockedEnumerator_VidPidFormattedAsUppercaseHex()
    {
        var mock = new Mock<IHidEnumerator>();
        mock.Setup(x => x.EnumerateGameControllers()).Returns(new List<HidDeviceInfo>
        {
            new()
            {
                Vid = "0B05", Pid = "1ABE",
                DevicePath = @"\\?\HID#test",
            }
        });

        var result = mock.Object.EnumerateGameControllers();

        result[0].Vid.Should().MatchRegex("^[0-9A-F]{4}$");
        result[0].Pid.Should().MatchRegex("^[0-9A-F]{4}$");
    }

    [Fact]
    public void RealEnumerator_DoesNotThrow()
    {
        // Integration test: HidSharp enumerate should not throw even with no controllers.
        var knownDb = new KnownDeviceDatabase();
        var enumerator = new HidEnumerator(knownDb);
        var act = () => enumerator.EnumerateGameControllers();
        act.Should().NotThrow();
    }

    [Fact]
    public void RealEnumerator_WithKnownVids_DoesNotThrow()
    {
        // With known devices loaded, the VID fallback path runs without error.
        var knownDb = new KnownDeviceDatabase();
        knownDb.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "known-devices-test.json"));
        var enumerator = new HidEnumerator(knownDb);
        var act = () => enumerator.EnumerateGameControllers();
        act.Should().NotThrow();
    }
}
