using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Vortice.XInput;
using Windows.Graphics;
using Windows.System;
using ControlShift.App.Controls;
using ControlShift.App.ViewModels;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.App;

/// <summary>
/// Standalone Xbox-aesthetic window (~480×600 logical px).
/// Shows 4 controller slot cards. Click a card to rumble it.
/// D-pad / Tab navigates cards; A / Enter selects for reordering; B / Escape cancels.
/// Polls for device changes every 5 seconds.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Core ──────────────────────────────────────────────────────────────────

    private readonly MainViewModel    _viewModel;
    private readonly DispatcherTimer  _pollTimer;
    private readonly SlotCard[]       _cards = new SlotCard[4];

    // ── Navigation state ──────────────────────────────────────────────────────

    /// <summary>Index into _cards[] of the card that currently has keyboard/gamepad focus. -1 = none.</summary>
    private int _focusedCardIndex  = -1;
    /// <summary>Index into _cards[] of the card currently being reordered. -1 = not in reorder mode.</summary>
    private int _reorderingIndex   = -1;
    /// <summary>Snapshot of _cards[] at the moment reorder mode was entered, for snap-back on cancel.</summary>
    private readonly SlotCard[] _savedOrder = new SlotCard[4];

    // ── XInput input polling ──────────────────────────────────────────────────

    private readonly DispatcherTimer _navTimer;
    private GamepadButtons           _prevGamepadButtons;
    private short                    _prevLeftThumbY;
    private bool                     _windowActive = true;

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        string dbPath      = System.IO.Path.Combine(AppContext.BaseDirectory, "devices", "known-devices.json");
        string vendorsPath = System.IO.Path.Combine(AppContext.BaseDirectory, "devices", "known-vendors.json");

        // DECISION: Both databases fall back to empty lists if their JSON files are missing.
        IDeviceFingerprinter fingerprinter;
        IVendorDatabase      vendorDb;

        try   { fingerprinter = DeviceFingerprinter.FromFile(dbPath); }
        catch { fingerprinter = new DeviceFingerprinter(Array.Empty<KnownDeviceEntry>()); }

        try   { vendorDb = VendorDatabase.FromFile(vendorsPath); }
        catch { vendorDb = new VendorDatabase(Array.Empty<KnownVendorEntry>()); }

        var matcher = new ControllerMatcher(vendorDb, fingerprinter);
        _viewModel  = new MainViewModel(new XInputEnumerator(), new HidEnumerator(), matcher);

        SetWindowSize(480, 600);

        // Build 4 fixed slot cards — one per XInput slot P1–P4.
        for (int i = 0; i < 4; i++)
        {
            _cards[i] = new SlotCard();
            SlotPanel.Children.Add(_cards[i]);
        }

        // Defer XYFocus links, TabIndex, and focus event wiring to ContentGrid.Loaded.
        // Setting XYFocusUp/Down or TabIndex before the window compositor is ready
        // (before the first layout pass) triggers STATUS_ASSERTION_FAILURE in WinUI 3.
        ContentGrid.Loaded += OnContentGridLoaded;

        // Poll for device changes every 5 seconds.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        // Poll XInput at 100ms for A/B buttons and D-pad navigation.
        _navTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _navTimer.Tick += NavTimer_Tick;
        _navTimer.Start();

        // Gate XInput polling on window focus so we don't steal input from other apps.
        Activated += (_, args) =>
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;

        Closed += (_, _) => { _pollTimer.Stop(); _navTimer.Stop(); };

        // Initial scan on window open.
        _ = RefreshAsync();
    }

    // ── Navigation: deferred setup ────────────────────────────────────────────

    /// <summary>
    /// Fires once when ContentGrid completes its first layout pass.
    /// XYFocusUp/Down and TabIndex must not be set before this — doing so before the
    /// compositor tree is ready causes STATUS_ASSERTION_FAILURE (0xc000027b) in WinUI 3.
    /// </summary>
    private void OnContentGridLoaded(object sender, RoutedEventArgs e)
    {
        ContentGrid.Loaded -= OnContentGridLoaded;

        UpdateXYFocusLinks();
        for (int i = 0; i < 4; i++)
        {
            _cards[i].TabIndex = i;
            int idx = i; // capture for closure
            _cards[i].GotFocus  += (_, _) => OnCardGotFocus(idx);
            _cards[i].LostFocus += (_, _) => OnCardLostFocus(idx);
        }
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        StatusText.Text = "Scanning...";
        await _viewModel.RefreshAsync();
        UpdateCards();
    }

    private void UpdateCards()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            if (i < _viewModel.Slots.Count)
                _cards[i].SetSlot(_viewModel.Slots[i]);
        }

        int connected = _viewModel.Slots.Count(s => s.IsConnected);
        StatusText.Text = connected > 0
            ? $"{connected} controller{(connected != 1 ? "s" : "")} connected"
            : "No controllers detected";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    // ── Navigation: XInput polling ────────────────────────────────────────────

    private void NavTimer_Tick(object? sender, object e)
    {
        if (!_windowActive) return;

        // Aggregate button state across all four slots — any connected controller can navigate.
        GamepadButtons current  = default;
        short          maxThumbY = 0;

        for (uint i = 0; i < 4; i++)
        {
            if (XInput.GetState(i, out State state))
            {
                current |= state.Gamepad.Buttons;
                if (Math.Abs(state.Gamepad.LeftThumbY) > Math.Abs(maxThumbY))
                    maxThumbY = state.Gamepad.LeftThumbY;
            }
        }

        // Edge-detect: only act on newly-pressed buttons (not held).
        GamepadButtons newPresses = current & ~_prevGamepadButtons;
        _prevGamepadButtons = current;

        // Thumbstick threshold crossing (~25% of range).
        const short Threshold = 8192;
        bool stickUp   = maxThumbY >  Threshold && _prevLeftThumbY <=  Threshold;
        bool stickDown = maxThumbY < -Threshold && _prevLeftThumbY >= -Threshold;
        _prevLeftThumbY = maxThumbY;

        bool dpadUp   = (newPresses & GamepadButtons.DPadUp)   != 0;
        bool dpadDown = (newPresses & GamepadButtons.DPadDown)  != 0;
        bool pressA   = (newPresses & GamepadButtons.A) != 0;
        bool pressB   = (newPresses & GamepadButtons.B) != 0;

        bool navUp   = dpadUp   || stickUp;
        bool navDown = dpadDown || stickDown;

        if (_reorderingIndex >= 0)
        {
            // Reorder mode: D-pad/stick moves the selected card; A confirms; B cancels.
            if (navUp)   MoveReorderingCard(-1);
            if (navDown) MoveReorderingCard(+1);
            if (pressA)  ConfirmReorder();
            if (pressB)  CancelReorder();
        }
        else
        {
            // Normal mode: D-pad/stick moves focus; A enters reorder mode.
            // (WinUI 3 XYFocus also handles D-pad natively — polling catches A/B.)
            if (navUp   && _focusedCardIndex > 0)
                _cards[_focusedCardIndex - 1].Focus(FocusState.Programmatic);
            if (navDown && _focusedCardIndex >= 0 && _focusedCardIndex < _cards.Length - 1)
                _cards[_focusedCardIndex + 1].Focus(FocusState.Programmatic);
            if (pressA && _focusedCardIndex >= 0)
                StartReorder();
        }
    }

    // ── Navigation: keyboard ──────────────────────────────────────────────────

    /// <summary>
    /// Root-Grid KeyDown — catches key events bubbling from any focused child.
    /// Handles: Enter/Space = A, Escape = B, arrow keys in reorder mode.
    /// Tab/Shift+Tab is handled by the WinUI 3 focus system automatically.
    /// </summary>
    private void ContentGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_reorderingIndex >= 0)
        {
            // Reorder mode: intercept arrow keys so they move the card.
            switch (e.Key)
            {
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    MoveReorderingCard(-1);
                    e.Handled = true;
                    break;
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    MoveReorderingCard(+1);
                    e.Handled = true;
                    break;
                case VirtualKey.Enter:
                case VirtualKey.Space:
                case VirtualKey.GamepadA:
                    ConfirmReorder();
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                case VirtualKey.GamepadB:
                    CancelReorder();
                    e.Handled = true;
                    break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                case VirtualKey.Space:
                case VirtualKey.GamepadA:
                    if (_focusedCardIndex >= 0)
                    {
                        StartReorder();
                        e.Handled = true;
                    }
                    break;
            }
        }
    }

    // ── Navigation: focus tracking ────────────────────────────────────────────

    private void OnCardGotFocus(int idx)
    {
        _focusedCardIndex = idx;
        UpdateCardStates();
    }

    private void OnCardLostFocus(int idx)
    {
        if (_focusedCardIndex == idx && _reorderingIndex < 0)
        {
            _focusedCardIndex = -1;
            UpdateCardStates();
        }
    }

    // ── Navigation: reorder state machine ────────────────────────────────────

    private void StartReorder()
    {
        if (_focusedCardIndex < 0) return;
        _reorderingIndex = _focusedCardIndex;
        Array.Copy(_cards, _savedOrder, _cards.Length);
        UpdateCardStates();
    }

    private void ConfirmReorder()
    {
        if (_reorderingIndex < 0) return;
        _focusedCardIndex = _reorderingIndex;
        _reorderingIndex  = -1;
        UpdateCardStates();
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;

        // Restore the original visual order from the saved snapshot.
        SlotPanel.Children.Clear();
        Array.Copy(_savedOrder, _cards, _cards.Length);
        foreach (var card in _cards)
            SlotPanel.Children.Add(card);

        UpdateXYFocusLinks();

        _focusedCardIndex = _reorderingIndex;
        _reorderingIndex  = -1;

        UpdateCardStates();
        _cards[_focusedCardIndex].Focus(FocusState.Programmatic);
    }

    private void MoveReorderingCard(int direction)
    {
        if (_reorderingIndex < 0) return;
        int newIdx = _reorderingIndex + direction;
        if (newIdx < 0 || newIdx >= _cards.Length) return;

        // Swap in panel.
        var movingCard = _cards[_reorderingIndex];
        SlotPanel.Children.RemoveAt(_reorderingIndex);
        SlotPanel.Children.Insert(newIdx, movingCard);

        // Swap in array.
        (_cards[_reorderingIndex], _cards[newIdx]) = (_cards[newIdx], _cards[_reorderingIndex]);

        // Rebuild tab order and XYFocus to match new visual order.
        for (int i = 0; i < _cards.Length; i++)
            _cards[i].TabIndex = i;
        UpdateXYFocusLinks();

        _reorderingIndex = newIdx;
        UpdateCardStates();
    }

    // ── Navigation: visual state ──────────────────────────────────────────────

    private void UpdateCardStates()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            CardState state;
            if (_reorderingIndex >= 0)
            {
                state = i == _reorderingIndex ? CardState.Selected : CardState.Dimmed;
            }
            else
            {
                state = i == _focusedCardIndex ? CardState.Focused : CardState.Normal;
            }
            _cards[i].SetCardState(state);
        }
    }

    private void UpdateXYFocusLinks()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].XYFocusUp   = i > 0                ? _cards[i - 1] : null;
            _cards[i].XYFocusDown = i < _cards.Length - 1 ? _cards[i + 1] : null;
        }
    }

    // ── Window sizing ─────────────────────────────────────────────────────────

    private void SetWindowSize(int logicalWidth, int logicalHeight)
    {
        var hwnd  = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi   = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;

        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        appWindow.Resize(new SizeInt32(
            (int)(logicalWidth  * scale),
            (int)(logicalHeight * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
