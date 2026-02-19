using System.Text.Json;
using ControlShift.Core.Enumeration;

namespace ControlShift.Core.Devices;

/// <summary>
/// Matches enumerated HID devices against the known-devices.json database
/// using VID+PID comparison. Devices that match are flagged as integrated gamepads.
/// </summary>
/// <remarks>
/// DECISION: Matching is case-insensitive. HidSharp formats VID/PID as uppercase,
/// and known-devices.json uses uppercase by convention, but defensive comparison
/// costs nothing and prevents silent misses if either side drifts.
///
/// DECISION: Use a static factory (FromFile) for production and an injectable
/// constructor (accepting a pre-loaded list) for unit tests. This avoids test
/// infrastructure that writes temp files while keeping the production path clean.
/// </remarks>
public sealed class DeviceFingerprinter : IDeviceFingerprinter
{
    private readonly IReadOnlyList<KnownDeviceEntry> _knownDevices;

    /// <summary>
    /// Accepts a pre-loaded list of known devices. Used directly in unit tests.
    /// </summary>
    public DeviceFingerprinter(IReadOnlyList<KnownDeviceEntry> knownDevices)
    {
        _knownDevices = knownDevices;
    }

    /// <summary>
    /// Loads known-devices.json from <paramref name="path"/> and returns a ready fingerprinter.
    /// Call this once at application startup.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the JSON is malformed or missing the "devices" array.</exception>
    public static DeviceFingerprinter FromFile(string path)
    {
        string json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize<KnownDevicesRoot>(json)
            ?? throw new InvalidOperationException($"Failed to deserialise known-devices.json at '{path}'.");
        return new DeviceFingerprinter(root.Devices);
    }

    public IReadOnlyList<FingerprintedDevice> Fingerprint(IReadOnlyList<HidDeviceInfo> devices)
    {
        var results = new List<FingerprintedDevice>(devices.Count);

        foreach (var device in devices)
        {
            var match = FindMatch(device.Vid, device.Pid);

            results.Add(new FingerprintedDevice(
                Device:              device,
                IsIntegratedGamepad: match is not null,
                KnownDeviceName:     match?.Name,
                IsConfirmed:         match?.Confirmed ?? false));
        }

        return results;
    }

    private KnownDeviceEntry? FindMatch(string vid, string pid)
    {
        foreach (var entry in _knownDevices)
        {
            if (string.Equals(entry.Vid, vid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Pid, pid, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }
}
