using FluentAssertions;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Tests.Devices;

public class DevicePathConverterTests
{
    [Theory]
    [InlineData(
        @"\\?\HID#VID_045E&PID_028E&IG_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"HID\VID_045E&PID_028E&IG_00\7&abc&0&0000")]
    [InlineData(
        @"\\?\HID#VID_0B05&PID_1ABE&IG_00#1&2abc3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        @"HID\VID_0B05&PID_1ABE&IG_00\1&2abc3&0&0000")]
    public void ToInstanceId_StandardPaths(string devicePath, string expected)
    {
        DevicePathConverter.ToInstanceId(devicePath).Should().Be(expected);
    }

    [Fact]
    public void ToInstanceId_NoPrefix_StillConverts()
    {
        var input = @"HID#VID_045E&PID_028E&IG_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var result = DevicePathConverter.ToInstanceId(input);

        result.Should().Be(@"HID\VID_045E&PID_028E&IG_00\7&abc&0&0000");
    }

    [Fact]
    public void ToInstanceId_NoGuid_StillConverts()
    {
        var input = @"\\?\HID#VID_045E&PID_028E&IG_00#7&abc&0&0000";
        var result = DevicePathConverter.ToInstanceId(input);

        result.Should().Be(@"HID\VID_045E&PID_028E&IG_00\7&abc&0&0000");
    }

    [Fact]
    public void ToInstanceId_ThrowsOnNull()
    {
        var act = () => DevicePathConverter.ToInstanceId(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToInstanceId_ThrowsOnWhitespace()
    {
        var act = () => DevicePathConverter.ToInstanceId("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToInstanceId_BluetoothPath()
    {
        // Bluetooth HID paths contain BTHENUM instead of HID
        var input = @"\\?\BTHENUM#VID_045E&PID_028E&IG_00#7&abc#{guid}";
        var result = DevicePathConverter.ToInstanceId(input);

        result.Should().StartWith(@"BTHENUM\");
        result.Should().NotContain("#");
        result.Should().NotContain("{");
    }
}
