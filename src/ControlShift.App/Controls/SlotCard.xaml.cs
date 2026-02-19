using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vortice.XInput;
using Windows.UI;
using ControlShift.App.ViewModels;

namespace ControlShift.App.Controls;

/// <summary>
/// A card representing one XInput player slot.
/// Shows controller name, brand badge, VID:PID, connection type, and battery.
/// Tap to identify the controller via a 200ms rumble pulse.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    private SlotViewModel? _slot;
    private bool           _isRumbling;

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
            ConnectionBadge.Visibility = Visibility.Collapsed;
            IntegratedBadge.Visibility = Visibility.Collapsed;
            BrandBadge.Visibility      = Visibility.Collapsed;
            VidPidText.Visibility      = Visibility.Collapsed;
            BatterySection.Visibility  = Visibility.Collapsed;
            return;
        }

        DeviceName.Text    = _slot.DeviceName;
        DeviceName.Opacity = 1.0;

        // Connection type badge.
        ConnectionBadge.Visibility = string.IsNullOrEmpty(_slot.ConnectionLabel)
            ? Visibility.Collapsed : Visibility.Visible;
        ConnectionText.Text = _slot.ConnectionLabel;

        // INTEGRATED badge.
        IntegratedBadge.Visibility = _slot.IsIntegrated
            ? Visibility.Visible : Visibility.Collapsed;

        // Brand badge.
        BrandBadge.Visibility = string.IsNullOrEmpty(_slot.VendorBrand)
            ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Text = _slot.VendorBrand;

        // VID:PID.
        VidPidText.Visibility = string.IsNullOrEmpty(_slot.VidPid)
            ? Visibility.Collapsed : Visibility.Visible;
        VidPidText.Text = _slot.VidPid;

        // Battery.
        if (string.IsNullOrEmpty(_slot.BatteryText))
        {
            BatterySection.Visibility = Visibility.Collapsed;
        }
        else
        {
            BatterySection.Visibility = Visibility.Visible;
            BatteryIcon.Text = _slot.BatteryGlyph;
            BatteryText.Text = _slot.BatteryText;
        }
    }

    /// <summary>
    /// Tap handler — 200ms rumble at 25% strength to identify this controller.
    /// Only fires for connected XInput controllers; HID-only devices silently skip.
    /// </summary>
    private async void SlotCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_slot is null || !_slot.IsConnected || _isRumbling) return;

        _isRumbling = true;
        try
        {
            // 16383 ≈ 25% of ushort.MaxValue (65535) — enough to feel without startling.
            XInput.SetVibration((uint)_slot.SlotIndex, 16383, 16383);

            CardBorder.BorderBrush     = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
            CardBorder.BorderThickness = new Thickness(2);

            await Task.Delay(200);

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
