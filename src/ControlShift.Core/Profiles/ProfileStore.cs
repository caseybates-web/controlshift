using System.Text.Json;
using ControlShift.Core.Models;

namespace ControlShift.Core.Profiles;

/// <summary>
/// Persists per-game profiles as individual JSON files in
/// %APPDATA%\ControlShift\profiles\{sanitized-name}.json.
/// </summary>
/// <remarks>
/// DECISION: Follows the same best-effort I/O pattern as SlotOrderStore.
/// All I/O errors are caught silently — profiles are non-critical data.
/// </remarks>
public sealed class ProfileStore : IProfileStore
{
    private readonly string _dirPath;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Creates a ProfileStore using the default %APPDATA%\ControlShift\profiles\ directory.
    /// </summary>
    public ProfileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ControlShift", "profiles"))
    {
    }

    /// <summary>
    /// Creates a ProfileStore using a custom directory (for testing).
    /// </summary>
    internal ProfileStore(string dirPath)
    {
        _dirPath = dirPath;
    }

    public IReadOnlyList<Profile> LoadAll()
    {
        try
        {
            if (!Directory.Exists(_dirPath))
                return Array.Empty<Profile>();

            var profiles = new List<Profile>();
            foreach (var file in Directory.EnumerateFiles(_dirPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<Profile>(json, ReadOptions);
                    if (profile is not null)
                        profiles.Add(profile);
                }
                catch { /* skip corrupt files */ }
            }

            return profiles;
        }
        catch { return Array.Empty<Profile>(); }
    }

    public Profile? LoadByName(string profileName)
    {
        try
        {
            var filePath = GetFilePath(profileName);
            if (!File.Exists(filePath)) return null;

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<Profile>(json, ReadOptions);
        }
        catch { return null; }
    }

    public Profile? FindByGameExe(string gameExe)
    {
        var all = LoadAll();
        return all.FirstOrDefault(p =>
            string.Equals(p.GameExe, gameExe, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(Profile profile)
    {
        try
        {
            Directory.CreateDirectory(_dirPath);
            var filePath = GetFilePath(profile.ProfileName);
            var json = JsonSerializer.Serialize(profile, WriteOptions);
            File.WriteAllText(filePath, json);
        }
        catch { /* best-effort */ }
    }

    public void Delete(string profileName)
    {
        try
        {
            var filePath = GetFilePath(profileName);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch { /* best-effort */ }
    }

    private string GetFilePath(string profileName) =>
        Path.Combine(_dirPath, SanitizeFileName(profileName) + ".json");

    /// <summary>
    /// Converts a profile name to a filesystem-safe filename.
    /// Replaces invalid path characters with underscores and limits length to 100 chars.
    /// </summary>
    internal static string SanitizeFileName(string profileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            profileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        if (sanitized.Length > 100)
            sanitized = sanitized[..100];

        // Guard against empty or whitespace-only names.
        return string.IsNullOrWhiteSpace(sanitized) ? "_profile" : sanitized;
    }
}
