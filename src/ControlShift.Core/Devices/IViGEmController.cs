namespace ControlShift.Core.Devices;

/// <summary>
/// Wraps a single ViGEm virtual Xbox 360 controller.
/// </summary>
public interface IViGEmController : IDisposable
{
    /// <summary>Creates and connects the virtual controller on the ViGEmBus.</summary>
    void Connect();

    /// <summary>Disconnects and disposes the virtual controller.</summary>
    void Disconnect();

    /// <summary>Submits a full gamepad state snapshot to the virtual controller.</summary>
    void SubmitReport(GamepadReport report);

    /// <summary>Whether the virtual controller is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>The XInput user index assigned by ViGEmBus after connection, or -1 if unknown.</summary>
    int UserIndex { get; }
}
