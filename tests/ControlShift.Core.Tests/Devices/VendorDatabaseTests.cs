using System.Text.Json;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Tests.Devices;

public class VendorDatabaseTests
{
    private static VendorDatabase MakeDb(params KnownVendorEntry[] entries) => new(entries);

    // ── GetBrand — known VIDs ─────────────────────────────────────────────────

    [Fact]
    public void GetBrand_KnownVid_ReturnsBrand()
    {
        var db = MakeDb(new KnownVendorEntry("045E", "Xbox"));
        Assert.Equal("Xbox", db.GetBrand("045E"));
    }

    [Fact]
    public void GetBrand_MultipleEntries_CorrectBrandReturned()
    {
        var db = MakeDb(new KnownVendorEntry("045E", "Xbox"), new KnownVendorEntry("054C", "PlayStation"), new KnownVendorEntry("057E", "Nintendo"));
        Assert.Equal("Xbox",        db.GetBrand("045E"));
        Assert.Equal("PlayStation", db.GetBrand("054C"));
        Assert.Equal("Nintendo",    db.GetBrand("057E"));
    }

    // ── GetBrand — unknown VIDs ───────────────────────────────────────────────

    [Fact]
    public void GetBrand_UnknownVid_ReturnsNull()
    {
        var db = MakeDb(new KnownVendorEntry("045E", "Xbox"));
        Assert.Null(db.GetBrand("FFFF"));
    }

    [Fact]
    public void GetBrand_EmptyDatabase_ReturnsNull()
    {
        var db = MakeDb();
        Assert.Null(db.GetBrand("045E"));
    }

    // ── Case-insensitive matching ─────────────────────────────────────────────

    [Theory]
    [InlineData("045e")]
    [InlineData("045E")]
    public void GetBrand_CaseInsensitive_Matches(string vid)
    {
        var db = MakeDb(new KnownVendorEntry("045E", "Xbox"));
        Assert.Equal("Xbox", db.GetBrand(vid));
    }

    [Theory]
    [InlineData("054c")]
    [InlineData("054C")]
    public void GetBrand_StoredLowercase_StillMatches(string vid)
    {
        // Vendor entries stored in lowercase still resolve.
        var db = MakeDb(new KnownVendorEntry("054c", "PlayStation"));
        Assert.Equal("PlayStation", db.GetBrand(vid));
    }

    // ── Duplicate VIDs — last-write-wins ─────────────────────────────────────

    [Fact]
    public void GetBrand_DuplicateVid_LastEntryWins()
    {
        var db = MakeDb(new KnownVendorEntry("045E", "Microsoft"), new KnownVendorEntry("045E", "Xbox"));
        Assert.Equal("Xbox", db.GetBrand("045E"));
    }

    // ── FromFile ──────────────────────────────────────────────────────────────

    [Fact]
    public void FromFile_LoadsKnownVendorsJson_XboxPresent()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "known-vendors.json");
        var db = VendorDatabase.FromFile(path);
        Assert.Equal("Xbox", db.GetBrand("045E"));
    }

    [Fact]
    public void FromFile_LoadsKnownVendorsJson_PlayStationPresent()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "known-vendors.json");
        var db = VendorDatabase.FromFile(path);
        Assert.Equal("PlayStation", db.GetBrand("054C"));
    }

    [Fact]
    public void FromFile_MalformedJson_ThrowsJsonException()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json {{{");
            Assert.Throws<JsonException>(() => VendorDatabase.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
