using ControlShift.Core.Models;

namespace ControlShift.Core.Profiles;

/// <summary>
/// CRUD operations for per-game profiles stored as JSON files.
/// </summary>
public interface IProfileStore
{
    /// <summary>Loads all saved profiles from the profiles directory.</summary>
    IReadOnlyList<Profile> LoadAll();

    /// <summary>Loads a single profile by its display name. Returns null if not found.</summary>
    Profile? LoadByName(string profileName);

    /// <summary>
    /// Finds the first profile matching a game exe filename (case-insensitive).
    /// Returns null if no profile targets that exe.
    /// </summary>
    Profile? FindByGameExe(string gameExe);

    /// <summary>
    /// Saves a profile to disk. Overwrites any existing profile with the same name.
    /// </summary>
    void Save(Profile profile);

    /// <summary>Deletes a profile by display name. No-op if the profile does not exist.</summary>
    void Delete(string profileName);
}
