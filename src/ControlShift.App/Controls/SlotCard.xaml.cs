using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ControlShift.App.ViewModels;
using ControlShift.Core.Models;

namespace ControlShift.App.Controls;

/// <summary>
/// A card control representing one of the 4 player slots (P1–P4).
/// Displays device name, connection type badge, and battery level.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    public SlotCard()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Update the card display from a SlotViewModel.
    /// </summary>
    public void SetSlot(SlotViewModel slot)
    {
        SlotBadge.Text = $"P{slot.SlotIndex + 1}";

        if (!slot.IsConnected)
        {
            DeviceName.Text = "Empty";
            DeviceName.Opacity = 0.5;
            ConnectionBadge.Visibility = Visibility.Collapsed;
            IntegratedBadge.Visibility = Visibility.Collapsed;
            BatteryIcon.Text = "";
            BatteryText.Text = "";
            return;
        }

        DeviceName.Text = slot.DisplayName ?? "Unknown Controller";
        DeviceName.Opacity = 1.0;

        // Connection type badge
        if (slot.ConnectionType != ConnectionType.Unknown)
        {
            ConnectionBadge.Visibility = Visibility.Visible;
            ConnectionText.Text = slot.ConnectionType switch
            {
                ConnectionType.Usb => "USB",
                ConnectionType.Bluetooth => "BT",
                ConnectionType.Integrated => "INT",
                _ => ""
            };
        }
        else
        {
            ConnectionBadge.Visibility = Visibility.Collapsed;
        }

        // Integrated badge
        IntegratedBadge.Visibility = slot.IsIntegratedGamepad
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Battery display
        if (slot.BatteryLevel.HasValue && slot.BatteryType != "Wired")
        {
            BatteryIcon.Text = "\xEBA7"; // Battery icon from Segoe MDL2
            BatteryText.Text = slot.BatteryLevel.Value switch
            {
                0 => "Empty",
                1 => "Low",
                2 => "Med",
                3 => "Full",
                _ => ""
            };
        }
        else if (slot.BatteryType == "Wired")
        {
            BatteryIcon.Text = "\xE83E"; // Plug icon
            BatteryText.Text = "Wired";
        }
        else
        {
            BatteryIcon.Text = "";
            BatteryText.Text = "";
        }
    }
}
