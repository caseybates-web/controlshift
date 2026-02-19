using System.ComponentModel;
using System.Runtime.CompilerServices;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.App.ViewModels;

/// <summary>
/// View model for a single XInput player slot card (P1–P4).
/// </summary>
public sealed class SlotViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // ── Slot identity ─────────────────────────────────────────────────────────

    public int    SlotIndex   { get; }
    public string PlayerLabel => $"P{SlotIndex + 1}";

    // ── Live data ─────────────────────────────────────────────────────────────

    private bool   _isConnected;
    private bool   _isIntegrated;
    private string _deviceName      = "—";
    private string _connectionLabel = string.Empty;
    private string _vendorBrand     = string.Empty;
    private string _vidPid          = string.Empty;
    private string _batteryText     = string.Empty;

    public bool IsConnected
    {
        get => _isConnected;
        set => Set(ref _isConnected, value);
    }

    public bool IsIntegrated
    {
        get => _isIntegrated;
        set => Set(ref _isIntegrated, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => Set(ref _deviceName, value);
    }

    public string ConnectionLabel
    {
        get => _connectionLabel;
        set => Set(ref _connectionLabel, value);
    }

    /// <summary>Brand from known-vendors.json (e.g. "Xbox", "PlayStation"). Empty string when unknown.</summary>
    public string VendorBrand
    {
        get => _vendorBrand;
        set => Set(ref _vendorBrand, value);
    }

    /// <summary>VID:PID in uppercase hex (e.g. "045E:02FD"). Empty string when no HID match.</summary>
    public string VidPid
    {
        get => _vidPid;
        set => Set(ref _vidPid, value);
    }

    public string BatteryText
    {
        get => _batteryText;
        set => Set(ref _batteryText, value);
    }

    /// <summary>Segoe MDL2 Assets glyph for the battery level. Empty when no battery info.</summary>
    public string BatteryGlyph => BatteryText switch
    {
        _ when string.IsNullOrEmpty(BatteryText)   => string.Empty,
        _ when BatteryText.EndsWith('%')            => "\uEBA7",  // battery icon
        _                                           => "\uE83E",  // plug icon (wired/unknown)
    };

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotViewModel(int slotIndex)
    {
        SlotIndex = slotIndex;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all live data from a <see cref="MatchedController"/> produced by
    /// <see cref="ControlShift.Core.Devices.IControllerMatcher"/>.
    /// </summary>
    public void UpdateFrom(MatchedController mc)
    {
        IsConnected  = mc.IsConnected;
        IsIntegrated = mc.IsIntegratedGamepad;

        if (!mc.IsConnected)
        {
            DeviceName      = "—";
            ConnectionLabel = string.Empty;
            VendorBrand     = string.Empty;
            VidPid          = string.Empty;
            BatteryText     = string.Empty;
            return;
        }

        string? productName = mc.Hid?.ProductName;
        if (string.IsNullOrWhiteSpace(productName)) productName = null;

        // Device name priority:
        //   1. HID product string (non-empty)
        //   2. Known-device name from devices database
        //   3. Vendor brand + "Controller" (e.g. "Xbox Controller") when brand is known
        //   4. "Unknown Controller" (last resort)
        DeviceName = productName
                     ?? mc.KnownDeviceName
                     ?? (!string.IsNullOrEmpty(mc.VendorBrand) ? $"{mc.VendorBrand} Controller" : null)
                     ?? "Unknown Controller";

        // Connection label.
        // HID path detection covers most cases. XInput override: if the HID path doesn't
        // contain a recognisable BT marker (detection returns Usb) but XInput's battery
        // query confirms the device is wireless (Alkaline/NiMH battery type), trust XInput —
        // the controller is definitely not USB-cabled. This covers Xbox Wireless Adapter and
        // any BT path format that doesn't match our heuristics.
        bool xinputWireless = mc.XInputConnectionType == XInputConnectionType.Wireless;
        ConnectionLabel = mc.HidConnectionType switch
        {
            HidConnectionType.Bluetooth                             => "BT",
            HidConnectionType.Usb when xinputWireless              => "BT",
            HidConnectionType.Usb                                   => "USB",
            _                           => mc.XInputConnectionType == XInputConnectionType.Wired ? "USB" : "Wireless",
        };

        VendorBrand = mc.VendorBrand ?? string.Empty;
        VidPid      = mc.Hid is not null ? $"{mc.Hid.Vid}:{mc.Hid.Pid}" : string.Empty;

        BatteryText = mc.BatteryPercent.HasValue ? $"{mc.BatteryPercent}%" : string.Empty;
    }
}
