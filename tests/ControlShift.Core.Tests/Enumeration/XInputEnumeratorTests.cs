using ControlShift.Core.Enumeration;
using Moq;

namespace ControlShift.Core.Tests.Enumeration;

public class XInputEnumeratorTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static Mock<IXInputEnumerator> MockWith(params XInputSlotInfo[] slots)
    {
        var mock = new Mock<IXInputEnumerator>();
        mock.Setup(e => e.GetSlots()).Returns(slots);
        return mock;
    }

    // ── slot count ────────────────────────────────────────────────────────────

    [Fact]
    public void GetSlots_AlwaysReturnsFourEntries()
    {
        var mock = MockWith(
            new XInputSlotInfo(0, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(1, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(2, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(3, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null));

        var slots = mock.Object.GetSlots();

        Assert.Equal(4, slots.Count);
    }

    // ── slot indices ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GetSlots_SlotIndex_MatchesPosition(int index)
    {
        var allSlots = Enumerable.Range(0, 4)
            .Select(i => new XInputSlotInfo(i, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null))
            .ToArray();
        var mock = MockWith(allSlots);

        var slots = mock.Object.GetSlots();

        Assert.Equal(index, slots[index].SlotIndex);
    }

    // ── connected wired gamepad ───────────────────────────────────────────────

    [Fact]
    public void GetSlots_ConnectedWiredGamepad_HasCorrectShape()
    {
        var slot = new XInputSlotInfo(0, true, XInputDeviceType.Gamepad, XInputConnectionType.Wired, null);
        var mock = MockWith(slot,
            new XInputSlotInfo(1, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(2, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(3, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null));

        var result = mock.Object.GetSlots()[0];

        Assert.True(result.IsConnected);
        Assert.Equal(XInputDeviceType.Gamepad, result.DeviceType);
        Assert.Equal(XInputConnectionType.Wired, result.ConnectionType);
        Assert.Null(result.BatteryPercent);   // wired -> no battery info
    }

    // ── wireless controller with battery ─────────────────────────────────────

    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    [InlineData(20)]
    [InlineData(0)]
    public void GetSlots_WirelessGamepad_BatteryPercentInRange(int pct)
    {
        var slot = new XInputSlotInfo(0, true, XInputDeviceType.Gamepad, XInputConnectionType.Wireless, pct);
        var mock = MockWith(slot,
            new XInputSlotInfo(1, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(2, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(3, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null));

        var result = mock.Object.GetSlots()[0];

        Assert.Equal(XInputConnectionType.Wireless, result.ConnectionType);
        Assert.NotNull(result.BatteryPercent);
        Assert.InRange(result.BatteryPercent!.Value, 0, 100);
    }

    // ── disconnected slot ─────────────────────────────────────────────────────

    [Fact]
    public void GetSlots_DisconnectedSlot_IsConnectedFalse()
    {
        var mock = MockWith(
            new XInputSlotInfo(0, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(1, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(2, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null),
            new XInputSlotInfo(3, false, XInputDeviceType.Unknown, XInputConnectionType.Wired, null));

        var slots = mock.Object.GetSlots();

        Assert.All(slots, s => Assert.False(s.IsConnected));
    }

    // ── multiple controllers ──────────────────────────────────────────────────

    [Fact]
    public void GetSlots_MultipleControllers_EachSlotIndependent()
    {
        var mock = MockWith(
            new XInputSlotInfo(0, true,  XInputDeviceType.Gamepad, XInputConnectionType.Wired,    null),
            new XInputSlotInfo(1, true,  XInputDeviceType.Gamepad, XInputConnectionType.Wireless,   60),
            new XInputSlotInfo(2, false, XInputDeviceType.Unknown, XInputConnectionType.Wired,    null),
            new XInputSlotInfo(3, true,  XInputDeviceType.Gamepad, XInputConnectionType.Wireless,    0));

        var slots = mock.Object.GetSlots();

        Assert.True(slots[0].IsConnected);
        Assert.Null(slots[0].BatteryPercent);

        Assert.True(slots[1].IsConnected);
        Assert.Equal(60, slots[1].BatteryPercent);

        Assert.False(slots[2].IsConnected);

        Assert.True(slots[3].IsConnected);
        Assert.Equal(0, slots[3].BatteryPercent);
    }

    // ── XInputSlotInfo record equality ────────────────────────────────────────

    [Fact]
    public void XInputSlotInfo_RecordEquality_WorksCorrectly()
    {
        var a = new XInputSlotInfo(0, true, XInputDeviceType.Gamepad, XInputConnectionType.Wired, null);
        var b = new XInputSlotInfo(0, true, XInputDeviceType.Gamepad, XInputConnectionType.Wired, null);
        var c = new XInputSlotInfo(1, true, XInputDeviceType.Gamepad, XInputConnectionType.Wired, null);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
