namespace ControlShift.Core.Models;

/// <summary>
/// A device entry from the known-devices.json database.
/// Used to identify integrated gamepads by VID/PID.
/// </summary>
public sealed record KnownDevice
{
    /// <summary>Human-readable device name (e.g. "ASUS ROG Ally MCU Gamepad").</summary>
    public required string Name { get; init; }

    /// <summary>Vendor ID in uppercase hex.</summary>
    public required string Vid { get; init; }

    /// <summary>Product ID in uppercase hex.</summary>
    public required string Pid { get; init; }

    /// <summary>Whether this VID/PID has been confirmed on real hardware.</summary>
    public bool Confirmed { get; init; }
}

/// <summary>
/// Root JSON model for the known-devices.json file.
/// </summary>
public sealed record KnownDeviceDatabaseModel
{
    public required List<KnownDevice> Devices { get; init; }
}
