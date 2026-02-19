using FluentAssertions;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Tests.Devices;

public class KnownDeviceDatabaseTests
{
    private static string TestDataPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "known-devices-test.json");

    [Fact]
    public void Load_ParsesJsonCorrectly()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        db.Count.Should().Be(3);
    }

    [Fact]
    public void Lookup_FindsKnownDevice()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        var device = db.Lookup("0B05", "1ABE");

        device.Should().NotBeNull();
        device!.Name.Should().Be("ASUS ROG Ally MCU Gamepad");
        device.Confirmed.Should().BeTrue();
    }

    [Fact]
    public void Lookup_ReturnsNullForUnknownDevice()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        db.Lookup("FFFF", "FFFF").Should().BeNull();
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        db.Lookup("0b05", "1abe").Should().NotBeNull();
        db.Lookup("0B05", "1ABE").Should().NotBeNull();
    }

    [Fact]
    public void IsKnownIntegratedGamepad_ReturnsTrueForKnown()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        db.IsKnownIntegratedGamepad("0B05", "1ABE").Should().BeTrue();
    }

    [Fact]
    public void IsKnownIntegratedGamepad_ReturnsFalseForUnknown()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);

        db.IsKnownIntegratedGamepad("DEAD", "BEEF").Should().BeFalse();
    }

    [Fact]
    public void Load_HandlesNonexistentFileGracefully()
    {
        var db = new KnownDeviceDatabase();

        var act = () => db.Load("/nonexistent/path.json");

        act.Should().NotThrow();
        db.Count.Should().Be(0);
    }

    [Fact]
    public void Load_ClearsPreviousDataOnReload()
    {
        var db = new KnownDeviceDatabase();
        db.Load(TestDataPath);
        db.Count.Should().Be(3);

        // Loading again should replace, not accumulate
        db.Load(TestDataPath);
        db.Count.Should().Be(3);
    }

    [Fact]
    public void Load_ThrowsOnMalformedJson()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "{ invalid json {{");
            var db = new KnownDeviceDatabase();

            var act = () => db.Load(tempFile);

            act.Should().Throw<System.Text.Json.JsonException>();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
