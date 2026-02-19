using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vortice.XInput;
using Windows.UI;
using ControlShift.App.ViewModels;

namespace ControlShift.App.Controls;

/// <summary>
/// A card representing one XInput player slot. Tap to identify the controller via rumble.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    private SlotViewModel? _slot;
    private bool _isRumbling;

    public SlotCard()
    {
        this.InitializeComponent();
    }

    /// <summary>Update the card to display the given slot view model.</summary>
    public void SetSlot(SlotViewModel slot)
    {
        _slot = slot;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (_slot is null) return;

        SlotBadge.Text = _slot.PlayerLabel;

        if (!_slot.IsConnected)
        {
            DeviceName.Text    = "Empty";
            DeviceName.Opacity = 0.4;
            ConnectionBadge.Visibility  = Visibility.Collapsed;
            IntegratedBadge.Visibility  = Visibility.Collapsed;
            BatterySection.Visibility   = Visibility.Collapsed;
            return;
        }

        DeviceName.Text    = _slot.DeviceName;
        DeviceName.Opacity = 1.0;

        ConnectionBadge.Visibility = string.IsNullOrEmpty(_slot.ConnectionLabel)
            ? Visibility.Collapsed : Visibility.Visible;
        ConnectionText.Text = _slot.ConnectionLabel;

        IntegratedBadge.Visibility = _slot.IsIntegrated
            ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(_slot.BatteryText))
        {
            BatterySection.Visibility = Visibility.Collapsed;
        }
        else
        {
            BatterySection.Visibility = Visibility.Visible;
            // Percentage values get a battery icon; otherwise show a plug for wired.
            BatteryIcon.Text = _slot.BatteryText.EndsWith('%') ? "\uEBA7" : "\uE83E";
            BatteryText.Text = _slot.BatteryText;
        }
    }

    /// <summary>
    /// Tap handler — sends a 500ms full-strength rumble to identify this controller.
    /// Only fires for connected XInput controllers. HID-only devices silently skip.
    /// </summary>
    private async void SlotCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_slot is null || !_slot.IsConnected || _isRumbling) return;

        _isRumbling = true;
        try
        {
            // Full-strength rumble: 65535 = max (ushort) for both motors.
            XInput.SetVibration((uint)_slot.SlotIndex, 65535, 65535);

            // Highlight card border green while rumbling.
            CardBorder.BorderBrush     = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
            CardBorder.BorderThickness = new Thickness(2);

            await Task.Delay(500);

            XInput.SetVibration((uint)_slot.SlotIndex, 0, 0);

            CardBorder.BorderBrush     = new SolidColorBrush(Color.FromArgb(255, 64, 64, 64));
            CardBorder.BorderThickness = new Thickness(1);
        }
        finally
        {
            _isRumbling = false;
        }
    }
}
