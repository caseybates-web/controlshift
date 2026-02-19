using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vortice.XInput;
using Windows.UI;
using ControlShift.App.ViewModels;

namespace ControlShift.App.Controls;

/// <summary>Visual focus/selection states for a controller card.</summary>
public enum CardState
{
    /// <summary>No focus — default appearance.</summary>
    Normal,
    /// <summary>Keyboard/gamepad focus — 1px Xbox-green border.</summary>
    Focused,
    /// <summary>Selected for reordering — 3px Xbox-green border + brighter background.</summary>
    Selected,
    /// <summary>Another card is being reordered — dimmed to 40% opacity.</summary>
    Dimmed,
}

/// <summary>
/// A card representing one XInput player slot.
/// Shows controller name, brand, VID:PID, connection type, and battery.
/// Tap to identify the controller via a short rumble pulse.
/// Tab/XYFocus navigable; call <see cref="SetCardState"/> from MainWindow to update visuals.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    // ── Card state colors ─────────────────────────────────────────────────────

    private static readonly Color ColorBorderNormal   = Color.FromArgb(255,  64,  64,  64); // #404040
    private static readonly Color ColorBorderFocused  = Color.FromArgb(255,  16, 124,  16); // #107C10
    private static readonly Color ColorBgNormal       = Color.FromArgb(255,  45,  45,  45); // #2D2D2D
    private static readonly Color ColorBgSelected     = Color.FromArgb(255,  55,  55,  55); // #373737

    // ── Fields ────────────────────────────────────────────────────────────────

    private SlotViewModel? _slot;
    private bool           _isRumbling;
    private CardState      _currentState = CardState.Normal;

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotCard()
    {
        this.InitializeComponent();
    }

    // ── Slot data ─────────────────────────────────────────────────────────────

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
        BrandText.Text = _slot.VendorBrand ?? string.Empty;

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

    // ── Visual state ──────────────────────────────────────────────────────────

    /// <summary>
    /// Update the card's visual state (focus/selection/dimmed).
    /// Called by MainWindow as navigation state changes.
    /// </summary>
    public void SetCardState(CardState state)
    {
        _currentState = state;
        ApplyCardState();
    }

    private void ApplyCardState()
    {
        switch (_currentState)
        {
            case CardState.Normal:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(ColorBgNormal);
                Opacity = 1.0;
                break;

            case CardState.Focused:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderFocused);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(ColorBgNormal);
                Opacity = 1.0;
                break;

            case CardState.Selected:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderFocused);
                CardBorder.BorderThickness = new Thickness(3);
                CardBorder.Background      = new SolidColorBrush(ColorBgSelected);
                Opacity = 1.0;
                break;

            case CardState.Dimmed:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(ColorBgNormal);
                Opacity = 0.4;
                break;
        }
    }

    // ── Rumble on tap ─────────────────────────────────────────────────────────

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

            // Temporarily flash the border while rumbling.
            CardBorder.BorderBrush     = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
            CardBorder.BorderThickness = new Thickness(2);

            await Task.Delay(200);

            XInput.SetVibration((uint)_slot.SlotIndex, 0, 0);

            // Restore to whatever state the card was in before the tap.
            ApplyCardState();
        }
        finally
        {
            _isRumbling = false;
        }
    }
}
