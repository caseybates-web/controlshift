using System.Text.Json.Serialization;

namespace ControlShift.Core.Devices;

/// <summary>
/// A single entry from known-devices.json.
/// VID and PID are stored as uppercase 4-digit hex strings (e.g. "0B05").
/// </summary>
public sealed record KnownDeviceEntry(
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("vid")]       string Vid,
    [property: JsonPropertyName("pid")]       string Pid,
    /// <summary>
    /// true = VID/PID has been validated on real hardware.
    /// false = sourced from community reports; treat as tentative.
    /// </summary>
    [property: JsonPropertyName("confirmed")] bool Confirmed
);

/// <summary>Root wrapper matching the top-level JSON object.</summary>
internal sealed class KnownDevicesRoot
{
    [JsonPropertyName("devices")]
    public List<KnownDeviceEntry> Devices { get; init; } = [];
}
