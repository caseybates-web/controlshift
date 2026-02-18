using System.Text.Json;
using FluentAssertions;
using ControlShift.Core.Models;

namespace ControlShift.Core.Tests.Models;

public class ProfileTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public void Profile_SerializesToExpectedFormat()
    {
        var profile = new Profile
        {
            ProfileName = "Test Profile",
            GameExe = "game.exe",
            SlotAssignments = new[] { "device-path-1", null, null, null },
            SuppressIntegrated = true,
            AntiCheatGame = false,
            CreatedAt = new DateTimeOffset(2026, 2, 18, 0, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);

        json.Should().Contain("\"profileName\": \"Test Profile\"");
        json.Should().Contain("\"gameExe\": \"game.exe\"");
        json.Should().Contain("\"suppressIntegrated\": true");
        json.Should().Contain("\"antiCheatGame\": false");
    }

    [Fact]
    public void Profile_DeserializesFromPrdExample()
    {
        var json = """
        {
          "profileName": "Elden Ring - BT Controller as P1",
          "gameExe": "eldenring.exe",
          "gamePath": null,
          "slotAssignments": ["bt-controller-device-path", null, null, null],
          "suppressIntegrated": true,
          "antiCheatGame": false,
          "createdAt": "2026-02-18T00:00:00Z"
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        profile.Should().NotBeNull();
        profile!.ProfileName.Should().Be("Elden Ring - BT Controller as P1");
        profile.GameExe.Should().Be("eldenring.exe");
        profile.GamePath.Should().BeNull();
        profile.SlotAssignments.Should().HaveCount(4);
        profile.SlotAssignments[0].Should().Be("bt-controller-device-path");
        profile.SlotAssignments[1].Should().BeNull();
        profile.SuppressIntegrated.Should().BeTrue();
        profile.AntiCheatGame.Should().BeFalse();
    }

    [Fact]
    public void Profile_RoundTrips()
    {
        var original = new Profile
        {
            ProfileName = "Round Trip Test",
            GameExe = "test.exe",
            GamePath = @"C:\Games\test.exe",
            SlotAssignments = new[] { "path-a", "path-b", null, null },
            SuppressIntegrated = false,
            AntiCheatGame = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var restored = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        restored.Should().NotBeNull();
        restored!.ProfileName.Should().Be(original.ProfileName);
        restored.GameExe.Should().Be(original.GameExe);
        restored.GamePath.Should().Be(original.GamePath);
        restored.SlotAssignments.Should().BeEquivalentTo(original.SlotAssignments);
        restored.SuppressIntegrated.Should().Be(original.SuppressIntegrated);
        restored.AntiCheatGame.Should().Be(original.AntiCheatGame);
    }

    [Fact]
    public void Profile_DefaultSlotAssignments_HasFourElements()
    {
        var profile = new Profile { ProfileName = "Default" };

        profile.SlotAssignments.Should().HaveCount(4);
        profile.SlotAssignments.Should().OnlyContain(s => s == null);
    }
}
