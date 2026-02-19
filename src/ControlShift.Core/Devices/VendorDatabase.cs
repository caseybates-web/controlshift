using System.Text.Json;

namespace ControlShift.Core.Devices;

/// <summary>
/// Maps USB VIDs to brand names using known-vendors.json.
/// </summary>
/// <remarks>
/// DECISION: Same factory pattern as DeviceFingerprinter — FromFile() for production,
/// injectable constructor for unit tests. Matching is OrdinalIgnoreCase so "045E" and "045e"
/// both hit the same entry.
/// </remarks>
public sealed class VendorDatabase : IVendorDatabase
{
    private readonly IReadOnlyDictionary<string, string> _vidToBrand;

    /// <summary>Accepts a pre-loaded list of vendor entries. Used directly in unit tests.</summary>
    public VendorDatabase(IReadOnlyList<KnownVendorEntry> entries)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            dict[e.Vid] = e.Brand;
        _vidToBrand = dict;
    }

    /// <summary>
    /// Loads known-vendors.json from <paramref name="path"/> and returns a ready database.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the JSON root is null after deserialisation.</exception>
    public static VendorDatabase FromFile(string path)
    {
        string json = File.ReadAllText(path);
        var root = JsonSerializer.Deserialize<KnownVendorsRoot>(json)
            ?? throw new InvalidOperationException($"Failed to deserialise known-vendors.json at '{path}'.");
        return new VendorDatabase(root.Vendors);
    }

    /// <inheritdoc />
    public string? GetBrand(string vid)
    {
        _vidToBrand.TryGetValue(vid, out string? brand);
        return brand;
    }
}
