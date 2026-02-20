using System.Text.Json;

namespace ControlShift.App;

/// <summary>
/// Persists user-assigned controller nicknames to %APPDATA%\ControlShift\nicknames.json.
/// Keyed by VID:PID (e.g. "045E:02FD" → "Casey's Pro Controller").
/// Follows the same best-effort I/O pattern as <see cref="SlotOrderStore"/>.
/// </summary>
public sealed class NicknameStore
{
    private static readonly string DirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ControlShift");

    private static readonly string FilePath = Path.Combine(DirPath, "nicknames.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Gets the user-assigned nickname for a controller, or null if none.</summary>
    public string? GetNickname(string vidPid)
    {
        if (string.IsNullOrEmpty(vidPid)) return null;
        var all = LoadAll();
        return all.TryGetValue(vidPid, out var name) ? name : null;
    }

    /// <summary>Sets (or overwrites) the nickname for a controller identified by VID:PID.</summary>
    public void SetNickname(string vidPid, string nickname)
    {
        if (string.IsNullOrEmpty(vidPid)) return;

        var all = LoadAllMutable();
        if (string.IsNullOrWhiteSpace(nickname))
        {
            all.Remove(vidPid);
        }
        else
        {
            all[vidPid] = nickname.Trim();
        }
        Save(all);
    }

    /// <summary>Removes the nickname for a controller.</summary>
    public void ClearNickname(string vidPid)
    {
        if (string.IsNullOrEmpty(vidPid)) return;
        var all = LoadAllMutable();
        if (all.Remove(vidPid))
            Save(all);
    }

    /// <summary>Loads all saved nicknames. Returns empty dictionary on missing/corrupt file.</summary>
    public IReadOnlyDictionary<string, string> LoadAll()
    {
        return LoadAllMutable();
    }

    private Dictionary<string, string> LoadAllMutable()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, string>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions)
                   ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }
    }

    private void Save(Dictionary<string, string> nicknames)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var json = JsonSerializer.Serialize(nicknames, WriteOptions);
            File.WriteAllText(FilePath, json);
        }
        catch { /* persistence is best-effort */ }
    }
}
