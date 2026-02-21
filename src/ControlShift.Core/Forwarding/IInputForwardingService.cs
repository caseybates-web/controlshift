using ControlShift.Core.Models;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Orchestrates controller reordering: creates ViGEm virtual controllers,
/// hides physical devices via HidHide, and forwards HID input.
/// </summary>
public interface IInputForwardingService : IDisposable
{
    /// <summary>Begin forwarding for all non-empty slot assignments.</summary>
    Task StartForwardingAsync(IReadOnlyList<SlotAssignment> assignments);

    /// <summary>Stop HID forwarding and clear HidHide rules.
    /// ViGEm virtual controllers remain connected for reuse.</summary>
    Task StopForwardingAsync();

    /// <summary>
    /// Hot-swaps the physical→virtual mapping on running forwarding pairs.
    /// Does NOT touch HidHide, ViGEm connections, or forwarding threads.
    /// </summary>
    Task UpdateMappingAsync(IReadOnlyList<SlotAssignment> assignments);

    /// <summary>Full revert: stop forwarding, disconnect ViGEm controllers, unhide all devices.
    /// Called by "Revert All" to restore physical controllers.</summary>
    Task RevertAllAsync();

    /// <summary>Whether any forwarding is currently active.</summary>
    bool IsForwarding { get; }

    /// <summary>The currently active slot assignments, or empty if not forwarding.</summary>
    IReadOnlyList<SlotAssignment> ActiveAssignments { get; }

    /// <summary>
    /// XInput slot indices occupied by ViGEm virtual controllers.
    /// Populated after <see cref="StartForwardingAsync"/> completes; empty when not forwarding.
    /// </summary>
    IReadOnlySet<int> VirtualSlotIndices { get; }

    /// <summary>Raised when a forwarding channel encounters an error.</summary>
    event EventHandler<ForwardingErrorEventArgs>? ForwardingError;
}
