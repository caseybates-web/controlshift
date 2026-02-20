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
    // ── Constants ───────────────────────────────────────────────────────────

    private const ushort TapRumbleIntensity = 16383; // 25% of ushort.MaxValue — noticeable but not startling
    private const int    TapRumbleDurationMs = 200;

    private static readonly Color ColorBadgeEmpty     = Color.FromArgb(255,  85,  85,  85); // #555555
    private static readonly Color ColorBadgeConnected = Color.FromArgb(255,  16, 124,  16); // #107C10
    private static readonly Color ColorBorderNormal   = Color.FromArgb(255,  68,  68,  68); // #444444
    private static readonly Color ColorBorderActive   = Color.FromArgb(255, 255, 255, 255); // white

    // ── Fields ────────────────────────────────────────────────────────────────

    private SlotViewModel? _slot;
    private bool           _isRumbling;
    private bool           _isRenaming;
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

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>Fired when the user commits a nickname rename. Payload is the new nickname (empty = clear).</summary>
    public event EventHandler<string>? NicknameChanged;

    // ── Slot data ─────────────────────────────────────────────────────────────

    /// <summary>The physical XInput slot index (0–3) this card represents.</summary>
    public int SlotIndex => _slot?.SlotIndex ?? -1;

    /// <summary>VID:PID in uppercase hex (e.g. "045E:02FD"). Empty when no HID match.</summary>
    public string VidPid => _slot?.VidPid ?? string.Empty;

    /// <summary>HID device path for this controller, or null when no HID match.</summary>
    public string? DevicePath => _slot?.DevicePath;

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

        DeviceName.Text = _slot.DisplayName;

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
        switch (_currentState)
        {
            case CardState.Normal:
                Opacity = 1.0;
                AnimateScale(1.0);
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                break;

            case CardState.Focused:
                Opacity = 1.0;
                AnimateScale(1.03);
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderActive);
                CardBorder.BorderThickness = new Thickness(3);
                break;

            case CardState.Selected:
                Opacity = 1.0;
                AnimateScale(1.03);
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderActive);
                CardBorder.BorderThickness = new Thickness(4);
                break;

            case CardState.Dimmed:
                Opacity = 0.5;
                AnimateScale(1.0);
                CardBorder.BorderBrush     = new SolidColorBrush(ColorBorderNormal);
                CardBorder.BorderThickness = new Thickness(1);
                break;
        }
    }

    /// <summary>
    /// Smooth scale-swell animation using the Composition API (avoids Storyboard overhead).
    /// Animates the UserControl's own visual — one level above CardBorder — so the scale
    /// overflows into the 8px margin the parent SlotPanel sets on each card rather than
    /// being clipped by the ScrollViewer's ScrollContentPresenter clip rect.
    /// </summary>
    private void AnimateScale(double toScale)
    {
        // Guard: compositor is not available until the element is in the visual tree.
        if (!IsLoaded || ActualWidth == 0) return;

        // Animate 'this' (the UserControl visual), not CardBorder.
        // The UserControl's parent (StackPanel) does not apply a compositor clip, so
        // the scaled visual can safely expand into the surrounding margin without clipping.
        var visual = ElementCompositionPreview.GetElementVisual(this);

        // Scale from center of the UserControl (which equals the card surface).
        visual.CenterPoint = new Vector3(
            (float)(ActualWidth  / 2),
            (float)(ActualHeight / 2),
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

    // ── Layout scale (handheld ↔ full-screen) ─────────────────────────────────

    /// <summary>
    /// Adjusts font sizes, padding, and corner radius for handheld (1.0) vs full-screen (~1.8) layout.
    /// Called by MainWindow when the layout mode changes.
    /// </summary>
    public void SetLayoutScale(double scale)
    {
        // Player badge
        SlotBadge.FontSize = 14 * scale;
        PlayerBadgeBorder.Padding      = new Thickness(10 * scale, 5 * scale, 10 * scale, 5 * scale);
        PlayerBadgeBorder.CornerRadius = new CornerRadius(8 * scale);
        PlayerBadgeBorder.Margin       = new Thickness(0, 0, 14 * scale, 0);

        // Device name + rename box
        DeviceName.FontSize = 15 * scale;
        RenameBox.FontSize  = 15 * scale;

        // Connection text
        ConnectionText.FontSize = 11 * scale;

        // Badge fonts
        IntegratedText.FontSize = 10 * scale;
        BrandText.FontSize      = 10 * scale;

        // VID:PID
        VidPidText.FontSize = 10 * scale;

        // Battery
        BatteryIcon.FontSize = 14 * scale;
        BatteryText.FontSize = 11 * scale;

        // Card border
        CardBorder.Padding      = new Thickness(16 * scale, 14 * scale, 16 * scale, 14 * scale);
        CardBorder.CornerRadius = new CornerRadius(12 * scale);
        CardBorder.MinHeight    = 80 * scale;

        // Empty dash
        EmptyDash.FontSize = 20 * scale;
    }

    // ── Hold-to-reorder progress bar ──────────────────────────────────────────

    /// <summary>
    /// Show the hold-progress bar and set its fill to [0,1].
    /// Called by MainWindow every nav tick while the A button is held.
    /// </summary>
    public void ShowHoldProgress(double value)
    {
        HoldProgressBar.Value      = Math.Clamp(value, 0.0, 1.0);
        HoldProgressBar.Visibility = Visibility.Visible;
    }

    /// <summary>Collapse and reset the progress bar.</summary>
    public void HideHoldProgress()
    {
        HoldProgressBar.Visibility = Visibility.Collapsed;
        HoldProgressBar.Value      = 0;
    }

    // ── Rumble ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set hold-progress rumble intensity (0–65535) on both motors.
    /// Called by MainWindow every HoldTimer tick while A is held.
    /// Safe to call if not connected — silently skips.
    /// </summary>
    public void SetHoldRumble(ushort intensity)
    {
        if (_slot is null || !_slot.IsConnected) return;
        XInput.SetVibration((uint)_slot.SlotIndex, intensity, intensity);
    }

    /// <summary>
    /// Stop any in-progress hold rumble immediately (sets both motors to 0).
    /// Safe to call if not connected — silently skips.
    /// </summary>
    public void StopHoldRumble()
    {
        if (_slot is null || !_slot.IsConnected) return;
        XInput.SetVibration((uint)_slot.SlotIndex, 0, 0);
    }

    /// <summary>
    /// Fire a 200ms rumble pulse at 25% strength.
    /// Called by MainWindow on A-tap and by the mouse/touch Tapped event.
    /// Safe to call if not connected — silently skips.
    /// </summary>
    public void TriggerRumble() => _ = RumbleAsync();

    private async Task RumbleAsync()
    {
        if (_slot is null || !_slot.IsConnected || _isRumbling) return;

        _isRumbling = true;
        try
        {
            XInput.SetVibration((uint)_slot.SlotIndex, TapRumbleIntensity, TapRumbleIntensity);

            JiggleStoryboard.Begin();

            await Task.Delay(TapRumbleDurationMs);

            XInput.SetVibration((uint)_slot.SlotIndex, 0, 0);
            ApplyCardState();
        }
        finally
        {
            _isRumbling = false;
        }
    }

    // ── Mouse / touch tap ─────────────────────────────────────────────────────

    /// <summary>
    /// Mouse/touch tap handler — rumbles to identify this controller.
    /// Gamepad A-tap is handled separately by MainWindow (hold vs tap detection).
    /// </summary>
    private void SlotCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_isRenaming) TriggerRumble();
    }

    // ── Inline rename (double-click) ────────────────────────────────────────

    /// <summary>
    /// Double-click handler — enters inline rename mode for the controller name.
    /// Only works when a controller is connected.
    /// </summary>
    private void SlotCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_slot is null || !_slot.IsConnected || _isRenaming) return;
        BeginRename();
    }

    private void BeginRename()
    {
        _isRenaming = true;
        RenameBox.Text = DeviceName.Text;
        DeviceName.Visibility = Visibility.Collapsed;
        RenameBox.Visibility  = Visibility.Visible;
        RenameBox.SelectAll();
        RenameBox.Focus(FocusState.Programmatic);
    }

    private void CommitRename()
    {
        if (!_isRenaming) return;
        _isRenaming = false;

        var newName = RenameBox.Text?.Trim() ?? string.Empty;
        RenameBox.Visibility  = Visibility.Collapsed;
        DeviceName.Visibility = Visibility.Visible;

        // Fire event so MainWindow can persist the nickname.
        NicknameChanged?.Invoke(this, newName);
    }

    private void CancelRename()
    {
        if (!_isRenaming) return;
        _isRenaming = false;

        RenameBox.Visibility  = Visibility.Collapsed;
        DeviceName.Visibility = Visibility.Visible;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRename();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelRename();
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Commit on focus loss (clicking elsewhere).
        CommitRename();
    }
}
