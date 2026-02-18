using System.Text.Json;
using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.Core.Devices;

/// <summary>
/// Loads and queries the known-devices.json database that maps VID/PID
/// pairs to known integrated gamepad models.
/// </summary>
public sealed class KnownDeviceDatabase
{
    private static readonly ILogger Logger = Log.ForContext<KnownDeviceDatabase>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Dictionary<string, KnownDevice> _devicesByVidPid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Load the known-devices database from a JSON file.
    /// </summary>
    public void Load(string jsonPath)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            var database = JsonSerializer.Deserialize<KnownDeviceDatabaseModel>(json, JsonOptions);

            if (database?.Devices is null)
            {
                Logger.Warning("Known devices file at {Path} contained no devices", jsonPath);
                return;
            }

            _devicesByVidPid.Clear();

            foreach (var device in database.Devices)
            {
                var key = $"{device.Vid}:{device.Pid}";
                _devicesByVidPid[key] = device;
            }

            Logger.Information("Loaded {Count} known devices from {Path}",
                _devicesByVidPid.Count, jsonPath);
        }
        catch (JsonException ex)
        {
            Logger.Error(ex, "Malformed JSON in known devices file {Path}", jsonPath);
            throw;
        }
        catch (FileNotFoundException)
        {
            Logger.Warning("Known devices file not found at {Path}", jsonPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load known devices from {Path}", jsonPath);
        }
    }

    /// <summary>
    /// Look up a device by VID and PID. Returns null if not found.
    /// </summary>
    public KnownDevice? Lookup(string vid, string pid)
    {
        var key = $"{vid}:{pid}";
        return _devicesByVidPid.TryGetValue(key, out var device) ? device : null;
    }

    /// <summary>
    /// Check whether a VID/PID pair matches a known integrated gamepad.
    /// </summary>
    public bool IsKnownIntegratedGamepad(string vid, string pid)
    {
        return Lookup(vid, pid) is not null;
    }

    /// <summary>
    /// Number of devices currently loaded.
    /// </summary>
    public int Count => _devicesByVidPid.Count;
}
