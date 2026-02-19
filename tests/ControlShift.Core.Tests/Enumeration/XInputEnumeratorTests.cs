using Moq;
using FluentAssertions;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Models;

namespace ControlShift.Core.Tests.Enumeration;

public class XInputEnumeratorTests
{
    [Fact]
    public void MockedEnumerator_ReturnsFourSlots()
    {
        var mock = new Mock<IXInputEnumerator>();
        mock.Setup(x => x.EnumerateSlots()).Returns(new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = true, BatteryType = "Wired", DeviceType = "Gamepad" },
            new() { SlotIndex = 1, IsConnected = false },
            new() { SlotIndex = 2, IsConnected = false },
            new() { SlotIndex = 3, IsConnected = false }
        });

        var result = mock.Object.EnumerateSlots();

        result.Should().HaveCount(4);
        result[0].IsConnected.Should().BeTrue();
        result[0].BatteryType.Should().Be("Wired");
        result[1].IsConnected.Should().BeFalse();
        result[2].IsConnected.Should().BeFalse();
        result[3].IsConnected.Should().BeFalse();
    }

    [Fact]
    public void MockedEnumerator_AllSlotsHaveValidIndex()
    {
        var mock = new Mock<IXInputEnumerator>();
        mock.Setup(x => x.EnumerateSlots()).Returns(new List<XInputSlot>
        {
            new() { SlotIndex = 0, IsConnected = false },
            new() { SlotIndex = 1, IsConnected = false },
            new() { SlotIndex = 2, IsConnected = false },
            new() { SlotIndex = 3, IsConnected = false }
        });

        var result = mock.Object.EnumerateSlots();

        result.Select(s => s.SlotIndex).Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
    }

    [Fact]
    public void MockedEnumerator_WirelessController_HasBatteryInfo()
    {
        var mock = new Mock<IXInputEnumerator>();
        mock.Setup(x => x.EnumerateSlots()).Returns(new List<XInputSlot>
        {
            new()
            {
                SlotIndex = 0,
                IsConnected = true,
                BatteryType = "Alkaline",
                BatteryLevel = 3,
                DeviceType = "Gamepad"
            }
        });

        var result = mock.Object.EnumerateSlots();

        result[0].BatteryType.Should().Be("Alkaline");
        result[0].BatteryLevel.Should().Be(3);
    }

    [Fact]
    public void RealEnumerator_ReturnsExactlyFourSlots()
    {
        // Integration test: requires Windows + XInput runtime.
        // On CI (windows-latest), this should return 4 slots (likely all disconnected).
        var enumerator = new XInputEnumerator();
        var slots = enumerator.EnumerateSlots();
        slots.Should().HaveCount(4);
        slots.Select(s => s.SlotIndex).Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
    }

    [Theory]
    [InlineData(Vortice.XInput.BatteryType.Wired, "Wired")]
    [InlineData(Vortice.XInput.BatteryType.Alkaline, "Alkaline")]
    [InlineData(Vortice.XInput.BatteryType.Nimh, "NiMH")]
    [InlineData(Vortice.XInput.BatteryType.Unknown, "Unknown")]
    [InlineData(Vortice.XInput.BatteryType.Disconnected, "Unknown")]
    public void NormalizeBatteryType_ProducesCanonicalStrings(
        Vortice.XInput.BatteryType input, string expected)
    {
        var result = XInputEnumerator.NormalizeBatteryType(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizeBatteryType_NimhProducesUppercaseNiMH()
    {
        // This is the critical case: Vortice enum ToString() returns "Nimh"
        // but we need "NiMH" to match the fingerprinter's connection type logic.
        var result = XInputEnumerator.NormalizeBatteryType(Vortice.XInput.BatteryType.Nimh);
        result.Should().Be("NiMH");
        result.Should().NotBe("Nimh", "Vortice's default ToString() casing would break fingerprinting");
    }

    [Fact]
    public void RealEnumerator_DisconnectedSlots_HaveNullBattery()
    {
        // On CI, all slots are disconnected — verify battery fields are null.
        var enumerator = new XInputEnumerator();
        var slots = enumerator.EnumerateSlots();

        foreach (var slot in slots.Where(s => !s.IsConnected))
        {
            slot.BatteryType.Should().BeNull();
            slot.BatteryLevel.Should().BeNull();
        }
    }
}
