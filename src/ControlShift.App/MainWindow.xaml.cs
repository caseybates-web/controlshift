using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vortice.XInput;
using Windows.Graphics;
using Windows.System;
using ControlShift.App.Controls;
using ControlShift.App.ViewModels;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Forwarding;

namespace ControlShift.App;

/// <summary>
/// Standalone Xbox-aesthetic window (400×700 logical px).
/// Shows 4 controller slot cards. Click a card to rumble it.
/// D-pad / Tab navigates cards; A / Enter selects for reordering; B / Escape cancels.
/// Polls for device changes every 5 seconds.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int    WindowWidth             = 400;
    private const int    WindowHeight            = 700;
    private const double UiPollIntervalMs        = 16.0;   // ~60 fps for gamepad input
    private const double DevicePollSeconds       = 5.0;    // device enumeration refresh
    private const double WatchdogIntervalSeconds = 5.0;    // nav timer health check
    private const short  StickDeadzoneThreshold  = 8192;   // ~25% of ±32768 range
    private const double HoldProgressDelayMs     = 200.0;  // show progress bar after this
    private const double HoldProgressRangeMs     = 300.0;  // bar fills over [200ms, 500ms]
    private const double HoldReorderThresholdMs  = 500.0;  // enter reorder mode at this
    private const ushort RumbleHoldMin           = 6553;   // ~10% intensity at hold start
    private const ushort RumbleHoldRange         = 19661;  // ramps to ~40% at threshold

    // ── Core ──────────────────────────────────────────────────────────────────

    private readonly MainViewModel   _viewModel;
    private readonly DispatcherTimer _pollTimer;
    private readonly List<SlotCard>  _cards = new();

    // ── Forwarding stack ─────────────────────────────────────────────────────

    private readonly IInputForwardingService _forwardingService;
    private readonly SlotOrderStore  _slotOrderStore = new();

    // ── Navigation state ──────────────────────────────────────────────────────

    /// <summary>Currently focused UI element (SlotCard or Button). Null = none.</summary>
    private UIElement? _focusedElement;
    /// <summary>Index of the card currently being reordered (in _cards). -1 = not in reorder mode.</summary>
    private int _reorderingIndex  = -1;
    /// <summary>Snapshot of _cards when reorder mode was entered, for snap-back on cancel.</summary>
    private readonly List<SlotCard> _savedOrder = new();

    // ── XInput polling ────────────────────────────────────────────────────────

    private readonly DispatcherTimer _navTimer;
    private readonly DispatcherTimer _watchdogTimer;
    private          GamepadButtons  _prevGamepadButtons;
    private          short           _prevLeftThumbY;
    private          bool            _windowActive = true;

    // A-button hold/tap state
    private DateTime? _aHoldStart;           // when A was first pressed; null when not held
    private bool      _aHoldEnteredReorder;  // true once the 500ms threshold was crossed
    private int       _navTickCount;         // total ticks; reset by watchdog every 5s
    private int       _holdTickCount;        // hold-timer ticks since last A press; reset on release/cancel/confirm

    // Dedicated timer for hold-A progress bar animation.
    // Initialized once in the constructor; reused on every A press (never recreated).
    private readonly DispatcherTimer _holdTimer;

    // When true, OnCardGotFocus / OnCardLostFocus are no-ops.
    // Set during CancelReorder/ConfirmReorder so that the Focus() call we issue
    // doesn't let event handlers corrupt _focusedCardIndex while we're explicitly
    // managing all state ourselves.
    private bool _suppressFocusEvents;

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

    public MainWindow(IInputForwardingService forwardingService)
    {
        _forwardingService = forwardingService;
        _forwardingService.ForwardingError += OnForwardingError;

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

        var busDetector = new PnpBusDetector();
        var matcher     = new ControllerMatcher(vendorDb, fingerprinter, busDetector);
        _viewModel      = new MainViewModel(new XInputEnumerator(), new HidEnumerator(), matcher);

        SetWindowSize(WindowWidth, WindowHeight);

        // Cards are created dynamically in UpdateCards() based on how many
        // non-excluded slots the ViewModel returns (could be 1–4).

        // Defer XYFocus links, TabIndex, and focus events to ContentGrid.Loaded.
        // Setting XYFocusUp/Down or TabIndex before the compositor tree is ready
        // (before the first layout pass) triggers STATUS_ASSERTION_FAILURE in WinUI 3.
        ContentGrid.Loaded += OnContentGridLoaded;

        // Poll for device changes every 5 seconds.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DevicePollSeconds) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        // Poll XInput at ~60 fps for A/B buttons and D-pad navigation.
        // DispatcherTimer fires on the UI thread, so no DispatcherQueue.TryEnqueue needed.
        _navTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiPollIntervalMs) };
        _navTimer.Tick += NavTimer_Tick;
        _navTimer.Start();

        // ONE hold timer — created here, reused for every A press, never recreated.
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiPollIntervalMs) };
        _holdTimer.Tick += HoldTimer_Tick;

        // Watchdog: every 5s verify the nav timer is still running and restart if not.
        // Guards against any scenario where the timer silently stops (driver errors,
        // unhandled exceptions escaping the catch, or unexpected Stop() calls).
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(WatchdogIntervalSeconds) };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
        _watchdogTimer.Start();

        // Gate XInput polling on window focus so we don't steal input from other apps.
        Activated += (_, args) =>
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;

        Closed += OnWindowClosed;

        // XInput diagnostic dump — written once on startup before any abstraction.
        WriteXInputDiagnostic();

        // Initial scan on window open.
        _ = RefreshAsync();
    }

    // ── Navigation: deferred setup ────────────────────────────────────────────

    /// <summary>
    /// Fires once when ContentGrid completes its first layout pass (window first shown).
    /// TabIndex must not be set before this point.
    /// </summary>
    private void OnContentGridLoaded(object sender, RoutedEventArgs e)
    {
        ContentGrid.Loaded -= OnContentGridLoaded;
        NavLog("OnContentGridLoaded — wiring focus events on footer buttons");

        // Wire focus tracking on footer buttons (cards are wired dynamically in EnsureCards).
        RevertButton.GotFocus  += (_, _) => OnElementGotFocus(RevertButton);
        RevertButton.LostFocus += (_, _) => OnElementLostFocus(RevertButton);
        ExitButton.GotFocus    += (_, _) => OnElementGotFocus(ExitButton);
        ExitButton.LostFocus   += (_, _) => OnElementLostFocus(ExitButton);

        // Per-device forwarding is started on-demand via ApplyForwardingAsync()
        // when the user confirms a reorder. No upfront initialization needed.
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        await _viewModel.RefreshAsync();
        UpdateCards();
    }

    private void UpdateCards()
    {
        // If in reorder mode, don't recreate cards — just update data on existing ones.
        if (_reorderingIndex >= 0)
        {
            RebuildCardsFromPanel();
            for (int i = 0; i < _cards.Count && i < _viewModel.Slots.Count; i++)
                _cards[i].SetSlot(_viewModel.Slots[i]);
            return;
        }

        EnsureCards(_viewModel.Slots.Count);

        for (int i = 0; i < _cards.Count && i < _viewModel.Slots.Count; i++)
            _cards[i].SetSlot(_viewModel.Slots[i]);

        // After cards have data, reorder to match the user's saved preferred order.
        ApplySavedOrder();
    }

    /// <summary>
    /// Ensures SlotPanel has exactly <paramref name="count"/> SlotCards.
    /// Creates or removes cards as needed; wires focus events on new cards.
    /// </summary>
    private void EnsureCards(int count)
    {
        // Remove excess cards
        while (_cards.Count > count)
        {
            var card = _cards[^1];
            _cards.RemoveAt(_cards.Count - 1);
            SlotPanel.Children.Remove(card);
        }

        // Add missing cards
        while (_cards.Count < count)
        {
            var card = new SlotCard { Margin = new Thickness(8, 4, 8, 4) };
            card.GotFocus  += (_, _) => OnElementGotFocus(card);
            card.LostFocus += (_, _) => OnElementLostFocus(card);
            _cards.Add(card);
            SlotPanel.Children.Add(card);
        }

        // Sync TabIndex
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].TabIndex = i;
    }

    /// <summary>
    /// Rebuilds _cards from SlotPanel.Children in visual top-to-bottom order.
    /// Call after ANY operation that modifies SlotPanel.Children (reorder, cancel,
    /// refresh) to guarantee _cards always matches what the user sees on screen.
    /// </summary>
    private void RebuildCardsFromPanel()
    {
        _cards.Clear();
        _cards.AddRange(SlotPanel.Children.OfType<SlotCard>());
    }

    // ── Positional navigation helpers ──────────────────────────────────────

    /// <summary>
    /// Returns all visible, focusable elements in top-to-bottom visual order:
    /// first all SlotCards from SlotPanel, then visible footer buttons.
    /// </summary>
    private List<UIElement> GetFocusableElementsInOrder()
    {
        var elements = new List<UIElement>();
        foreach (var card in _cards)
            elements.Add(card);

        if (RevertButton.Visibility == Visibility.Visible)
            elements.Add(RevertButton);
        elements.Add(ExitButton);

        return elements;
    }

    /// <summary>Returns the currently focused card, or null if a button is focused.</summary>
    private SlotCard? FocusedCard => _focusedElement as SlotCard;

    /// <summary>Returns the index of the focused card in _cards, or -1.</summary>
    private int FocusedCardIndex => FocusedCard is { } card ? _cards.IndexOf(card) : -1;

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _navTimer.Stop();
        this.Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _pollTimer.Stop();
        _navTimer.Stop();
        _watchdogTimer.Stop();
        _holdTimer.Stop();
        _forwardingService.Dispose();
    }

    // ── Forwarding ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops any active forwarding, builds slot assignments from the current card order,
    /// and starts per-device HID→ViGEm forwarding.
    /// </summary>
    private async Task ApplyForwardingAsync()
    {
        try
        {
            await _forwardingService.StopForwardingAsync();
            var assignments = new List<ControlShift.Core.Models.SlotAssignment>();
            for (int visualPos = 0; visualPos < _cards.Count; visualPos++)
            {
                var card = _cards[visualPos];
                assignments.Add(new ControlShift.Core.Models.SlotAssignment
                {
                    TargetSlot       = visualPos,
                    SourceDevicePath = card.DevicePath,
                });
            }
            await _forwardingService.StartForwardingAsync(assignments);
            NavLog("[Forwarding] Started — per-device HID forwarding active");
            if (RevertButton is not null)
                RevertButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            NavLog($"[Forwarding] Start failed: {ex.Message}");
        }
    }

    private async void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _forwardingService.StopForwardingAsync();
            _slotOrderStore.Clear();
            NavLog("[Forwarding] Stopped — reverted to physical order, cleared saved order");
            if (RevertButton is not null)
                RevertButton.Visibility = Visibility.Collapsed;
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            NavLog($"[Forwarding] Revert failed: {ex.Message}");
        }
    }

    private void OnForwardingError(object? sender, ForwardingErrorEventArgs e)
    {
        NavLog($"[Forwarding] Error on slot {e.TargetSlot} ({e.DevicePath}): {e.ErrorMessage}");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_forwardingService.IsForwarding && RevertButton is not null)
                RevertButton.Visibility = Visibility.Collapsed;
        });
    }

    // ── Order persistence ────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current visual card order (VID:PID strings) and ViGEm slotMap
    /// to %APPDATA%\ControlShift\slot-order.json.
    /// </summary>
    private void SaveCurrentOrder()
    {
        var vidPids = _cards
            .Select(c => c.VidPid)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToArray();

        if (vidPids.Length == 0) return;

        var slotMap = BuildSlotMap();
        _slotOrderStore.Save(vidPids, slotMap);
        NavLog($"[SaveOrder] Saved {vidPids.Length} entries: [{string.Join(", ", vidPids)}]");
    }

    /// <summary>
    /// Reorders _cards and SlotPanel.Children to match the saved preferred order.
    /// Controllers in the saved order come first (in saved order).
    /// New controllers not in saved order append at the bottom.
    /// Missing controllers (disconnected) are skipped.
    /// </summary>
    private void ApplySavedOrder()
    {
        var saved = _slotOrderStore.Load();
        if (saved is null) return;

        // Build lookup: VID:PID → desired visual position
        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < saved.Order.Length; i++)
        {
            if (!orderMap.ContainsKey(saved.Order[i]))
                orderMap[saved.Order[i]] = i;
        }

        // Sort: cards with known VidPid in saved order → unknowns in their current order
        var sorted = _cards
            .Select((card, idx) => (card, idx))
            .OrderBy(t => orderMap.TryGetValue(t.card.VidPid, out int pos)
                          && !string.IsNullOrEmpty(t.card.VidPid) ? pos : int.MaxValue)
            .ThenBy(t => t.idx)
            .Select(t => t.card)
            .ToList();

        // Only rearrange if order actually changed
        if (sorted.SequenceEqual(_cards)) return;

        NavLog($"[ApplySavedOrder] Reordering cards to match saved order");

        _suppressFocusEvents = true;
        try
        {
            SlotPanel.Children.Clear();
            _cards.Clear();
            _cards.AddRange(sorted);
            foreach (var card in _cards)
                SlotPanel.Children.Add(card);

            for (int i = 0; i < _cards.Count; i++)
                _cards[i].TabIndex = i;
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Start forwarding with the restored visual order
        _ = ApplyForwardingAsync();
    }

    /// <summary>
    /// Builds a slotMap[physicalSlot] = virtualSlot from the current card visual order.
    /// </summary>
    private int[] BuildSlotMap()
    {
        var slotMap = new int[] { 0, 1, 2, 3 };
        for (int visualPos = 0; visualPos < _cards.Count; visualPos++)
        {
            int physicalSlot = _cards[visualPos].SlotIndex;
            if (physicalSlot >= 0 && physicalSlot < 4)
                slotMap[physicalSlot] = visualPos;
        }
        return slotMap;
    }

    // ── Navigation: XInput polling ────────────────────────────────────────────

    private void NavTimer_Tick(object? sender, object e)
    {
        NavLog($"NAV#{_navTickCount} windowActive={_windowActive} " +
               $"focused={_focusedElement?.GetType().Name ?? "null"} reorderIdx={_reorderingIndex} " +
               $"holdTick={_holdTickCount} aHoldStart={_aHoldStart.HasValue} " +
               $"aEnteredReorder={_aHoldEnteredReorder} suppress={_suppressFocusEvents} " +
               $"cardCount={_cards.Count}");
        _navTickCount++;

        try
        {
            if (!_windowActive) return;

            // ── Phase 1: read XInput state (pure P/Invoke, no UI side-effects) ──────

            GamepadButtons current   = default;
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

            GamepadButtons newPresses  = current            & ~_prevGamepadButtons;
            GamepadButtons newReleases = _prevGamepadButtons & ~current;
            _prevGamepadButtons = current;

            bool stickUp   = maxThumbY >  StickDeadzoneThreshold && _prevLeftThumbY <=  StickDeadzoneThreshold;
            bool stickDown = maxThumbY < -StickDeadzoneThreshold && _prevLeftThumbY >= -StickDeadzoneThreshold;
            _prevLeftThumbY = maxThumbY;

            bool navUp        = (newPresses  & GamepadButtons.DPadUp)   != 0 || stickUp;
            bool navDown      = (newPresses  & GamepadButtons.DPadDown)  != 0 || stickDown;
            bool pressB       = (newPresses  & GamepadButtons.B)         != 0;
            bool aJustPressed = (newPresses  & GamepadButtons.A)         != 0;
            bool aJustReleased= (newReleases & GamepadButtons.A)         != 0;

            // ── Phase 2: UI mutations ─────────────────────────────────────────────────

            if (_reorderingIndex >= 0)
            {
                // ── Reorder mode: d-pad/stick moves card; A confirms; B cancels ──────
                if (navUp)        { NavLog("[NavTick] → MoveReorderingCard(-1)"); MoveReorderingCard(-1); }
                if (navDown)      { NavLog("[NavTick] → MoveReorderingCard(+1)"); MoveReorderingCard(+1); }
                if (aJustPressed) { NavLog("[NavTick] → ConfirmReorder");         ConfirmReorder(); }
                if (pressB)       { NavLog("[NavTick] → CancelReorder");          CancelReorder(); }
            }
            else
            {
                // ── Normal mode: d-pad/stick moves focus through flat element list ───
                var elements = GetFocusableElementsInOrder();
                int curIdx = _focusedElement is not null ? elements.IndexOf(_focusedElement) : -1;

                if (navUp && curIdx > 0)
                {
                    NavLog($"[NavTick] → Focus element {curIdx - 1} (up)");
                    elements[curIdx - 1].Focus(FocusState.Programmatic);
                }
                if (navDown && curIdx < elements.Count - 1)
                {
                    int nextIdx = curIdx < 0 ? 0 : curIdx + 1;
                    NavLog($"[NavTick] → Focus element {nextIdx} (down)");
                    elements[nextIdx].Focus(FocusState.Programmatic);
                }

                // ── A button on cards: tap = rumble, hold = reorder ──────────────────
                // On buttons: tap = activate
                int cardIdx = FocusedCardIndex;

                if (aJustPressed && _focusedElement is not null)
                {
                    if (cardIdx >= 0)
                    {
                        _holdTimer.Stop();
                        _cards[cardIdx].StopHoldRumble();
                        _aHoldStart          = DateTime.UtcNow;
                        _aHoldEnteredReorder = false;
                        _holdTimer.Start();
                        NavLog($"[NavTick] A pressed on card {cardIdx}");
                    }
                    else if (_focusedElement is Button btn)
                    {
                        // A on a button = activate it
                        NavLog($"[NavTick] A pressed on button '{btn.Content}'");
                        ActivateButton(btn);
                    }
                }

                if (aJustReleased)
                {
                    _holdTimer.Stop();
                    _holdTickCount = 0;
                    if (_aHoldStart.HasValue && !_aHoldEnteredReorder && cardIdx >= 0)
                    {
                        NavLog("[NavTick] → TriggerRumble (tap <500ms)");
                        _cards[cardIdx].StopHoldRumble();
                        _cards[cardIdx].HideHoldProgress();
                        _cards[cardIdx].TriggerRumble();
                    }
                    _aHoldStart          = null;
                    _aHoldEnteredReorder = false;
                }
            }
        }
        catch (Exception ex)
        {
            NavLog($"[NavTimer_Tick ERROR] {ex}");
        }
    }

    /// <summary>Programmatically activates a footer button (simulates click).</summary>
    private void ActivateButton(Button btn)
    {
        if (btn == RevertButton) RevertButton_Click(btn, new RoutedEventArgs());
        else if (btn == ExitButton) ExitButton_Click(btn, new RoutedEventArgs());
    }

    private void WatchdogTimer_Tick(object? sender, object e)
    {
        NavLog($"[Watchdog] navTimer.IsEnabled={_navTimer.IsEnabled} " +
               $"windowActive={_windowActive} ticksSinceLast={_navTickCount} " +
               $"navTimer=0x{_navTimer.GetHashCode():X}");

        // Restart if disabled OR if it's enabled but stalled (no ticks in the last 5s).
        bool stalled = _navTimer.IsEnabled && _navTickCount == 0;
        if (!_navTimer.IsEnabled || stalled)
        {
            NavLog($"[Watchdog] *** nav timer {(stalled ? "stalled" : "stopped")} — restarting ***");
            _navTimer.Stop();
            _navTimer.Start();
        }

        _navTickCount = 0;
    }

    /// <summary>
    /// Owns the hold-A progress bar animation.
    /// Fires every 16ms while A is held, started by NavTimer_Tick on A press.
    /// Suppresses the bar for the first 200ms (tap feels instant), then fills it
    /// over [200ms, 500ms]. Calls StartReorder() at the 500ms threshold and stops itself.
    /// </summary>
    private void HoldTimer_Tick(object? sender, object e)
    {
        try
        {
            _holdTickCount++;
            var card = FocusedCard;
            int cardIdx = card is not null ? _cards.IndexOf(card) : -1;

            if (_aHoldStart is null || card is null || cardIdx < 0)
            {
                card?.StopHoldRumble();
                _holdTimer.Stop();
                _holdTickCount = 0;
                return;
            }

            double elapsedMs = (DateTime.UtcNow - _aHoldStart.Value).TotalMilliseconds;

            var rumbleIntensity = (ushort)(RumbleHoldMin + RumbleHoldRange * Math.Clamp(elapsedMs / HoldReorderThresholdMs, 0.0, 1.0));
            card.SetHoldRumble(rumbleIntensity);

            if (elapsedMs >= HoldProgressDelayMs)
                card.ShowHoldProgress((elapsedMs - HoldProgressDelayMs) / HoldProgressRangeMs);

            if (elapsedMs >= HoldReorderThresholdMs && !_aHoldEnteredReorder)
            {
                _aHoldEnteredReorder = true;
                _holdTimer.Stop();
                card.HideHoldProgress();
                card.StopHoldRumble();
                NavLog("[HoldTimer] → StartReorder (hold ≥500ms)");
                StartReorder();
            }
        }
        catch (Exception ex)
        {
            NavLog($"[HoldTimer_Tick ERROR] {ex}");
            _holdTimer.Stop();
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
                    NavLog("[KeyDown] ConfirmReorder (keyboard)");
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
                    if (FocusedCardIndex >= 0)
                    {
                        NavLog($"[KeyDown] StartReorder (keyboard) on card {FocusedCardIndex}");
                        StartReorder();
                        e.Handled = true;
                    }
                    break;

                case VirtualKey.Tab:
                {
                    var elements = GetFocusableElementsInOrder();
                    int curIdx = _focusedElement is not null ? elements.IndexOf(_focusedElement) : -1;
                    var shiftState = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(VirtualKey.Shift);
                    bool shiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                    int next = shiftDown ? curIdx - 1 : curIdx + 1;
                    if (next >= 0 && next < elements.Count)
                    {
                        NavLog($"[KeyDown] Tab({(shiftDown ? "up" : "down")}) → element {next}");
                        elements[next].Focus(FocusState.Programmatic);
                        e.Handled = true;
                    }
                    break;
                }
            }
        }
    }

    // ── Navigation: focus tracking ────────────────────────────────────────────

    private void OnElementGotFocus(UIElement element)
    {
        if (_suppressFocusEvents) return;

        // If focus moved mid-hold, cancel the pending hold on the old card.
        if (_aHoldStart.HasValue && FocusedCard is { } oldCard && !ReferenceEquals(oldCard, element))
        {
            _holdTimer.Stop();
            oldCard.HideHoldProgress();
            oldCard.StopHoldRumble();
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
        }
        _focusedElement = element;
        UpdateCardStates();
        UpdateButtonFocusVisuals();
    }

    private void OnElementLostFocus(UIElement element)
    {
        if (_suppressFocusEvents) return;

        if (ReferenceEquals(_focusedElement, element) && _reorderingIndex < 0)
        {
            _focusedElement = null;
            UpdateCardStates();
            UpdateButtonFocusVisuals();
        }
    }

    // ── Navigation: reorder state machine ────────────────────────────────────

    private void StartReorder()
    {
        int cardIdx = FocusedCardIndex;
        if (cardIdx < 0) return;
        NavLog($"[StartReorder] card {cardIdx}");
        _reorderingIndex = cardIdx;
        _savedOrder.Clear();
        _savedOrder.AddRange(_cards);
        UpdateCardStates();
    }

    private void ConfirmReorder()
    {
        if (_reorderingIndex < 0) return;
        int confirmIdx = _reorderingIndex;
        var confirmCard = _cards[confirmIdx];
        NavLog($"[ConfirmReorder] confirmIdx={confirmIdx}");

        _suppressFocusEvents = true;
        try
        {
            _holdTimer.Stop();
            _holdTickCount       = 0;
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
            _focusedElement      = confirmCard;
            _reorderingIndex     = -1;

            UpdateCardStates();
            confirmCard.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Sync drift check
        if (!ReferenceEquals(_focusedElement, confirmCard))
        {
            NavLog("[ConfirmReorder] sync drift — correcting");
            _focusedElement = confirmCard;
            UpdateCardStates();
        }

        // Deferred guard
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_reorderingIndex < 0 && !ReferenceEquals(_focusedElement, confirmCard))
            {
                NavLog("[ConfirmReorder deferred guard] — correcting");
                _focusedElement = confirmCard;
                UpdateCardStates();
            }
        });

        NavLog($"[ConfirmReorder] done — reorderIdx={_reorderingIndex}");

        _ = ApplyForwardingAsync();
        SaveCurrentOrder();
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[CancelReorder] reorderIdx={_reorderingIndex}");

        var focusCard = _cards[_reorderingIndex];

        _suppressFocusEvents = true;
        try
        {
            SlotPanel.Children.Clear();
            _cards.Clear();
            _cards.AddRange(_savedOrder);
            foreach (var card in _cards)
                SlotPanel.Children.Add(card);

            RebuildCardsFromPanel();
            for (int i = 0; i < _cards.Count; i++)
                _cards[i].TabIndex = i;

            _holdTimer.Stop();
            _holdTickCount       = 0;
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
            _focusedElement      = focusCard;
            _reorderingIndex     = -1;

            UpdateCardStates();
            focusCard.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Sync drift check
        if (!ReferenceEquals(_focusedElement, focusCard))
        {
            NavLog("[CancelReorder] sync drift — correcting");
            _focusedElement = focusCard;
            UpdateCardStates();
        }

        // Deferred guard
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_reorderingIndex < 0 && !ReferenceEquals(_focusedElement, focusCard))
            {
                NavLog("[CancelReorder deferred guard] — correcting");
                _focusedElement = focusCard;
                UpdateCardStates();
            }
        });

        NavLog($"[CancelReorder] done — reorderIdx={_reorderingIndex}");
    }

    private void MoveReorderingCard(int direction)
    {
        if (_reorderingIndex < 0) return;
        int newIdx = _reorderingIndex + direction;
        if (newIdx < 0 || newIdx >= _cards.Count) return;

        NavLog($"[MoveReorderingCard] {_reorderingIndex} → {newIdx}");

        var movingCard = _cards[_reorderingIndex];

        _suppressFocusEvents = true;
        try
        {
            SlotPanel.Children.RemoveAt(_reorderingIndex);
            SlotPanel.Children.Insert(newIdx, movingCard);

            RebuildCardsFromPanel();

            for (int i = 0; i < _cards.Count; i++)
                _cards[i].TabIndex = i;

            _reorderingIndex = newIdx;
            _focusedElement  = movingCard;
            UpdateCardStates();
            movingCard.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        NavLog($"[MoveReorderingCard] done, reorderIdx now {_reorderingIndex}");
    }

    // ── Navigation: visual state ──────────────────────────────────────────────

    private void UpdateCardStates()
    {
        int focusedIdx = FocusedCardIndex;
        for (int i = 0; i < _cards.Count; i++)
        {
            CardState state;
            if (_reorderingIndex >= 0)
                state = i == _reorderingIndex ? CardState.Selected : CardState.Dimmed;
            else
                state = i == focusedIdx ? CardState.Focused : CardState.Normal;

            _cards[i].SetCardState(state);
        }
    }

    /// <summary>
    /// Applies a white focus ring to footer buttons when they have gamepad/keyboard focus.
    /// </summary>
    private void UpdateButtonFocusVisuals()
    {
        ApplyButtonFocusVisual(RevertButton, ReferenceEquals(_focusedElement, RevertButton));
        ApplyButtonFocusVisual(ExitButton, ReferenceEquals(_focusedElement, ExitButton));
    }

    private static void ApplyButtonFocusVisual(Button btn, bool focused)
    {
        btn.BorderThickness = focused ? new Thickness(2) : new Thickness(0);
        btn.BorderBrush     = focused
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : null;
    }

    // DECISION: XYFocus (WinUI's built-in D-pad navigation via XYFocusUp/Down) is
    // intentionally NOT used. XYFocus links become stale after Children.RemoveAt/Insert
    // during reorder, and any link update races with WinUI's focus engine layout pass.
    // The entire class of XYFocus corruption bugs is eliminated by:
    //   1. XYFocusKeyboardNavigation="Disabled" on SlotPanel (XAML)
    //   2. All D-pad focus movement via explicit _cards[n].Focus(Programmatic)
    //      in NavTimer_Tick (gamepad) and ContentGrid_KeyDown (keyboard / Tab)
    //   3. Tab order driven purely by TabIndex, rebuilt after every reorder

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

    // ── XInput diagnostic dump ────────────────────────────────────────────────
    // Raw P/Invoke to XInputGetState and XInputGetBatteryInformation — bypasses
    // Vortice.XInput so we can log the actual Win32 return codes and struct values
    // without any wrapping. Does not affect any existing logic.

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte   bLeftTrigger;
        public byte   bRightTrigger;
        public short  sThumbLX;
        public short  sThumbLY;
        public short  sThumbRX;
        public short  sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint          dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint RawXInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint RawXInputGetBatteryInformation(
        uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    private static void WriteXInputDiagnostic()
    {
        const uint ERROR_SUCCESS                 = 0;
        const uint ERROR_DEVICE_NOT_CONNECTED    = 1167;
        const byte BATTERY_DEVTYPE_GAMEPAD       = 0;

        try
        {
            var dumpPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "controlshift-xinput-dump.txt");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ControlShift XInput dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine();

            for (uint slot = 0; slot < 4; slot++)
            {
                uint rc = RawXInputGetState(slot, out XINPUT_STATE state);

                string rcLabel = rc == ERROR_SUCCESS              ? "ERROR_SUCCESS (connected)"
                               : rc == ERROR_DEVICE_NOT_CONNECTED ? "ERROR_DEVICE_NOT_CONNECTED"
                               : $"0x{rc:X8}";

                sb.AppendLine($"Slot {slot}: rc={rc} — {rcLabel}");

                if (rc == ERROR_SUCCESS)
                {
                    sb.AppendLine($"  packetNumber = {state.dwPacketNumber}");

                    uint brc = RawXInputGetBatteryInformation(
                        slot, BATTERY_DEVTYPE_GAMEPAD, out XINPUT_BATTERY_INFORMATION batt);

                    string battTypeLabel = batt.BatteryType switch
                    {
                        0   => "DISCONNECTED",
                        1   => "WIRED",
                        2   => "ALKALINE",
                        3   => "NIMH",
                        255 => "UNKNOWN",
                        _   => $"0x{batt.BatteryType:X2}",
                    };
                    string battLevelLabel = batt.BatteryLevel switch
                    {
                        0 => "EMPTY",
                        1 => "LOW",
                        2 => "MEDIUM",
                        3 => "FULL",
                        _ => $"0x{batt.BatteryLevel:X2}",
                    };

                    sb.AppendLine($"  battery:      rc={brc}  type={batt.BatteryType} ({battTypeLabel})  level={batt.BatteryLevel} ({battLevelLabel})");
                }

                sb.AppendLine();
            }

            System.IO.File.WriteAllText(dumpPath, sb.ToString());
        }
        catch { /* diagnostic writes must never throw */ }
    }
}
