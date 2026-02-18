using ControlShift.Core.Models;

namespace ControlShift.App.ViewModels;

/// <summary>
/// View model for a single controller slot (P1–P4).
/// </summary>
public class SlotViewModel
{
    public int SlotIndex { get; set; }
    public bool IsConnected { get; set; }
    public string? DisplayName { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public bool IsIntegratedGamepad { get; set; }
    public byte? BatteryLevel { get; set; }
    public string? BatteryType { get; set; }
    public string? DevicePath { get; set; }
    public string? Vid { get; set; }
    public string? Pid { get; set; }

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
}
