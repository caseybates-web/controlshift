using System.Text.Json.Serialization;

namespace ControlShift.Core.Devices;

/// <summary>A single entry from known-vendors.json mapping a USB VID to a brand name.</summary>
public sealed record KnownVendorEntry(
    [property: JsonPropertyName("vid")]   string Vid,
    [property: JsonPropertyName("brand")] string Brand
);

/// <summary>Root wrapper matching the top-level JSON object in known-vendors.json.</summary>
internal sealed class KnownVendorsRoot
{
    [JsonPropertyName("vendors")]
    public List<KnownVendorEntry> Vendors { get; init; } = [];
}
