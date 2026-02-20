using FluentAssertions;
using ControlShift.Core.Models;
using ControlShift.Core.Profiles;

namespace ControlShift.Core.Tests.Profiles;

public class ProfileStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ControlShift-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _store = new ProfileStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static Profile CreateProfile(string name = "Test Profile", string? gameExe = "game.exe") =>
        new()
        {
            ProfileName = name,
            GameExe = gameExe,
            SlotAssignments = new[] { "045E:02FD", null, null, null },
        };

    [Fact]
    public void Save_CreatesJsonFile_InProfilesDirectory()
    {
        var profile = CreateProfile();
        _store.Save(profile);

        var files = Directory.GetFiles(_tempDir, "*.json");
        files.Should().HaveCount(1);
    }

    [Fact]
    public void LoadAll_ReturnsAllSavedProfiles()
    {
        _store.Save(CreateProfile("Profile A"));
        _store.Save(CreateProfile("Profile B"));

        var all = _store.LoadAll();
        all.Should().HaveCount(2);
        all.Select(p => p.ProfileName).Should().Contain("Profile A").And.Contain("Profile B");
    }

    [Fact]
    public void LoadByName_ReturnsCorrectProfile()
    {
        _store.Save(CreateProfile("My Profile", "test.exe"));

        var loaded = _store.LoadByName("My Profile");
        loaded.Should().NotBeNull();
        loaded!.ProfileName.Should().Be("My Profile");
        loaded.GameExe.Should().Be("test.exe");
    }

    [Fact]
    public void LoadByName_NotFound_ReturnsNull()
    {
        var loaded = _store.LoadByName("Nonexistent");
        loaded.Should().BeNull();
    }

    [Fact]
    public void FindByGameExe_CaseInsensitive_ReturnsMatch()
    {
        _store.Save(CreateProfile("Test", "MyGame.exe"));

        var found = _store.FindByGameExe("MYGAME.EXE");
        found.Should().NotBeNull();
        found!.ProfileName.Should().Be("Test");
    }

    [Fact]
    public void FindByGameExe_NoMatch_ReturnsNull()
    {
        _store.Save(CreateProfile("Test", "other.exe"));

        var found = _store.FindByGameExe("missing.exe");
        found.Should().BeNull();
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        _store.Save(CreateProfile("To Delete"));
        _store.LoadByName("To Delete").Should().NotBeNull();

        _store.Delete("To Delete");
        _store.LoadByName("To Delete").Should().BeNull();
    }

    [Fact]
    public void Save_RoundTrips_AllProperties()
    {
        var original = new Profile
        {
            ProfileName = "Full Profile",
            GameExe = "game.exe",
            GamePath = @"C:\Games\game.exe",
            SlotAssignments = new[] { "045E:02FD", "054C:0CE6", null, "057E:2009" },
            SuppressIntegrated = true,
            AntiCheatGame = true,
            CreatedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        };

        _store.Save(original);
        var loaded = _store.LoadByName("Full Profile");

        loaded.Should().NotBeNull();
        loaded!.ProfileName.Should().Be(original.ProfileName);
        loaded.GameExe.Should().Be(original.GameExe);
        loaded.GamePath.Should().Be(original.GamePath);
        loaded.SlotAssignments.Should().BeEquivalentTo(original.SlotAssignments);
        loaded.SuppressIntegrated.Should().Be(original.SuppressIntegrated);
        loaded.AntiCheatGame.Should().Be(original.AntiCheatGame);
        loaded.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Fact]
    public void Save_OverwritesExistingProfile_SameName()
    {
        _store.Save(CreateProfile("Same Name", "old.exe"));
        _store.Save(CreateProfile("Same Name", "new.exe"));

        var loaded = _store.LoadByName("Same Name");
        loaded.Should().NotBeNull();
        loaded!.GameExe.Should().Be("new.exe");

        _store.LoadAll().Should().HaveCount(1);
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        var sanitized = ProfileStore.SanitizeFileName("Game: Special <Edition>");
        sanitized.Should().NotContain(":").And.NotContain("<").And.NotContain(">");
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongNames()
    {
        var longName = new string('A', 200);
        var sanitized = ProfileStore.SanitizeFileName(longName);
        sanitized.Length.Should().BeLessOrEqualTo(100);
    }

    [Fact]
    public void SanitizeFileName_EmptyName_ReturnsFallback()
    {
        var sanitized = ProfileStore.SanitizeFileName("");
        sanitized.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadAll_EmptyDirectory_ReturnsEmptyList()
    {
        var all = _store.LoadAll();
        all.Should().BeEmpty();
    }

    [Fact]
    public void Delete_Nonexistent_DoesNotThrow()
    {
        _store.Invoking(s => s.Delete("Nonexistent")).Should().NotThrow();
    }
}
