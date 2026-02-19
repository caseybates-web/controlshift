namespace ControlShift.Core.Devices;

public interface IVendorDatabase
{
    /// <summary>
    /// Returns the brand name for the given USB VID (e.g. "Xbox" for "045E"),
    /// or null if the VID is not in the database.
    /// Comparison is case-insensitive.
    /// </summary>
    string? GetBrand(string vid);
}
