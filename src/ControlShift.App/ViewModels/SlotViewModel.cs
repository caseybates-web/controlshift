using System.ComponentModel;
using System.Runtime.CompilerServices;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;
using Microsoft.UI.Xaml;

namespace ControlShift.App.ViewModels;

/// <summary>
/// View model for a single XInput player slot card (P1–P4).
/// Updated by <see cref="UpdateFrom(MatchedController)"/> after each enumeration poll.
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

    private bool    _isConnected;
    private bool    _isIntegrated;
    private string  _deviceName      = "—";
    private string  _connectionLabel = string.Empty;
    private string  _batteryText     = string.Empty;
    private string? _vendorBrand;
    private string  _vidPid          = string.Empty;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            Set(ref _isConnected, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VidPidVisibility)));
        }
    }

    public bool IsIntegrated
    {
        get => _isIntegrated;
        set
        {
            Set(ref _isIntegrated, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IntegratedBadgeVisibility)));
        }
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

    public string BatteryText
    {
        get => _batteryText;
        set
        {
            Set(ref _batteryText, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryVisibility)));
        }
    }

    public string? VendorBrand
    {
        get => _vendorBrand;
        set
        {
            Set(ref _vendorBrand, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrandBadgeVisibility)));
        }
    }

    public string VidPid
    {
        get => _vidPid;
        set
        {
            Set(ref _vidPid, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VidPidVisibility)));
        }
    }

    // ── Derived visibility ────────────────────────────────────────────────────

    public Visibility ConnectionVisibility =>
        IsConnected && !string.IsNullOrEmpty(ConnectionLabel)
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BatteryVisibility =>
        !string.IsNullOrEmpty(BatteryText) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IntegratedBadgeVisibility =>
        IsIntegrated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BrandBadgeVisibility =>
        !string.IsNullOrEmpty(VendorBrand) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility VidPidVisibility =>
        IsConnected && !string.IsNullOrEmpty(VidPid) ? Visibility.Visible : Visibility.Collapsed;

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotViewModel(int slotIndex)
    {
        SlotIndex = slotIndex;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all live data from the matched controller result for this slot.
    /// </summary>
    public void UpdateFrom(MatchedController mc)
    {
        IsConnected  = mc.IsConnected;
        IsIntegrated = mc.IsIntegratedGamepad;

        if (!mc.IsConnected)
        {
            DeviceName      = "—";
            ConnectionLabel = string.Empty;
            BatteryText     = string.Empty;
            VendorBrand     = null;
            VidPid          = string.Empty;
            return;
        }

        // Name priority: HID product string → known device name → VID:PID fallback.
        DeviceName = mc.Hid?.ProductName?.Trim() is { Length: > 0 } name
            ? name
            : mc.KnownDeviceName
              ?? (mc.Hid is not null ? $"{mc.Hid.Vid}:{mc.Hid.Pid}" : "Controller");

        // Connection label prefers the more specific HID detection over XInput's wired/wireless.
        ConnectionLabel = mc.HidConnectionType switch
        {
            HidConnectionType.Bluetooth => "Bluetooth",
            HidConnectionType.Usb       => "USB",
            _                           => mc.XInputConnectionType == XInputConnectionType.Wireless
                                               ? "Wireless" : "USB",
        };

        BatteryText = mc.BatteryPercent.HasValue ? $"{mc.BatteryPercent}%" : string.Empty;
        VendorBrand = mc.VendorBrand;
        VidPid      = mc.Hid is not null ? $"{mc.Hid.Vid}:{mc.Hid.Pid}" : string.Empty;
    }
}
