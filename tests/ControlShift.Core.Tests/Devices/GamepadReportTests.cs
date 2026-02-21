using FluentAssertions;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Tests.Devices;

public class GamepadReportTests
{
    [Fact]
    public void Default_AllFieldsZero()
    {
        var report = default(GamepadReport);

        report.Buttons.Should().Be(0);
        report.LeftTrigger.Should().Be(0);
        report.RightTrigger.Should().Be(0);
        report.ThumbLX.Should().Be(0);
        report.ThumbLY.Should().Be(0);
        report.ThumbRX.Should().Be(0);
        report.ThumbRY.Should().Be(0);
    }

    [Fact]
    public void Constructor_SetsAllFields()
    {
        var report = new GamepadReport(
            Buttons: 0x100F,
            LeftTrigger: 200,
            RightTrigger: 100,
            ThumbLX: 1000,
            ThumbLY: -1000,
            ThumbRX: 2000,
            ThumbRY: -2000);

        report.Buttons.Should().Be(0x100F);
        report.LeftTrigger.Should().Be(200);
        report.RightTrigger.Should().Be(100);
        report.ThumbLX.Should().Be(1000);
        report.ThumbLY.Should().Be(-1000);
        report.ThumbRX.Should().Be(2000);
        report.ThumbRY.Should().Be(-2000);
    }

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new GamepadReport(0x1000, 255, 128, 100, -100, 200, -200);
        var b = new GamepadReport(0x1000, 255, 128, 100, -100, 200, -200);

        a.Should().Be(b);
    }

    [Fact]
    public void RecordEquality_DifferentButtons_AreNotEqual()
    {
        var a = new GamepadReport(0x1000, 255, 128, 100, -100, 200, -200);
        var b = new GamepadReport(0x2000, 255, 128, 100, -100, 200, -200);

        a.Should().NotBe(b);
    }

    [Fact]
    public void GuideButton_0x0400_CanBeDetected()
    {
        // The Guide button is at bit 10 (0x0400). This is the bit that
        // ViGEmController must strip before forwarding.
        const ushort guideButtonMask = 0x0400;
        var report = new GamepadReport(Buttons: guideButtonMask,
            LeftTrigger: 0, RightTrigger: 0,
            ThumbLX: 0, ThumbLY: 0, ThumbRX: 0, ThumbRY: 0);

        (report.Buttons & guideButtonMask).Should().NotBe(0,
            "Guide button should be detectable via 0x0400 mask");
    }

    [Fact]
    public void GuideButton_Stripped_OtherButtonsPreserved()
    {
        const ushort guideButtonMask = 0x0400;
        // All buttons pressed including Guide
        ushort allButtons = 0xFFFF;
        ushort stripped = (ushort)(allButtons & ~guideButtonMask);

        stripped.Should().Be(0xFBFF, "all buttons except Guide should remain");
        (stripped & guideButtonMask).Should().Be(0, "Guide bit should be cleared");
    }

    [Fact]
    public void ExtremeValues_AllMax()
    {
        var report = new GamepadReport(
            Buttons: ushort.MaxValue,
            LeftTrigger: byte.MaxValue,
            RightTrigger: byte.MaxValue,
            ThumbLX: short.MaxValue,
            ThumbLY: short.MaxValue,
            ThumbRX: short.MaxValue,
            ThumbRY: short.MaxValue);

        report.Buttons.Should().Be(ushort.MaxValue);
        report.LeftTrigger.Should().Be(byte.MaxValue);
        report.ThumbLX.Should().Be(short.MaxValue);
    }

    [Fact]
    public void ExtremeValues_AllMin()
    {
        var report = new GamepadReport(
            Buttons: 0,
            LeftTrigger: 0,
            RightTrigger: 0,
            ThumbLX: short.MinValue,
            ThumbLY: short.MinValue,
            ThumbRX: short.MinValue,
            ThumbRY: short.MinValue);

        report.ThumbLX.Should().Be(short.MinValue);
        report.ThumbRY.Should().Be(short.MinValue);
    }
}
