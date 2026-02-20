using System.Text.Json;

namespace ControlShift.App;

/// <summary>
/// Persists the user's preferred controller visual order and ViGEm slotMap
/// to %APPDATA%\ControlShift\slot-order.json.
/// </summary>
public sealed class SlotOrderStore
{
    private static readonly string DirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ControlShift");

    private static readonly string FilePath = Path.Combine(DirPath, "slot-order.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Saves the visual order (VID:PID strings) and ViGEm slotMap.</summary>
    public void Save(string[] vidPidOrder, int[] slotMap)
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            var data = new SlotOrderData { Order = vidPidOrder, SlotMap = slotMap };
            var json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch { /* persistence is best-effort */ }
    }

    /// <summary>Loads the saved order. Returns null if no saved order exists or file is corrupt.</summary>
    public SlotOrderData? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<SlotOrderData>(json, ReadOptions);
            if (data?.Order is null || data.Order.Length == 0) return null;
            if (data.SlotMap is null || data.SlotMap.Length != 4) return null;
            return data;
        }
        catch { return null; }
    }

    /// <summary>Deletes the saved order file.</summary>
    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* best-effort */ }
    }
}

/// <summary>JSON-serializable data for slot-order.json.</summary>
public sealed class SlotOrderData
{
    public string[] Order { get; set; } = Array.Empty<string>();
    public int[] SlotMap { get; set; } = Array.Empty<int>();
}
