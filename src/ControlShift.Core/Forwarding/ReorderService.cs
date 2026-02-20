using System.Diagnostics;
using HidSharp;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Concrete <see cref="IReorderService"/> that orchestrates the full forwarding stack:
/// HidHide → ViGEm pool → InputForwardingService.
/// </summary>
public sealed class ReorderService : IReorderService
{
    private static readonly int[] IdentityMap = { 0, 1, 2, 3 };

    private ViGEmControllerPool? _pool;
    private HidHideService? _hidHide;
    private InputForwardingService? _forwarding;
    private bool _disposed;

    public bool IsActive => _forwarding?.IsRunning ?? false;

    /// <inheritdoc />
    public IReadOnlySet<int> VirtualSlotIndices =>
        _pool?.VirtualSlotIndices ?? (IReadOnlySet<int>)new HashSet<int>();

    /// <summary>
    /// Initializes the forwarding stack: hides physical controllers, creates virtual ones,
    /// and starts forwarding with the given slot map.
    /// </summary>
    public void Initialize(string exePath)
    {
        if (_forwarding?.IsRunning == true) return;

        // 1. Set up HidHide — hide physical XInput controllers
        _hidHide = new HidHideService();
        if (_hidHide.IsAvailable)
        {
            _hidHide.ClearAll();
            _hidHide.AddToAllowlist(exePath);

            // Find XInput HID interfaces and hide them
            var xinputHidDevices = DeviceList.Local.GetHidDevices()
                .Where(d => d.DevicePath.IndexOf("ig_", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var hid in xinputHidDevices)
            {
                string instanceId = HidHideService.HidPathToInstanceId(hid.DevicePath);
                _hidHide.HideDevice(instanceId);
            }

            _hidHide.Enable();
        }

        // 2. Create virtual controllers — they get slots 0–3 now that physical are hidden
        _pool = new ViGEmControllerPool();
        _pool.Connect();

        // 3. Start forwarding with identity map
        _forwarding = new InputForwardingService(_pool);
        _forwarding.SetSlotMap(IdentityMap);
        _forwarding.Start();
    }

    public void ApplyOrder(int[] newOrder)
    {
        if (newOrder.Length != 4) throw new ArgumentException("Order must have exactly 4 entries.");
        _forwarding?.SetSlotMap(newOrder);
    }

    public void RevertAll()
    {
        _forwarding?.SetSlotMap(IdentityMap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _forwarding?.Dispose();
        _pool?.Dispose();
        _hidHide?.Dispose();
    }
}
