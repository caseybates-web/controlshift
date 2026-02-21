namespace ControlShift.Core.Models;

/// <summary>
/// Tracks the reorder state of a single slot during active forwarding (Phase 2).
/// </summary>
public sealed record SlotAssignment
{
    /// <summary>Target XInput slot (0–3).</summary>
    public required int TargetSlot { get; init; }

    /// <summary>HID device path of the source controller, or null if slot is empty.</summary>
    public string? SourceDevicePath { get; init; }

    /// <summary>Physical XInput slot index (0–3) to read input from. -1 if unknown.</summary>
    public int SourceSlotIndex { get; init; } = -1;

    /// <summary>Whether input forwarding is currently active for this slot.</summary>
    public bool IsForwarding { get; init; }
}
