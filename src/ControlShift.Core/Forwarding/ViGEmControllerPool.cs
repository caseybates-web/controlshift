using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Vortice.XInput;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Creates and holds 4 virtual Xbox 360 controllers via ViGEmBus.
/// Controllers are connected on <see cref="Connect"/> and disconnected on <see cref="Dispose"/>.
/// AutoSubmitReport is disabled — callers batch Set* calls and invoke SubmitReport() explicitly.
/// </summary>
public sealed class ViGEmControllerPool : IDisposable
{
    /// <summary>Delay after connecting virtual controllers for Windows to propagate slot assignments.</summary>
    private const int SlotPropagationDelayMs = 300;

    private readonly ViGEmClient _client;
    private readonly IXbox360Controller[] _controllers = new IXbox360Controller[4];
    private readonly HashSet<int> _virtualSlotIndices = new();
    private bool _connected;
    private bool _disposed;

    public ViGEmControllerPool()
    {
        _client = new ViGEmClient();
    }

    /// <summary>
    /// Creates and connects 4 virtual Xbox 360 controllers.
    /// Diffs XInput slots before/after to detect which indices Windows assigned to virtual controllers.
    /// Call AFTER hiding physical controllers via HidHide.
    /// </summary>
    public void Connect()
    {
        if (_connected) return;

        // Snapshot which XInput slots are occupied BEFORE connecting virtual controllers
        var preSlots = new HashSet<int>();
        for (uint i = 0; i < 4; i++)
        {
            if (XInput.GetState(i, out _))
                preSlots.Add((int)i);
        }

        for (int i = 0; i < 4; i++)
        {
            var ctrl = _client.CreateXbox360Controller();
            ctrl.AutoSubmitReport = false;
            ctrl.Connect();
            _controllers[i] = ctrl;
        }

        _connected = true;

        Thread.Sleep(SlotPropagationDelayMs);

        // Diff: new slots that appeared after connecting are virtual
        _virtualSlotIndices.Clear();
        for (uint i = 0; i < 4; i++)
        {
            if (!preSlots.Contains((int)i) && XInput.GetState(i, out _))
                _virtualSlotIndices.Add((int)i);
        }

        Debug.WriteLine($"[ViGEmPool] Connected 4 virtual controllers. " +
                        $"Pre-existing slots: [{string.Join(", ", preSlots.OrderBy(x => x))}] " +
                        $"Virtual slots: [{string.Join(", ", _virtualSlotIndices.OrderBy(x => x))}]");
    }

    /// <summary>Gets the virtual controller at the given index (0–3).</summary>
    public IXbox360Controller this[int index] => _controllers[index];

    /// <summary>True if all 4 controllers are connected.</summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// XInput slot indices that were assigned to virtual controllers (detected via before/after diff).
    /// These slots should be hidden from the UI.
    /// </summary>
    public IReadOnlySet<int> VirtualSlotIndices => _virtualSlotIndices;

    /// <summary>
    /// Forwards XInput gamepad state to the specified virtual controller and submits the report.
    /// </summary>
    public static void Forward(IXbox360Controller target, Vortice.XInput.Gamepad gp)
    {
        target.SetButtonsFull((ushort)gp.Buttons);
        target.SetAxisValue(Xbox360Axis.LeftThumbX, gp.LeftThumbX);
        target.SetAxisValue(Xbox360Axis.LeftThumbY, gp.LeftThumbY);
        target.SetAxisValue(Xbox360Axis.RightThumbX, gp.RightThumbX);
        target.SetAxisValue(Xbox360Axis.RightThumbY, gp.RightThumbY);
        target.SetSliderValue(Xbox360Slider.LeftTrigger, gp.LeftTrigger);
        target.SetSliderValue(Xbox360Slider.RightTrigger, gp.RightTrigger);
        target.SubmitReport();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var ctrl in _controllers)
        {
            try { ctrl?.Disconnect(); } catch { /* best-effort */ }
        }

        _client.Dispose();
        _connected = false;
    }
}
