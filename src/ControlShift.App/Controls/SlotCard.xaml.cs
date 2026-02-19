using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ControlShift.App.ViewModels;
using ControlShift.Core.Models;

namespace ControlShift.App.Controls;

/// <summary>
/// Minimal Xbox-style controller slot card.
/// Shows device name, connection subtitle, and a single battery glyph.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    private static readonly SolidColorBrush MutedBadgeBg = new(ColorHelper.FromArgb(255, 51, 51, 51));   // #333
    private static readonly SolidColorBrush MutedBadgeText = new(ColorHelper.FromArgb(255, 85, 85, 85)); // #555
    private static readonly SolidColorBrush MutedNameBrush = new(ColorHelper.FromArgb(255, 85, 85, 85)); // #555

    // Segoe Fluent Icons battery glyphs (white outline, fill varies)
    private const string BatteryFull = "\xEBAA";  // Battery 10 (full)
    private const string BatteryHalf = "\xEBA6";  // Battery 5 (half)
    private const string BatteryLow = "\xEBA2";   // Battery 1 (low)
    private const string BatteryEmpty = "\xEBA0";  // Battery 0 (empty)
    private const string PlugIcon = "\xEBD4";      // PlugConnected

    public SlotCard()
    {
        this.InitializeComponent();
    }

    /// <summary>
    /// Update the card from a SlotViewModel.
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
        CardBorder.Opacity = 0.4;

        SlotBadgeBorder.Background = MutedBadgeBg;
        SlotBadge.Foreground = MutedBadgeText;

        DeviceName.Text = "No controller";
        DeviceName.Foreground = MutedNameBrush;
        DeviceName.FontWeight = Microsoft.UI.Text.FontWeights.Normal;

        DeviceSub.Visibility = Visibility.Collapsed;
        BatteryIcon.Text = "";
    }

    private void SetConnectedState(SlotViewModel slot)
    {
        CardBorder.Opacity = 1.0;

        SlotBadgeBorder.Background = (SolidColorBrush)Application.Current.Resources["CsAccentBrush"];
        SlotBadge.Foreground = new SolidColorBrush(Colors.White);

        DeviceName.Text = slot.DisplayName ?? "Unknown Controller";
        DeviceName.Foreground = (SolidColorBrush)Application.Current.Resources["CsTextPrimaryBrush"];
        DeviceName.FontWeight = Microsoft.UI.Text.FontWeights.Medium;

        // Subtitle: connection type
        var subText = slot.ConnectionType switch
        {
            ConnectionType.Usb => "USB",
            ConnectionType.Bluetooth => "Bluetooth",
            ConnectionType.Integrated => "Integrated",
            _ => null
        };

        if (subText != null)
        {
            DeviceSub.Text = subText;
            DeviceSub.Visibility = Visibility.Visible;
        }
        else
        {
            DeviceSub.Visibility = Visibility.Collapsed;
        }

        // Battery: single glyph, 3 fill levels + plug for wired
        if (slot.BatteryType == "Wired")
        {
            BatteryIcon.Text = PlugIcon;
        }
        else if (slot.BatteryLevel.HasValue)
        {
            BatteryIcon.Text = slot.BatteryLevel.Value switch
            {
                0 => BatteryLow,
                1 => BatteryHalf,
                2 => BatteryHalf,
                3 => BatteryFull,
                _ => BatteryEmpty
            };
        }
        else
        {
            BatteryIcon.Text = "";
        }
    }
}
