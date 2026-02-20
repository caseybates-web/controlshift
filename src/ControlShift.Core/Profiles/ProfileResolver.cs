using ControlShift.Core.Models;

namespace ControlShift.Core.Profiles;

/// <summary>
/// Resolves VID:PID-based profile slot assignments to current device paths
/// using the set of connected controllers.
/// </summary>
/// <remarks>
/// DECISION: Takes tuples instead of SlotViewModel to keep this in Core
/// with no App/UI dependency. The App layer converts its SlotCard/SlotViewModel
/// data to tuples before calling this.
/// </remarks>
public static class ProfileResolver
{
    /// <summary>
    /// Resolves a profile's VID:PID slot assignments to <see cref="SlotAssignment"/> objects
    /// with current device paths.
    /// </summary>
    /// <param name="profile">The profile whose SlotAssignments contain VID:PID strings.</param>
    /// <param name="connectedControllers">
    /// Currently connected controllers as (VidPid, DevicePath) tuples.
    /// </param>
    /// <returns>
    /// A list of SlotAssignment objects for each slot (0–3). Unmatched slots have
    /// a null SourceDevicePath.
    /// </returns>
    public static IReadOnlyList<SlotAssignment> Resolve(
        Profile profile,
        IReadOnlyList<(string VidPid, string? DevicePath)> connectedControllers)
    {
        // Track which connected controllers have already been claimed
        // to handle the case where multiple slots reference the same VID:PID.
        var claimed = new HashSet<int>();
        var assignments = new List<SlotAssignment>();

        for (int slot = 0; slot < profile.SlotAssignments.Length; slot++)
        {
            string? targetVidPid = profile.SlotAssignments[slot];
            string? resolvedPath = null;

            if (targetVidPid is not null)
            {
                for (int i = 0; i < connectedControllers.Count; i++)
                {
                    if (claimed.Contains(i))
                        continue;

                    if (string.Equals(connectedControllers[i].VidPid, targetVidPid,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedPath = connectedControllers[i].DevicePath;
                        claimed.Add(i);
                        break;
                    }
                }
            }

            assignments.Add(new SlotAssignment
            {
                TargetSlot = slot,
                SourceDevicePath = resolvedPath,
            });
        }

        return assignments;
    }
}
