using System.ComponentModel;
using System.Runtime.CompilerServices;
using ControlShift.Core.Models;

namespace ControlShift.App.ViewModels;

/// <summary>
/// View model for a single controller slot (P1–P4).
/// Implements INotifyPropertyChanged for data-binding support.
/// </summary>
public class SlotViewModel : INotifyPropertyChanged
{
    private int _slotIndex;
    private bool _isConnected;
    private string? _displayName;
    private ConnectionType _connectionType;
    private bool _isIntegratedGamepad;
    private byte? _batteryLevel;
    private string? _batteryType;
    private string? _devicePath;
    private string? _vid;
    private string? _pid;

    public int SlotIndex
    {
        get => _slotIndex;
        set => SetField(ref _slotIndex, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => SetField(ref _isConnected, value);
    }

    public string? DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public ConnectionType ConnectionType
    {
        get => _connectionType;
        set => SetField(ref _connectionType, value);
    }

    public bool IsIntegratedGamepad
    {
        get => _isIntegratedGamepad;
        set => SetField(ref _isIntegratedGamepad, value);
    }

    public byte? BatteryLevel
    {
        get => _batteryLevel;
        set => SetField(ref _batteryLevel, value);
    }

    public string? BatteryType
    {
        get => _batteryType;
        set => SetField(ref _batteryType, value);
    }

    public string? DevicePath
    {
        get => _devicePath;
        set => SetField(ref _devicePath, value);
    }

    public string? Vid
    {
        get => _vid;
        set => SetField(ref _vid, value);
    }

    public string? Pid
    {
        get => _pid;
        set => SetField(ref _pid, value);
    }

    /// <summary>
    /// Update this view model from a ControllerInfo model.
    /// </summary>
    public void UpdateFrom(ControllerInfo info)
    {
        SlotIndex = info.SlotIndex;
        IsConnected = info.IsConnected;
        DisplayName = info.DisplayName;
        ConnectionType = info.ConnectionType;
        IsIntegratedGamepad = info.IsIntegratedGamepad;
        BatteryLevel = info.BatteryLevel;
        BatteryType = info.BatteryType;
        DevicePath = info.DevicePath;
        Vid = info.Vid;
        Pid = info.Pid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
