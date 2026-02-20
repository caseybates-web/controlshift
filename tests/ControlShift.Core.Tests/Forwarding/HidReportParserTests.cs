using FluentAssertions;
using ControlShift.Core.Forwarding;

namespace ControlShift.Core.Tests.Forwarding;

public class HidReportParserTests
{
    [Fact]
    public void Parse_TooShort_ReturnsDefault()
    {
        var raw = new byte[5]; // well below MinReportLength
        var report = HidReportParser.Parse(raw);

        report.Buttons.Should().Be(0);
        report.LeftTrigger.Should().Be(0);
        report.RightTrigger.Should().Be(0);
        report.ThumbLX.Should().Be(0);
        report.ThumbLY.Should().Be(0);
        report.ThumbRX.Should().Be(0);
        report.ThumbRY.Should().Be(0);
    }

    [Fact]
    public void Parse_Empty_ReturnsDefault()
    {
        var report = HidReportParser.Parse(ReadOnlySpan<byte>.Empty);
        report.Should().Be(default(Devices.GamepadReport));
    }

    [Fact]
    public void Parse_AllZeros_ReturnsZeroedReport()
    {
        var raw = new byte[20];
        var report = HidReportParser.Parse(raw);

        report.Buttons.Should().Be(0);
        report.LeftTrigger.Should().Be(0);
        report.RightTrigger.Should().Be(0);
        report.ThumbLX.Should().Be(0);
        report.ThumbLY.Should().Be(0);
        report.ThumbRX.Should().Be(0);
        report.ThumbRY.Should().Be(0);
    }

    [Fact]
    public void Parse_ButtonA_Pressed()
    {
        // Button A = bit 12 = 0x1000
        var raw = new byte[20];
        raw[2] = 0x00; // low byte of buttons
        raw[3] = 0x10; // high byte → 0x1000
        var report = HidReportParser.Parse(raw);

        report.Buttons.Should().Be(0x1000);
    }

    [Fact]
    public void Parse_AllButtons_Pressed()
    {
        var raw = new byte[20];
        raw[2] = 0xFF;
        raw[3] = 0xFF;
        var report = HidReportParser.Parse(raw);

        report.Buttons.Should().Be(0xFFFF);
    }

    [Fact]
    public void Parse_Triggers_MaxValue()
    {
        var raw = new byte[20];
        raw[4] = 255; // left trigger
        raw[5] = 128; // right trigger
        var report = HidReportParser.Parse(raw);

        report.LeftTrigger.Should().Be(255);
        report.RightTrigger.Should().Be(128);
    }

    [Fact]
    public void Parse_LeftThumbstick_PositiveValues()
    {
        var raw = new byte[20];
        // ThumbLX = 10000 (0x2710 LE → bytes 0x10, 0x27)
        raw[6] = 0x10;
        raw[7] = 0x27;
        // ThumbLY = -5000 (0xEC78 LE → bytes 0x78, 0xEC)
        raw[8] = 0x78;
        raw[9] = 0xEC;
        var report = HidReportParser.Parse(raw);

        report.ThumbLX.Should().Be(10000);
        report.ThumbLY.Should().Be(-5000);
    }

    [Fact]
    public void Parse_RightThumbstick_ExtremeValues()
    {
        var raw = new byte[20];
        // ThumbRX = short.MaxValue = 32767 (0x7FFF LE → 0xFF, 0x7F)
        raw[10] = 0xFF;
        raw[11] = 0x7F;
        // ThumbRY = short.MinValue = -32768 (0x8000 LE → 0x00, 0x80)
        raw[12] = 0x00;
        raw[13] = 0x80;
        var report = HidReportParser.Parse(raw);

        report.ThumbRX.Should().Be(short.MaxValue);
        report.ThumbRY.Should().Be(short.MinValue);
    }

    [Fact]
    public void Parse_FullReport_AllFieldsPopulated()
    {
        var raw = new byte[20];
        raw[0] = 0x00; // report ID
        raw[1] = 0x14; // report size
        raw[2] = 0x0F; // buttons low (D-pad all)
        raw[3] = 0x10; // buttons high (Start)
        raw[4] = 200;  // left trigger
        raw[5] = 100;  // right trigger
        raw[6] = 0xE8; raw[7] = 0x03;   // ThumbLX = 1000
        raw[8] = 0x18; raw[9] = 0xFC;   // ThumbLY = -1000
        raw[10] = 0xD0; raw[11] = 0x07; // ThumbRX = 2000
        raw[12] = 0x30; raw[13] = 0xF8; // ThumbRY = -2000

        var report = HidReportParser.Parse(raw);

        report.Buttons.Should().Be(0x100F);
        report.LeftTrigger.Should().Be(200);
        report.RightTrigger.Should().Be(100);
        report.ThumbLX.Should().Be(1000);
        report.ThumbLY.Should().Be(-1000);
        report.ThumbRX.Should().Be(2000);
        report.ThumbRY.Should().Be(-2000);
    }

    [Fact]
    public void Parse_ExactlyMinLength_Works()
    {
        var raw = new byte[HidReportParser.MinReportLength];
        raw[4] = 42;
        var report = HidReportParser.Parse(raw);

        report.LeftTrigger.Should().Be(42);
    }

    [Fact]
    public void Parse_OneBelowMinLength_ReturnsDefault()
    {
        var raw = new byte[HidReportParser.MinReportLength - 1];
        var report = HidReportParser.Parse(raw);

        report.Should().Be(default(Devices.GamepadReport));
    }
}
