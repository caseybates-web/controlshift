using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Tests.Devices;

public class DeviceFingerprinterTests
{
    // ── fixtures ──────────────────────────────────────────────────────────────

    private static readonly KnownDeviceEntry RogAlly =
        new("ASUS ROG Ally MCU Gamepad", "0B05", "1ABE", Confirmed: true);

    private static readonly KnownDeviceEntry LegionGo =
        new("Lenovo Legion Go", "17EF", "6178", Confirmed: false);

    private static DeviceFingerprinter MakeFingerprinter(params KnownDeviceEntry[] entries)
        => new(entries);

    private static HidDeviceInfo MakeHidDevice(string vid, string pid, string? productName = null)
        => new(vid, pid, productName, $@"\\?\hid\{vid}_{pid}");

    // ── empty inputs ──────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_NoDevices_ReturnsEmptyList()
    {
        var fp = MakeFingerprinter(RogAlly);

        var result = fp.Fingerprint([]);

        Assert.Empty(result);
    }

    [Fact]
    public void Fingerprint_NoKnownDevices_AllUnrecognised()
    {
        var fp = MakeFingerprinter();
        var devices = new[] { MakeHidDevice("0B05", "1ABE") };

        var result = fp.Fingerprint(devices);

        Assert.Single(result);
        Assert.False(result[0].IsIntegratedGamepad);
    }

    // ── matched device ────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_KnownDevice_IsIntegratedGamepadTrue()
    {
        var fp = MakeFingerprinter(RogAlly);
        var devices = new[] { MakeHidDevice("0B05", "1ABE") };

        var result = fp.Fingerprint(devices);

        Assert.True(result[0].IsIntegratedGamepad);
    }

    [Fact]
    public void Fingerprint_KnownDevice_KnownDeviceNamePopulated()
    {
        var fp = MakeFingerprinter(RogAlly);
        var devices = new[] { MakeHidDevice("0B05", "1ABE") };

        var result = fp.Fingerprint(devices);

        Assert.Equal("ASUS ROG Ally MCU Gamepad", result[0].KnownDeviceName);
    }

    [Fact]
    public void Fingerprint_ConfirmedKnownDevice_IsConfirmedTrue()
    {
        var fp = MakeFingerprinter(RogAlly); // confirmed: true
        var devices = new[] { MakeHidDevice("0B05", "1ABE") };

        var result = fp.Fingerprint(devices);

        Assert.True(result[0].IsConfirmed);
    }

    [Fact]
    public void Fingerprint_UnconfirmedKnownDevice_IsConfirmedFalse()
    {
        var fp = MakeFingerprinter(LegionGo); // confirmed: false
        var devices = new[] { MakeHidDevice("17EF", "6178") };

        var result = fp.Fingerprint(devices);

        Assert.True(result[0].IsIntegratedGamepad);
        Assert.False(result[0].IsConfirmed);
    }

    // ── unmatched device ──────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_UnknownDevice_IsIntegratedGamepadFalse()
    {
        var fp = MakeFingerprinter(RogAlly);
        var devices = new[] { MakeHidDevice("045E", "028E") }; // generic Xbox controller

        var result = fp.Fingerprint(devices);

        Assert.False(result[0].IsIntegratedGamepad);
    }

    [Fact]
    public void Fingerprint_UnknownDevice_KnownDeviceNameNull()
    {
        var fp = MakeFingerprinter(RogAlly);
        var devices = new[] { MakeHidDevice("045E", "028E") };

        var result = fp.Fingerprint(devices);

        Assert.Null(result[0].KnownDeviceName);
        Assert.False(result[0].IsConfirmed);
    }

    // ── case-insensitive matching ─────────────────────────────────────────────

    [Theory]
    [InlineData("0b05", "1abe")]   // all lowercase
    [InlineData("0B05", "1ABE")]   // all uppercase (normal)
    [InlineData("0b05", "1ABE")]   // mixed
    [InlineData("0B05", "1abe")]   // mixed other way
    public void Fingerprint_VidPidCaseInsensitive_Matches(string vid, string pid)
    {
        var fp = MakeFingerprinter(RogAlly); // stored as "0B05" / "1ABE"

        var result = fp.Fingerprint([MakeHidDevice(vid, pid)]);

        Assert.True(result[0].IsIntegratedGamepad);
    }

    // ── partial match (wrong PID) ─────────────────────────────────────────────

    [Fact]
    public void Fingerprint_SameVidDifferentPid_NoMatch()
    {
        var fp = MakeFingerprinter(RogAlly); // pid 1ABE
        var devices = new[] { MakeHidDevice("0B05", "FFFF") };

        var result = fp.Fingerprint(devices);

        Assert.False(result[0].IsIntegratedGamepad);
    }

    [Fact]
    public void Fingerprint_SamePidDifferentVid_NoMatch()
    {
        var fp = MakeFingerprinter(RogAlly); // vid 0B05
        var devices = new[] { MakeHidDevice("FFFF", "1ABE") };

        var result = fp.Fingerprint(devices);

        Assert.False(result[0].IsIntegratedGamepad);
    }

    // ── mixed list ────────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_MixedDevices_OnlyMatchesAreIntegratedGamepads()
    {
        var fp = MakeFingerprinter(RogAlly, LegionGo);
        var devices = new[]
        {
            MakeHidDevice("0B05", "1ABE"),  // ROG Ally — match
            MakeHidDevice("045E", "028E"),  // Xbox 360 — no match
            MakeHidDevice("17EF", "6178"),  // Legion Go — match
        };

        var result = fp.Fingerprint(devices);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].IsIntegratedGamepad);
        Assert.Equal("ASUS ROG Ally MCU Gamepad", result[0].KnownDeviceName);

        Assert.False(result[1].IsIntegratedGamepad);
        Assert.Null(result[1].KnownDeviceName);

        Assert.True(result[2].IsIntegratedGamepad);
        Assert.Equal("Lenovo Legion Go", result[2].KnownDeviceName);
    }

    // ── original HidDeviceInfo is preserved ───────────────────────────────────

    [Fact]
    public void Fingerprint_OriginalDeviceInfoPreserved()
    {
        var fp = MakeFingerprinter(RogAlly);
        var original = MakeHidDevice("0B05", "1ABE", productName: "ROG Ally Gamepad");

        var result = fp.Fingerprint([original]);

        Assert.Equal(original, result[0].Device);
        Assert.Equal("ROG Ally Gamepad", result[0].Device.ProductName);
        Assert.Equal(original.DevicePath, result[0].Device.DevicePath);
    }

    // ── FromFile loads and matches correctly ──────────────────────────────────

    [Fact]
    public void FromFile_LoadsKnownDevicesJson_MatchesCorrectly()
    {
        // Locate known-devices.json relative to the test assembly output directory.
        // The file is at repo root /devices/known-devices.json and copied to output
        // by the test project build (see .csproj Content item).
        string path = Path.Combine(AppContext.BaseDirectory, "known-devices.json");
        var fp = DeviceFingerprinter.FromFile(path);

        // ROG Ally is confirmed: true in the shipped file
        var rogAlly = MakeHidDevice("0B05", "1ABE");
        var result = fp.Fingerprint([rogAlly]);

        Assert.True(result[0].IsIntegratedGamepad);
        Assert.Equal("ASUS ROG Ally MCU Gamepad", result[0].KnownDeviceName);
        Assert.True(result[0].IsConfirmed);
    }

    [Fact]
    public void FromFile_MalformedJson_ThrowsInvalidOperationException()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json at all {{{{");
            Assert.Throws<System.Text.Json.JsonException>(
                () => DeviceFingerprinter.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
