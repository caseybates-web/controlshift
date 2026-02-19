namespace ControlShift.Core.Models;

/// <summary>
/// Per-game profile stored at %APPDATA%\ControlShift\profiles\{name}.json.
/// Schema matches the PRD specification exactly.
/// </summary>
public sealed class Profile
{
    /// <summary>Display name (e.g. "Elden Ring - BT Controller as P1").</summary>
    public required string ProfileName { get; set; }

    /// <summary>Target EXE filename (e.g. "eldenring.exe"). Null for default profiles.</summary>
    public string? GameExe { get; set; }

    /// <summary>Optional full path for disambiguation when multiple EXEs share a name.</summary>
    public string? GamePath { get; set; }

    /// <summary>
    /// Ordered device identifiers for P1–P4. Each element is a device path or null (empty slot).
    /// Array always has exactly 4 elements. Setting a shorter array pads with nulls;
    /// setting a longer array truncates to 4.
    /// </summary>
    public string?[] SlotAssignments
    {
        get => _slotAssignments;
        set
        {
            if (value is null || value.Length == 4)
            {
                _slotAssignments = value ?? new string?[4];
                return;
            }

            var normalized = new string?[4];
            Array.Copy(value, normalized, Math.Min(value.Length, 4));
            _slotAssignments = normalized;
        }
    }
    private string?[] _slotAssignments = new string?[4];

    /// <summary>Whether to hide the integrated gamepad when this profile is active.</summary>
    public bool SuppressIntegrated { get; set; }

    /// <summary>If true, ControlShift will auto-revert before this game launches (anticheat safety).</summary>
    public bool AntiCheatGame { get; set; }

    /// <summary>ISO 8601 timestamp of profile creation.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
