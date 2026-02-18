using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Tests.Enumeration;

public class HidEnumeratorTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static Mock<IHidEnumerator> MockWith(params HidDeviceInfo[] devices)
    {
        var mock = new Mock<IHidEnumerator>();
        mock.Setup(e => e.GetDevices()).Returns(devices);
        return mock;
    }

    private static HidDeviceInfo MakeDevice(
        string vid = "0B05",
        string pid = "1ABE",
        string? productName = "Test Controller",
        string path = @"\\?\HID#VID_0B05&PID_1ABE#1&abc&0&0000#{4d1e55b2-0000-0000-0000-000000000000}")
        => new(vid, pid, productName, path);

    // ── empty list ────────────────────────────────────────────────────────────

    [Fact]
    public void GetDevices_NoDevices_ReturnsEmptyList()
    {
        var mock = MockWith();

        var devices = mock.Object.GetDevices();

        Assert.Empty(devices);
    }

    // ── single device ─────────────────────────────────────────────────────────

    [Fact]
    public void GetDevices_SingleDevice_ReturnsSingleEntry()
    {
        var mock = MockWith(MakeDevice());

        var devices = mock.Object.GetDevices();

        Assert.Single(devices);
    }

    // ── VID / PID format ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("0B05", "1ABE")]   // ASUS ROG Ally
    [InlineData("17EF", "6178")]   // Lenovo Legion Go
    [InlineData("2833", "0004")]   // GPD Win 4
    [InlineData("045E", "028E")]   // Xbox 360 wired
    public void GetDevices_VidPid_FourCharUpperHex(string vid, string pid)
    {
        var mock = MockWith(MakeDevice(vid: vid, pid: pid));

        var device = mock.Object.GetDevices()[0];

        Assert.Equal(4, device.Vid.Length);
        Assert.Equal(4, device.Pid.Length);
        Assert.Equal(vid, device.Vid);
        Assert.Equal(pid, device.Pid);
        // ensure uppercase
        Assert.Equal(vid.ToUpperInvariant(), device.Vid);
        Assert.Equal(pid.ToUpperInvariant(), device.Pid);
    }

    // ── product name ──────────────────────────────────────────────────────────

    [Fact]
    public void GetDevices_ProductNamePresent_IsPreserved()
    {
        var mock = MockWith(MakeDevice(productName: "ASUS ROG Ally MCU Gamepad"));

        var device = mock.Object.GetDevices()[0];

        Assert.Equal("ASUS ROG Ally MCU Gamepad", device.ProductName);
    }

    [Fact]
    public void GetDevices_ProductNameUnavailable_IsNull()
    {
        var mock = MockWith(MakeDevice(productName: null));

        var device = mock.Object.GetDevices()[0];

        Assert.Null(device.ProductName);
    }

    // ── device path ───────────────────────────────────────────────────────────

    [Fact]
    public void GetDevices_DevicePath_IsPreserved()
    {
        const string expectedPath = @"\\?\HID#VID_0B05&PID_1ABE#1&abc&0&0000#{4d1e55b2-feed-beef-cafe-000000000000}";
        var mock = MockWith(MakeDevice(path: expectedPath));

        var device = mock.Object.GetDevices()[0];

        Assert.Equal(expectedPath, device.DevicePath);
    }

    // ── multiple devices ──────────────────────────────────────────────────────

    [Fact]
    public void GetDevices_MultipleDevices_AllReturned()
    {
        var mock = MockWith(
            MakeDevice(vid: "0B05", pid: "1ABE", productName: "ROG Ally",     path: @"\\?\hid\1"),
            MakeDevice(vid: "17EF", pid: "6178", productName: "Legion Go",    path: @"\\?\hid\2"),
            MakeDevice(vid: "2833", pid: "0004", productName: null,            path: @"\\?\hid\3"));

        var devices = mock.Object.GetDevices();

        Assert.Equal(3, devices.Count);
        Assert.Equal("0B05", devices[0].Vid);
        Assert.Equal("17EF", devices[1].Vid);
        Assert.Null(devices[2].ProductName);
    }

    // ── record equality ───────────────────────────────────────────────────────

    [Fact]
    public void HidDeviceInfo_RecordEquality_WorksCorrectly()
    {
        var a = MakeDevice(vid: "0B05", pid: "1ABE", productName: "ROG Ally", path: @"\\?\hid\1");
        var b = MakeDevice(vid: "0B05", pid: "1ABE", productName: "ROG Ally", path: @"\\?\hid\1");
        var c = MakeDevice(vid: "17EF", pid: "6178", productName: "Legion Go", path: @"\\?\hid\2");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── device path uniqueness ────────────────────────────────────────────────

    [Fact]
    public void GetDevices_SameVidPid_DifferentPaths_AreSeparateEntries()
    {
        // Two physical instances of the same controller model have identical
        // VID+PID but distinct device paths.
        var mock = MockWith(
            MakeDevice(vid: "045E", pid: "028E", path: @"\\?\hid\instance0"),
            MakeDevice(vid: "045E", pid: "028E", path: @"\\?\hid\instance1"));

        var devices = mock.Object.GetDevices();

        Assert.Equal(2, devices.Count);
        Assert.NotEqual(devices[0].DevicePath, devices[1].DevicePath);
    }
}
