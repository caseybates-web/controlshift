using System.Diagnostics;
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

    private readonly MainViewModel   _viewModel;
    private readonly DispatcherTimer _pollTimer;
    private readonly SlotCard[]      _cards = new SlotCard[4];

    // ── Navigation state ──────────────────────────────────────────────────────

    /// <summary>Index of the card that currently has keyboard/gamepad focus. -1 = none.</summary>
    private int _focusedCardIndex = -1;
    /// <summary>Index of the card currently being reordered. -1 = not in reorder mode.</summary>
    private int _reorderingIndex  = -1;
    /// <summary>Snapshot of _cards[] when reorder mode was entered, for snap-back on cancel.</summary>
    private readonly SlotCard[] _savedOrder = new SlotCard[4];

    // ── XInput polling ────────────────────────────────────────────────────────

    private readonly DispatcherTimer _navTimer;
    private GamepadButtons           _prevGamepadButtons;
    private short                    _prevLeftThumbY;
    private bool                     _windowActive = true;

    // ── Diagnostic logging ────────────────────────────────────────────────────

    // Timestamped log file in %TEMP% — written even in Release, survives a crash.
    private static readonly string NavLogPath =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ControlShift-nav-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    private static void NavLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
        try { System.IO.File.AppendAllText(NavLogPath, line + System.Environment.NewLine); }
        catch { /* log writes must never throw */ }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        NavLog($"MainWindow ctor — log: {NavLogPath}");

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

        SetWindowSize(400, 700);

        // Build 4 fixed slot cards — one per XInput slot P1–P4.
        for (int i = 0; i < 4; i++)
        {
            _cards[i] = new SlotCard();
            SlotPanel.Children.Add(_cards[i]);
        }

        // Defer XYFocus links, TabIndex, and focus events to ContentGrid.Loaded.
        // Setting XYFocusUp/Down or TabIndex before the compositor tree is ready
        // (before the first layout pass) triggers STATUS_ASSERTION_FAILURE in WinUI 3.
        ContentGrid.Loaded += OnContentGridLoaded;

        // Poll for device changes every 5 seconds.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        // Poll XInput at 16ms (~60 fps) for A/B buttons and D-pad navigation.
        // DispatcherTimer fires on the UI thread, so no DispatcherQueue.TryEnqueue needed.
        _navTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
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
    /// Fires once when ContentGrid completes its first layout pass (window first shown).
    /// XYFocusUp/Down and TabIndex must not be set before this point.
    /// </summary>
    private void OnContentGridLoaded(object sender, RoutedEventArgs e)
    {
        ContentGrid.Loaded -= OnContentGridLoaded;
        NavLog("OnContentGridLoaded — wiring XYFocus + TabIndex + focus events");

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
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _navTimer.Stop();
        this.Close();
    }

    // ── Navigation: XInput polling ────────────────────────────────────────────

    private void NavTimer_Tick(object? sender, object e)
    {
        try
        {
            if (!_windowActive) return;

            // ── Phase 1: read XInput state (pure P/Invoke, no UI side-effects) ──────

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

            bool navUp   = (newPresses & GamepadButtons.DPadUp)   != 0 || stickUp;
            bool navDown = (newPresses & GamepadButtons.DPadDown)  != 0 || stickDown;
            bool pressA  = (newPresses & GamepadButtons.A) != 0;
            bool pressB  = (newPresses & GamepadButtons.B) != 0;

            // Nothing actionable this tick — skip early.
            if (!navUp && !navDown && !pressA && !pressB) return;

            // ── Phase 2: UI mutations — DispatcherTimer already fires on the UI thread ──
            NavLog($"[NavTick] reorderIdx={_reorderingIndex} focusedIdx={_focusedCardIndex} " +
                   $"up={navUp} down={navDown} A={pressA} B={pressB}");

            if (_reorderingIndex >= 0)
            {
                // Reorder mode: D-pad/stick moves the selected card; A confirms; B cancels.
                if (navUp)   { NavLog("[NavTick] → MoveReorderingCard(-1)"); MoveReorderingCard(-1); }
                if (navDown) { NavLog("[NavTick] → MoveReorderingCard(+1)"); MoveReorderingCard(+1); }
                if (pressA)  { NavLog("[NavTick] → ConfirmReorder");         ConfirmReorder(); }
                if (pressB)  { NavLog("[NavTick] → CancelReorder");          CancelReorder(); }
            }
            else
            {
                // Normal mode: D-pad/stick moves focus; A enters reorder mode.
                if (navUp && _focusedCardIndex > 0)
                {
                    NavLog($"[NavTick] → Focus card {_focusedCardIndex - 1} (up)");
                    _cards[_focusedCardIndex - 1].Focus(FocusState.Programmatic);
                }
                if (navDown && _focusedCardIndex >= 0 && _focusedCardIndex < _cards.Length - 1)
                {
                    NavLog($"[NavTick] → Focus card {_focusedCardIndex + 1} (down)");
                    _cards[_focusedCardIndex + 1].Focus(FocusState.Programmatic);
                }
                if (pressA && _focusedCardIndex >= 0)
                {
                    NavLog($"[NavTick] → StartReorder on card {_focusedCardIndex}");
                    StartReorder();
                }
            }

            NavLog("[NavTick] done");
        }
        catch (Exception ex)
        {
            NavLog($"[NavTimer_Tick ERROR] {ex}");
        }
    }

    // ── Navigation: keyboard ──────────────────────────────────────────────────

    /// <summary>
    /// Root-Grid KeyDown handler — catches key events from any focused child.
    /// Enter/Space = A, Escape = B, arrow keys move card in reorder mode.
    /// Tab/Shift+Tab is handled by the WinUI 3 focus system automatically.
    /// </summary>
    private void ContentGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_reorderingIndex >= 0)
        {
            switch (e.Key)
            {
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    NavLog("[KeyDown] MoveReorderingCard(-1)");
                    MoveReorderingCard(-1);
                    e.Handled = true;
                    break;
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    NavLog("[KeyDown] MoveReorderingCard(+1)");
                    MoveReorderingCard(+1);
                    e.Handled = true;
                    break;
                case VirtualKey.Enter:
                case VirtualKey.Space:
                case VirtualKey.GamepadA:
                    NavLog("[KeyDown] ConfirmReorder");
                    ConfirmReorder();
                    e.Handled = true;
                    break;
                case VirtualKey.Escape:
                case VirtualKey.GamepadB:
                    NavLog("[KeyDown] CancelReorder");
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
                        NavLog($"[KeyDown] StartReorder on card {_focusedCardIndex}");
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
        NavLog($"[StartReorder] card {_focusedCardIndex}");
        _reorderingIndex = _focusedCardIndex;
        Array.Copy(_cards, _savedOrder, _cards.Length);
        UpdateCardStates();
    }

    private void ConfirmReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[ConfirmReorder] card now at idx {_reorderingIndex}");
        _focusedCardIndex = _reorderingIndex;
        _reorderingIndex  = -1;
        UpdateCardStates();
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[CancelReorder] restoring from reorderIdx={_reorderingIndex}");

        // Clear XYFocus links before removing any element from the visual tree.
        // WinUI's focus engine follows XYFocusUp/Down when an element loses focus
        // on removal; if the link target is itself being removed, WinUI throws a
        // native assertion (STATUS_ASSERTION_FAILURE).
        ClearXYFocusLinks();

        SlotPanel.Children.Clear();
        Array.Copy(_savedOrder, _cards, _cards.Length);
        foreach (var card in _cards)
            SlotPanel.Children.Add(card);

        // All cards are back in the tree — safe to restore XYFocus links now.
        UpdateXYFocusLinks();

        _focusedCardIndex = _reorderingIndex;
        _reorderingIndex  = -1;

        UpdateCardStates();
        _cards[_focusedCardIndex].Focus(FocusState.Programmatic);
        NavLog("[CancelReorder] done");
    }

    private void MoveReorderingCard(int direction)
    {
        if (_reorderingIndex < 0) return;
        int newIdx = _reorderingIndex + direction;
        if (newIdx < 0 || newIdx >= _cards.Length) return;

        NavLog($"[MoveReorderingCard] {_reorderingIndex} → {newIdx}");

        // Clear XYFocus links before touching the children collection.
        // WinUI asserts if its focus engine follows an XYFocus link to an element
        // that is currently being removed from or not yet re-inserted into the tree.
        ClearXYFocusLinks();

        var movingCard = _cards[_reorderingIndex];
        SlotPanel.Children.RemoveAt(_reorderingIndex);
        SlotPanel.Children.Insert(newIdx, movingCard);

        (_cards[_reorderingIndex], _cards[newIdx]) = (_cards[newIdx], _cards[_reorderingIndex]);

        // All cards are back in the tree — rebuild TabIndex and XYFocus.
        for (int i = 0; i < _cards.Length; i++)
            _cards[i].TabIndex = i;
        UpdateXYFocusLinks();

        _reorderingIndex = newIdx;
        UpdateCardStates();
        NavLog($"[MoveReorderingCard] done, reorderIdx now {_reorderingIndex}");
    }

    // ── Navigation: visual state ──────────────────────────────────────────────

    private void UpdateCardStates()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            CardState state;
            if (_reorderingIndex >= 0)
                state = i == _reorderingIndex ? CardState.Selected : CardState.Dimmed;
            else
                state = i == _focusedCardIndex ? CardState.Focused : CardState.Normal;

            _cards[i].SetCardState(state);
        }
    }

    /// <summary>
    /// Nulls out all XYFocusUp/Down links on every card.
    /// Must be called before any SlotPanel.Children modification (RemoveAt, Clear)
    /// to prevent WinUI's focus engine from following stale links to detached elements.
    /// </summary>
    private void ClearXYFocusLinks()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            _cards[i].XYFocusUp   = null;
            _cards[i].XYFocusDown = null;
        }
    }

    /// <summary>
    /// Sets XYFocusUp/Down so WinUI's built-in D-pad focus navigation chains through
    /// the cards top-to-bottom. Only call after all cards are in the visual tree.
    /// </summary>
    private void UpdateXYFocusLinks()
    {
        try
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i].XYFocusUp   = i > 0                ? _cards[i - 1] : null;
                _cards[i].XYFocusDown = i < _cards.Length - 1 ? _cards[i + 1] : null;
            }
        }
        catch (Exception ex)
        {
            NavLog($"[UpdateXYFocusLinks ERROR] {ex}");
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
