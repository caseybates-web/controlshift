using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace ControlShift.App.ViewModels;

/// <summary>
/// View model for a single XInput player slot card (P1–P4).
/// Implements INotifyPropertyChanged so x:Bind Mode=OneWay updates work in Step 6
/// when live enumeration data is wired in.
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

    // ── Slot identity (fixed at construction) ─────────────────────────────────

    public int    SlotIndex   { get; }
    public string PlayerLabel => $"P{SlotIndex + 1}";

    // ── Live data (updated in Step 6) ─────────────────────────────────────────

    private bool   _isConnected;
    private string _deviceName       = "—";
    private string _connectionLabel  = string.Empty;
    private string _batteryText      = string.Empty;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            Set(ref _isConnected, value);
            // Derived visibility properties change whenever IsConnected changes.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryVisibility)));
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

    // ── Derived visibility (consumed by x:Bind in XAML) ──────────────────────

    /// <summary>Collapses the connection-type label when the slot is empty.</summary>
    public Visibility ConnectionVisibility =>
        IsConnected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Collapses the battery readout when the slot is empty or wired.</summary>
    public Visibility BatteryVisibility =>
        !string.IsNullOrEmpty(BatteryText) ? Visibility.Visible : Visibility.Collapsed;

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotViewModel(int slotIndex)
    {
        SlotIndex = slotIndex;
    }
}
