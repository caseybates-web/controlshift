using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ControlShift.App.ViewModels;
using ControlShift.Core.Models;

namespace ControlShift.App.Controls;

/// <summary>
/// A card control representing one of the 4 player slots (P1–P4).
/// Displays device name, connection type badge, battery level bar, and Xbox-style accent indicator.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    // Battery bar colors
    private static readonly SolidColorBrush BatteryRedBrush = new(ColorHelper.FromArgb(255, 231, 72, 86));     // #E74856
    private static readonly SolidColorBrush BatteryAmberBrush = new(ColorHelper.FromArgb(255, 245, 158, 11));   // #F59E0B
    private static readonly SolidColorBrush BatteryGreenBrush = new(ColorHelper.FromArgb(255, 16, 124, 16));    // #107C10
    private static readonly SolidColorBrush BatteryFullBrush = new(ColorHelper.FromArgb(255, 155, 240, 11));    // #9BF00B

    // Badge colors
    private static readonly SolidColorBrush BadgeMutedBrush = new(ColorHelper.FromArgb(255, 61, 61, 61));       // #3D3D3D
    private static readonly SolidColorBrush BadgeMutedTextBrush = new(ColorHelper.FromArgb(255, 120, 120, 120)); // #787878

    private const double BatteryBarMaxWidth = 32.0;

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
            SetEmptyState();
            return;
        }

        SetConnectedState(slot);
    }

    private void SetEmptyState()
    {
        // Accent bar hidden
        AccentBar.Visibility = Visibility.Collapsed;

        // Muted card border (reduced opacity)
        CardBorder.Opacity = 0.6;
        CardBorder.BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(80, 61, 61, 61)); // semi-transparent border

        // Muted badge
        SlotBadgeBorder.Background = BadgeMutedBrush;
        SlotBadge.Foreground = BadgeMutedTextBrush;

        // Empty state text
        DeviceName.Text = "No Controller";
        DeviceName.Opacity = 0.5;

        // Hide all detail elements
        ConnectionBadge.Visibility = Visibility.Collapsed;
        IntegratedBadge.Visibility = Visibility.Collapsed;
        BatteryIcon.Text = "";
        BatteryBarContainer.Visibility = Visibility.Collapsed;
        BatteryText.Text = "";
    }

    private void SetConnectedState(SlotViewModel slot)
    {
        // Show green accent bar
        AccentBar.Visibility = Visibility.Visible;

        // Full-opacity card with solid border
        CardBorder.Opacity = 1.0;
        CardBorder.BorderBrush = (SolidColorBrush)Application.Current.Resources["CsBorderBrush"];

        // Green badge
        SlotBadgeBorder.Background = (SolidColorBrush)Application.Current.Resources["CsAccentBrush"];
        SlotBadge.Foreground = new SolidColorBrush(Colors.White);

        // Device name
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
            BatteryIcon.Text = "\xEBA7"; // Battery icon
            BatteryText.Text = "";
            SetBatteryBar(slot.BatteryLevel.Value);
        }
        else if (slot.BatteryType == "Wired")
        {
            BatteryIcon.Text = "\xE83E"; // Plug icon
            BatteryBarContainer.Visibility = Visibility.Collapsed;
            BatteryText.Text = "Wired";
            BatteryText.Foreground = (SolidColorBrush)Application.Current.Resources["CsAccentBrush"];
        }
        else
        {
            BatteryIcon.Text = "";
            BatteryBarContainer.Visibility = Visibility.Collapsed;
            BatteryText.Text = "";
        }
    }

    private void SetBatteryBar(byte level)
    {
        BatteryBarContainer.Visibility = Visibility.Visible;

        // Set fill width and color based on battery level
        var (fillFraction, fillBrush) = level switch
        {
            0 => (0.10, BatteryRedBrush),
            1 => (0.33, BatteryAmberBrush),
            2 => (0.66, BatteryGreenBrush),
            3 => (1.00, BatteryFullBrush),
            _ => (0.0, BatteryRedBrush)
        };

        BatteryBarFill.Width = BatteryBarMaxWidth * fillFraction;
        BatteryBarFill.Background = fillBrush;
    }
}
