using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ControlShift.Core.Devices;

/// <summary>
/// Wraps a ViGEm virtual Xbox 360 controller. One instance per forwarding slot.
/// </summary>
/// <remarks>
/// DECISION: The caller provides a shared <see cref="ViGEmClient"/> singleton.
/// Creating multiple ViGEmClient instances is wasteful — the bus connection is global.
/// </remarks>
public sealed class ViGEmController : IViGEmController
{
    private readonly IXbox360Controller _controller;
    private bool _connected;
    private bool _disposed;

    public ViGEmController(ViGEmClient client)
    {
        _controller = client.CreateXbox360Controller();
    }

    public bool IsConnected => _connected;

    public int UserIndex => _connected ? _controller.UserIndex : -1;

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _controller.Connect();
        _connected = true;
    }

    public void Disconnect()
    {
        if (!_connected) return;
        _controller.Disconnect();
        _connected = false;
    }

    public void SubmitReport(GamepadReport r)
    {
        if (!_connected) return;

        // Buttons — standard Xbox 360 bitmask mapping.
        _controller.SetButtonState(Xbox360Button.Up,             (r.Buttons & 0x0001) != 0);
        _controller.SetButtonState(Xbox360Button.Down,           (r.Buttons & 0x0002) != 0);
        _controller.SetButtonState(Xbox360Button.Left,           (r.Buttons & 0x0004) != 0);
        _controller.SetButtonState(Xbox360Button.Right,          (r.Buttons & 0x0008) != 0);
        _controller.SetButtonState(Xbox360Button.Start,          (r.Buttons & 0x0010) != 0);
        _controller.SetButtonState(Xbox360Button.Back,           (r.Buttons & 0x0020) != 0);
        _controller.SetButtonState(Xbox360Button.LeftThumb,      (r.Buttons & 0x0040) != 0);
        _controller.SetButtonState(Xbox360Button.RightThumb,     (r.Buttons & 0x0080) != 0);
        _controller.SetButtonState(Xbox360Button.LeftShoulder,   (r.Buttons & 0x0100) != 0);
        _controller.SetButtonState(Xbox360Button.RightShoulder,  (r.Buttons & 0x0200) != 0);
        _controller.SetButtonState(Xbox360Button.Guide,          (r.Buttons & 0x0400) != 0);
        _controller.SetButtonState(Xbox360Button.A,              (r.Buttons & 0x1000) != 0);
        _controller.SetButtonState(Xbox360Button.B,              (r.Buttons & 0x2000) != 0);
        _controller.SetButtonState(Xbox360Button.X,              (r.Buttons & 0x4000) != 0);
        _controller.SetButtonState(Xbox360Button.Y,              (r.Buttons & 0x8000) != 0);

        // Triggers
        _controller.SetSliderValue(Xbox360Slider.LeftTrigger,  r.LeftTrigger);
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, r.RightTrigger);

        // Axes
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX,  r.ThumbLX);
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY,  r.ThumbLY);
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, r.ThumbRX);
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, r.ThumbRY);

        _controller.SubmitReport();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
