using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    /// <summary>Keyboard/gamepad focus — 1px green border + scale 1.03 swell.</summary>
    Focused,
    /// <summary>Selected for reordering — 3px green border + brighter bg + scale 1.03.</summary>
    Selected,
    /// <summary>Another card is being reordered — dimmed to 50% opacity.</summary>
    Dimmed,
}

/// <summary>
/// A card representing one XInput player slot.
/// Shows controller name, connection type, brand badge, VID:PID, and battery.
/// Tap to identify via a 200ms rumble pulse.
/// Gains a smooth scale-swell animation on focus via the Composition API.
/// </summary>
public sealed partial class SlotCard : UserControl
{
    // ── Card state colors ─────────────────────────────────────────────────────

    private static readonly Color ColorBorderNormal   = Color.FromArgb(255,  48,  48,  48); // #303030
    private static readonly Color ColorBorderFocused  = Color.FromArgb(255,  16, 124,  16); // #107C10
    private static readonly Color ColorBgEmpty        = Color.FromArgb(255,  34,  34,  34); // #222222
    private static readonly Color ColorBgNormal       = Color.FromArgb(255,  45,  45,  45); // #2D2D2D
    private static readonly Color ColorBgFocused      = Color.FromArgb(255,  56,  56,  56); // #383838
    private static readonly Color ColorBgSelected     = Color.FromArgb(255,  64,  64,  64); // #404040
    private static readonly Color ColorBadgeEmpty     = Color.FromArgb(255,  85,  85,  85); // #555555
    private static readonly Color ColorBadgeConnected = Color.FromArgb(255,  16, 124,  16); // #107C10

    // ── Fields ────────────────────────────────────────────────────────────────

    private SlotViewModel? _slot;
    private bool           _isRumbling;
    private CardState      _currentState = CardState.Normal;

    // ── Construction ──────────────────────────────────────────────────────────

    public SlotCard()
    {
        this.InitializeComponent();
        // Defer IsTabStop / UseSystemFocusVisuals to Loaded — setting them during
        // construction triggers STATUS_ASSERTION_FAILURE (0xc000027b) in WinUI 3.
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        this.Loaded -= OnLoaded;
        this.IsTabStop             = true;
        this.UseSystemFocusVisuals = false;
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
            PlayerBadgeBorder.Background = new SolidColorBrush(ColorBadgeEmpty);
            ConnectedContent.Visibility  = Visibility.Collapsed;
            EmptyDash.Visibility         = Visibility.Visible;
            BatterySection.Visibility    = Visibility.Collapsed;
            return;
        }

        PlayerBadgeBorder.Background = new SolidColorBrush(ColorBadgeConnected);
        ConnectedContent.Visibility  = Visibility.Visible;
        EmptyDash.Visibility         = Visibility.Collapsed;

        DeviceName.Text = _slot.DeviceName;

        ConnectionText.Text       = _slot.ConnectionLabel;
        ConnectionText.Visibility = string.IsNullOrEmpty(_slot.ConnectionLabel)
            ? Visibility.Collapsed : Visibility.Visible;

        IntegratedBadge.Visibility = _slot.IsIntegrated
            ? Visibility.Visible : Visibility.Collapsed;

        BrandBadge.Visibility = string.IsNullOrEmpty(_slot.VendorBrand)
            ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Text = _slot.VendorBrand;

        VidPidText.Visibility = string.IsNullOrEmpty(_slot.VidPid)
            ? Visibility.Collapsed : Visibility.Visible;
        VidPidText.Text = _slot.VidPid;

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
        bool connected = _slot?.IsConnected ?? false;

        switch (_currentState)
        {
            case CardState.Normal:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(connected ? ColorBgNormal : ColorBgEmpty);
                Opacity = 1.0;
                AnimateScale(1.0);
                break;

            case CardState.Focused:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderFocused);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(ColorBgFocused);
                Opacity = 1.0;
                AnimateScale(1.03);
                break;

            case CardState.Selected:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderFocused);
                CardBorder.BorderThickness = new Thickness(3);
                CardBorder.Background      = new SolidColorBrush(ColorBgSelected);
                Opacity = 1.0;
                AnimateScale(1.03);
                break;

            case CardState.Dimmed:
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                CardBorder.Background      = new SolidColorBrush(connected ? ColorBgNormal : ColorBgEmpty);
                Opacity = 0.5;
                AnimateScale(1.0);
                break;
        }
    }

    /// <summary>
    /// Smooth scale-swell animation using the Composition API (avoids Storyboard overhead).
    /// Animates the card's visual scale to <paramref name="toScale"/> over 100ms ease-in-out.
    /// Safe to call before layout — returns immediately if the element is not yet loaded.
    /// </summary>
    private void AnimateScale(double toScale)
    {
        // Guard: compositor is not available until the element is in the visual tree.
        if (!IsLoaded || CardBorder.ActualWidth == 0) return;

        var visual = ElementCompositionPreview.GetElementVisual(CardBorder);

        // Scale from center of the card.
        visual.CenterPoint = new Vector3(
            (float)(CardBorder.ActualWidth  / 2),
            (float)(CardBorder.ActualHeight / 2),
            0f);

        var compositor = visual.Compositor;
        var easing     = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.45f, 0f),   // ease-in control point
            new Vector2(0.55f, 1f));  // ease-out control point

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(100);
        anim.InsertKeyFrame(1.0f, (float)toScale, easing);

        visual.StartAnimation("Scale.X", anim);
        visual.StartAnimation("Scale.Y", anim);
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

            // Flash the border while rumbling; restore state when done.
            CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderFocused);
            CardBorder.BorderThickness = new Thickness(2);

            await Task.Delay(200);

            XInput.SetVibration((uint)_slot.SlotIndex, 0, 0);

            // Restore to whatever nav state the card had before the tap.
            ApplyCardState();
        }
        finally
        {
            _isRumbling = false;
        }
    }
}
