using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlShift.Core.Profiles;

/// <summary>
/// Loads a bundled list of known anticheat-protected games and provides
/// fast lookup by executable name.
/// </summary>
/// <remarks>
/// DECISION: Uses a case-insensitive HashSet for O(1) lookup. The list is loaded
/// once at startup from anticheat-games.json. Games not in the list can still be
/// manually flagged via Profile.AntiCheatGame.
/// </remarks>
public sealed class AntiCheatDatabase
{
    private readonly HashSet<string> _exeNames;
    private readonly List<AntiCheatEntry> _entries;

    public AntiCheatDatabase(IReadOnlyList<AntiCheatEntry> entries)
    {
        _entries = new List<AntiCheatEntry>(entries);
        _exeNames = new HashSet<string>(
            entries.Select(e => e.Exe),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the given exe name matches a known anticheat-protected game.
    /// </summary>
    public bool IsAntiCheatGame(string exeName) =>
        _exeNames.Contains(exeName);

    /// <summary>
    /// All known anticheat entries.
    /// </summary>
    public IReadOnlyList<AntiCheatEntry> Entries => _entries;

    /// <summary>
    /// Loads the database from a JSON file at the specified path.
    /// </summary>
    public static AntiCheatDatabase FromFile(string path)
    {
        string json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize<AntiCheatRoot>(json, JsonOptions);
        return new AntiCheatDatabase(root?.Games ?? Array.Empty<AntiCheatEntry>());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class AntiCheatRoot
    {
        [JsonPropertyName("games")]
        public AntiCheatEntry[] Games { get; set; } = Array.Empty<AntiCheatEntry>();
    }
}

/// <summary>
/// A single entry in the anticheat games database.
/// </summary>
public sealed class AntiCheatEntry
{
    [JsonPropertyName("exe")]
    public string Exe { get; set; } = string.Empty;

    [JsonPropertyName("anticheat")]
    public string AntiCheat { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
