using ControlShift.Core.Profiles;
using FluentAssertions;

namespace ControlShift.Core.Tests.Profiles;

public sealed class AntiCheatDatabaseTests
{
    private static readonly AntiCheatEntry[] SampleEntries =
    [
        new() { Exe = "eldenring.exe", AntiCheat = "EAC", Name = "Elden Ring" },
        new() { Exe = "destiny2.exe",  AntiCheat = "BattlEye", Name = "Destiny 2" },
    ];

    [Fact]
    public void KnownGame_ReturnsTrue()
    {
        var db = new AntiCheatDatabase(SampleEntries);
        db.IsAntiCheatGame("eldenring.exe").Should().BeTrue();
    }

    [Fact]
    public void UnknownGame_ReturnsFalse()
    {
        var db = new AntiCheatDatabase(SampleEntries);
        db.IsAntiCheatGame("notepad.exe").Should().BeFalse();
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCase()
    {
        var db = new AntiCheatDatabase(SampleEntries);
        db.IsAntiCheatGame("ELDENRING.EXE").Should().BeTrue();
        db.IsAntiCheatGame("Destiny2.exe").Should().BeTrue();
    }

    [Fact]
    public void EmptyDatabase_AlwaysReturnsFalse()
    {
        var db = new AntiCheatDatabase(Array.Empty<AntiCheatEntry>());
        db.IsAntiCheatGame("eldenring.exe").Should().BeFalse();
    }

    [Fact]
    public void Entries_ReturnsAllLoadedEntries()
    {
        var db = new AntiCheatDatabase(SampleEntries);
        db.Entries.Should().HaveCount(2);
        db.Entries[0].Exe.Should().Be("eldenring.exe");
        db.Entries[1].AntiCheat.Should().Be("BattlEye");
    }

    [Fact]
    public void FromFile_LoadsBundledDatabase()
    {
        // This test loads the actual anticheat-games.json bundled with the project.
        string path = Path.Combine(AppContext.BaseDirectory, "anticheat-games.json");
        if (!File.Exists(path))
        {
            // Skip gracefully in environments where the file isn't copied.
            return;
        }

        var db = AntiCheatDatabase.FromFile(path);
        db.IsAntiCheatGame("eldenring.exe").Should().BeTrue();
        db.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public void FromFile_InvalidPath_Throws()
    {
        Action act = () => AntiCheatDatabase.FromFile("/nonexistent/path.json");
        act.Should().Throw<Exception>();
    }
}
