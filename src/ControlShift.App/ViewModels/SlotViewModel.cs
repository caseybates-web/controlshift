using System.ComponentModel;
using System.Runtime.CompilerServices;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;
using Microsoft.UI.Xaml;

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
    private string _batteryText     = string.Empty;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            Set(ref _isConnected, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryVisibility)));
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

    // ── Derived visibility ────────────────────────────────────────────────────

    public Visibility ConnectionVisibility =>
        IsConnected && !string.IsNullOrEmpty(ConnectionLabel)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility BatteryVisibility =>
        !string.IsNullOrEmpty(BatteryText) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IntegratedBadgeVisibility =>
        IsIntegrated ? Visibility.Visible : Visibility.Collapsed;

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotViewModel(int slotIndex)
    {
        SlotIndex = slotIndex;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all live data from a polled XInput slot and its fingerprinted HID match.
    /// Pass null for <paramref name="integratedMatch"/> when no integrated device is detected.
    /// </summary>
    public void UpdateFrom(XInputSlotInfo slot, FingerprintedDevice? integratedMatch)
    {
        IsConnected  = slot.IsConnected;
        IsIntegrated = integratedMatch?.IsIntegratedGamepad == true;

        if (!slot.IsConnected)
        {
            DeviceName      = "—";
            ConnectionLabel = string.Empty;
            BatteryText     = string.Empty;
            return;
        }

        DeviceName = integratedMatch?.KnownDeviceName
                     ?? (IsIntegrated ? "Built-in Controller" : "Controller");

        ConnectionLabel = slot.ConnectionType == XInputConnectionType.Wired ? "USB" : "Wireless";

        BatteryText = slot.BatteryPercent.HasValue
            ? $"{slot.BatteryPercent}%"
            : string.Empty;
    }
}
