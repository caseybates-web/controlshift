namespace ControlShift.Core.Forwarding;

/// <summary>
/// Orchestrates controller reordering: manages the ViGEm pool, HidHide, and forwarding loop.
/// </summary>
public interface IReorderService : IDisposable
{
    /// <summary>
    /// Applies a new controller order. newOrder[physicalSlot] = virtualSlot.
    /// For example, to swap P1 and P2: { 1, 0, 2, 3 }.
    /// </summary>
    void ApplyOrder(int[] newOrder);

    /// <summary>Resets to identity mapping (0→0, 1→1, 2→2, 3→3).</summary>
    void RevertAll();

    /// <summary>True if the forwarding stack is running.</summary>
    bool IsActive { get; }
}
