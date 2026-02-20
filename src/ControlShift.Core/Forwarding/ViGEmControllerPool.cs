using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Creates and holds 4 virtual Xbox 360 controllers via ViGEmBus.
/// Controllers are connected on <see cref="Connect"/> and disconnected on <see cref="Dispose"/>.
/// AutoSubmitReport is disabled — callers batch Set* calls and invoke SubmitReport() explicitly.
/// </summary>
public sealed class ViGEmControllerPool : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller[] _controllers = new IXbox360Controller[4];
    private bool _connected;
    private bool _disposed;

    public ViGEmControllerPool()
    {
        _client = new ViGEmClient();
    }

    /// <summary>
    /// Creates and connects 4 virtual Xbox 360 controllers.
    /// Windows assigns XInput slots 0–3 in connection order.
    /// Call this AFTER hiding physical controllers via HidHide so virtual controllers get slots 0–3.
    /// </summary>
    public void Connect()
    {
        if (_connected) return;

        for (int i = 0; i < 4; i++)
        {
            var ctrl = _client.CreateXbox360Controller();
            ctrl.AutoSubmitReport = false;
            ctrl.Connect();
            _controllers[i] = ctrl;
        }

        _connected = true;
    }

    /// <summary>Gets the virtual controller at the given index (0–3).</summary>
    public IXbox360Controller this[int index] => _controllers[index];

    /// <summary>True if all 4 controllers are connected.</summary>
    public bool IsConnected => _connected;

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
