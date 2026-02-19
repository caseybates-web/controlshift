using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Tests.Enumeration;

public class HidPathParserTests
{
    // ── ExtractBthVid ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractBthVid_BthenumPath_ReturnsVidStrippingSubcode()
    {
        // Real-world BTHENUM path: VID&{subcode:4}{vid:4}
        const string path =
            @"\\?\BTHENUM#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&02e0#7&abc&0&A0B45678_00000001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal("045E", HidPathParser.ExtractBthVid(path));
    }

    [Theory]
    [InlineData(@"\\?\BTHENUM#..._VID&0002045e_PID&02e0", "045E")]   // Xbox Wireless (045E)
    [InlineData(@"\\?\BTHENUM#..._VID&0002054c_PID&05c4", "054C")]   // DualShock 4 (054C)
    [InlineData(@"\\?\BTHENUM#..._VID&00020079_PID&0006", "0079")]   // Generic BT (0079)
    [InlineData(@"\\?\BTHENUM#..._VID&00001234_PID&ABCD", "1234")]   // Subcode 0000, VID 1234
    public void ExtractBthVid_VariousVids_StripsSubcodeCorrectly(string path, string expectedVid)
    {
        Assert.Equal(expectedVid, HidPathParser.ExtractBthVid(path));
    }

    [Fact]
    public void ExtractBthVid_UsbPath_ReturnsNull()
    {
        // USB paths use "VID_" (underscore), not "VID&" — should not match
        const string path =
            @"\\?\HID#VID_045E&PID_028E&IG_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Null(HidPathParser.ExtractBthVid(path));
    }

    [Fact]
    public void ExtractBthVid_NoVidSegment_ReturnsNull()
    {
        Assert.Null(HidPathParser.ExtractBthVid(@"\\?\HID#SomeRandomPath"));
    }

    [Fact]
    public void ExtractBthVid_VidSegmentTooShort_ReturnsNull()
    {
        // "VID&04" — only 2 chars after VID&, need 8
        Assert.Null(HidPathParser.ExtractBthVid(@"\\?\BTHENUM#VID&045e"));
    }

    [Fact]
    public void ExtractBthVid_ResultIsUppercase()
    {
        const string path = @"\\?\BTHENUM#..._VID&0002045e_PID&02e0";
        var vid = HidPathParser.ExtractBthVid(path);
        Assert.Equal(vid, vid?.ToUpperInvariant());
    }

    // ── ExtractBthPid ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractBthPid_BthenumPath_ReturnsPid()
    {
        const string path =
            @"\\?\BTHENUM#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&02e0#7&abc&0&A0B45678_00000001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal("02E0", HidPathParser.ExtractBthPid(path));
    }

    [Theory]
    [InlineData(@"\\?\BTHENUM#..._VID&0002045e_PID&02e0", "02E0")]   // Xbox Wireless BT
    [InlineData(@"\\?\BTHENUM#..._VID&0002054c_PID&05c4", "05C4")]   // DualShock 4
    [InlineData(@"\\?\BTHENUM#..._VID&00020079_PID&0006", "0006")]   // Generic BT gamepad
    public void ExtractBthPid_VariousPids_ReturnsCorrectValue(string path, string expectedPid)
    {
        Assert.Equal(expectedPid, HidPathParser.ExtractBthPid(path));
    }

    [Fact]
    public void ExtractBthPid_UsbPath_ReturnsNull()
    {
        // USB paths use "PID_" (underscore), not "PID&"
        const string path =
            @"\\?\HID#VID_045E&PID_028E&IG_00#7&abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Null(HidPathParser.ExtractBthPid(path));
    }

    [Fact]
    public void ExtractBthPid_NoPidSegment_ReturnsNull()
    {
        Assert.Null(HidPathParser.ExtractBthPid(@"\\?\HID#SomeRandomPath"));
    }

    [Fact]
    public void ExtractBthPid_ResultIsUppercase()
    {
        const string path = @"\\?\BTHENUM#..._VID&0002045e_PID&02e0";
        var pid = HidPathParser.ExtractBthPid(path);
        Assert.Equal(pid, pid?.ToUpperInvariant());
    }

    // ── Round-trip with real-world Xbox Wireless Controller BT path ───────────

    [Fact]
    public void ExtractBthVidPid_XboxWirelessBtPath_ReturnsCorrectPair()
    {
        // Actual path format observed for Xbox Wireless Controller (045E:02E0) over BT
        const string path =
            @"\\?\BTHENUM#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&02e0_REV&0105#7&14f5c9b9&0&A0B456789ABC_00000001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal("045E", HidPathParser.ExtractBthVid(path));
        Assert.Equal("02E0", HidPathParser.ExtractBthPid(path));
    }
}
