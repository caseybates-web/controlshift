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

        var busDetector = new PnpBusDetector();
        var matcher     = new ControllerMatcher(vendorDb, fingerprinter, busDetector);
        _viewModel      = new MainViewModel(new XInputEnumerator(), new HidEnumerator(), matcher);

        SetWindowSize(400, 700);

        // Build 4 fixed slot cards — one per XInput slot P1–P4.
        // Margin="8,4,8,4": 8px horizontal gives the 1.03 swell room within the
        // ScrollContentPresenter clip; 4px vertical keeps the 8px card-to-card gap
        // (4 bottom + 4 top) while guarding the first/last card's swell at the
        // top/bottom viewport edge. Preserved intact through all reorder moves.
        for (int i = 0; i < 4; i++)
        {
            _cards[i] = new SlotCard { Margin = new Thickness(8, 4, 8, 4) };
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

        // ONE hold timer — created here, reused for every A press, never recreated.
        // Fires at 16ms while A is held; drives the hold-progress bar animation.
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _holdTimer.Tick += HoldTimer_Tick;

        // Watchdog: every 5s verify the nav timer is still running and restart if not.
        // Guards against any scenario where the timer silently stops (driver errors,
        // unhandled exceptions escaping the catch, or unexpected Stop() calls).
        _watchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
        _watchdogTimer.Start();

        // Gate XInput polling on window focus so we don't steal input from other apps.
        Activated += (_, args) =>
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;

        Closed += (_, _) => { _pollTimer.Stop(); _navTimer.Stop(); _watchdogTimer.Stop(); _holdTimer.Stop(); };

        // XInput diagnostic dump — written once on startup before any abstraction.
        WriteXInputDiagnostic();

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
        // ALL state flags logged at the very top of every tick — before any early-return.
        // This line goes to both Debug Output and the log file so post-mortem analysis
        // can identify exactly which flag is stuck after a reorder cancel/confirm.
        NavLog($"NAV#{_navTickCount} windowActive={_windowActive} " +
               $"focusIdx={_focusedCardIndex} reorderIdx={_reorderingIndex} " +
               $"holdTick={_holdTickCount} aHoldStart={_aHoldStart.HasValue} " +
               $"aEnteredReorder={_aHoldEnteredReorder} suppress={_suppressFocusEvents} " +
               $"savedOrderSet={_savedOrder.Any(c => c is not null)}");
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

            // Edge-detect new presses and new releases vs previous frame.
            GamepadButtons newPresses  = current            & ~_prevGamepadButtons;
            GamepadButtons newReleases = _prevGamepadButtons & ~current;
            _prevGamepadButtons = current;

            // Thumbstick threshold crossing (~25% of range).
            const short Threshold = 8192;
            bool stickUp   = maxThumbY >  Threshold && _prevLeftThumbY <=  Threshold;
            bool stickDown = maxThumbY < -Threshold && _prevLeftThumbY >= -Threshold;
            _prevLeftThumbY = maxThumbY;

            bool navUp        = (newPresses  & GamepadButtons.DPadUp)   != 0 || stickUp;
            bool navDown      = (newPresses  & GamepadButtons.DPadDown)  != 0 || stickDown;
            bool pressB       = (newPresses  & GamepadButtons.B)         != 0;
            bool aJustPressed = (newPresses  & GamepadButtons.A)         != 0;
            bool aHeld        = (current     & GamepadButtons.A)         != 0;
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
                // ── Normal mode: d-pad/stick moves focus ─────────────────────────────
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

                // ── A button: tap (<500ms) = rumble; hold (≥500ms) = reorder ────────
                // Progress animation is owned by _holdTimer (HoldTimer_Tick).
                // NavTimer_Tick only handles press start and release.

                if (aJustPressed && _focusedCardIndex >= 0)
                {
                    // Cancel any previous hold (e.g. rapid double-press) then start fresh.
                    _holdTimer.Stop();
                    _cards[_focusedCardIndex].StopHoldRumble(); // defensive: clear any stale hold rumble
                    _aHoldStart          = DateTime.UtcNow;
                    _aHoldEnteredReorder = false;
                    _holdTimer.Start();
                    NavLog($"[NavTick] A pressed on card {_focusedCardIndex} — " +
                           $"holdTimer hashCode=0x{_holdTimer.GetHashCode():X}");
                }

                if (aJustReleased)
                {
                    _holdTimer.Stop();
                    _holdTickCount = 0;
                    if (_aHoldStart.HasValue && !_aHoldEnteredReorder && _focusedCardIndex >= 0)
                    {
                        // Tap (released before 500ms) — stop progressive rumble, then
                        // fire the normal 200ms identify rumble.
                        NavLog("[NavTick] → TriggerRumble (tap <500ms)");
                        _cards[_focusedCardIndex].StopHoldRumble();
                        _cards[_focusedCardIndex].HideHoldProgress();
                        _cards[_focusedCardIndex].TriggerRumble();
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

            if (_aHoldStart is null || _focusedCardIndex < 0)
            {
                // Guard: shouldn't happen, but stop cleanly if state is inconsistent.
                if (_focusedCardIndex >= 0)
                    _cards[_focusedCardIndex].StopHoldRumble();
                _holdTimer.Stop();
                _holdTickCount = 0;
                return;
            }

            double elapsedMs = (DateTime.UtcNow - _aHoldStart.Value).TotalMilliseconds;

            // Progressive rumble: linear ramp from 10% (6553) at 0ms to 40% (26214) at 500ms.
            // Formula: intensity = 6553 + 19661 * clamp(t, 0, 1) where t = elapsedMs / 500
            var rumbleIntensity = (ushort)(6553 + 19661 * Math.Clamp(elapsedMs / 500.0, 0.0, 1.0));
            _cards[_focusedCardIndex].SetHoldRumble(rumbleIntensity);

            // Show progress bar only after 200ms delay; map [200ms, 500ms] → [0, 1].
            if (elapsedMs >= 200.0)
                _cards[_focusedCardIndex].ShowHoldProgress((elapsedMs - 200.0) / 300.0);

            if (elapsedMs >= 500.0 && !_aHoldEnteredReorder)
            {
                _aHoldEnteredReorder = true;
                _holdTimer.Stop();
                _cards[_focusedCardIndex].HideHoldProgress();
                _cards[_focusedCardIndex].StopHoldRumble(); // stop progressive rumble before reorder mode
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
                    // Keyboard confirm: immediate. GamepadA is handled via XInput hold detection.
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
                    // Keyboard reorder: immediate. GamepadA uses XInput hold detection (tap=rumble, hold=reorder).
                    if (_focusedCardIndex >= 0)
                    {
                        NavLog($"[KeyDown] StartReorder (keyboard) on card {_focusedCardIndex}");
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
        // Suppressed during CancelReorder / ConfirmReorder — state is managed explicitly there.
        if (_suppressFocusEvents) return;

        // If the user moved focus mid-hold, cancel the pending hold on the old card.
        if (_aHoldStart.HasValue && _focusedCardIndex >= 0 && _focusedCardIndex != idx)
        {
            _holdTimer.Stop();
            _cards[_focusedCardIndex].HideHoldProgress();
            _cards[_focusedCardIndex].StopHoldRumble(); // stop progressive rumble on card losing focus
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
        }
        _focusedCardIndex = idx;
        UpdateCardStates();
    }

    private void OnCardLostFocus(int idx)
    {
        // Suppressed during CancelReorder / ConfirmReorder — state is managed explicitly there.
        if (_suppressFocusEvents) return;

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
        int confirmIdx = _reorderingIndex;
        NavLog($"[ConfirmReorder] confirmIdx={confirmIdx} focusIdx={_focusedCardIndex}");

        // Enqueue XY focus update — deferred so WinUI completes layout after tree mutations
        // from MoveReorderingCard before we set the links.
        EnqueueXYFocusUpdate();

        _suppressFocusEvents = true;
        try
        {
            _holdTimer.Stop();
            _holdTickCount       = 0;
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
            _focusedCardIndex    = confirmIdx;
            _reorderingIndex     = -1;

            UpdateCardStates();
            _cards[_focusedCardIndex].Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Sync drift check — deferred LostFocus from MoveReorderingCard (RemoveAt/Insert)
        // can have fired synchronously during Focus() above.
        if (_focusedCardIndex != confirmIdx)
        {
            NavLog($"[ConfirmReorder] sync drift {_focusedCardIndex}→{confirmIdx} — correcting");
            _focusedCardIndex = confirmIdx;
            UpdateCardStates();
        }

        // Deferred guard — MoveReorderingCard's Children.RemoveAt/Insert can post deferred
        // LostFocus events that fire after this method returns.  Same queue-ordering
        // guarantee as CancelReorder: we enqueue AFTER those events were posted, so
        // we run last and re-assert the correct _focusedCardIndex.
        int guardIdx = confirmIdx;
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_reorderingIndex < 0 && _focusedCardIndex != guardIdx)
            {
                NavLog($"[ConfirmReorder deferred guard] focusIdx={_focusedCardIndex} expected={guardIdx} — correcting");
                _focusedCardIndex = guardIdx;
                UpdateCardStates();
            }
        });

        NavLog($"[ConfirmReorder] done — focusIdx={_focusedCardIndex} reorderIdx={_reorderingIndex}");
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[CancelReorder] reorderIdx={_reorderingIndex} focusIdx={_focusedCardIndex}");

        // Capture the card being moved BY REFERENCE before we restore _cards[] from
        // _savedOrder — the index may point to a different card after the copy.
        var focusCard = _cards[_reorderingIndex];

        // Suppress BEFORE any tree modification.
        // Children.Clear() posts deferred LostFocus events to the WinUI dispatcher
        // queue; those events fire in a subsequent dispatcher cycle AFTER this method
        // returns and AFTER suppress is cleared by the finally block.  If suppress is
        // not set here, the first tree-removal LostFocus fires unsuppressed and calls
        // OnCardLostFocus with _reorderingIndex already -1, setting _focusedCardIndex
        // to -1 and permanently breaking d-pad navigation.  Setting suppress first
        // makes any synchronous LostFocus (same-tick) a no-op; the DispatcherQueue
        // guard below handles the deferred (next-tick) case.
        _suppressFocusEvents = true;
        try
        {
            ClearXYFocusLinks();
            SlotPanel.Children.Clear();
            Array.Copy(_savedOrder, _cards, _cards.Length);
            foreach (var card in _cards)
                SlotPanel.Children.Add(card);
            EnqueueXYFocusUpdate();

            int restoreIdx = Array.IndexOf(_cards, focusCard);
            if (restoreIdx < 0) restoreIdx = 0;

            _holdTimer.Stop();
            _holdTickCount       = 0;
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
            _focusedCardIndex    = restoreIdx;
            _reorderingIndex     = -1;

            UpdateCardStates();
            _cards[restoreIdx].Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Sync drift check — catches events that fired synchronously within the try block.
        int expectedIdx = Array.IndexOf(_cards, focusCard);
        if (expectedIdx < 0) expectedIdx = 0;
        if (_focusedCardIndex != expectedIdx)
        {
            NavLog($"[CancelReorder] sync drift {_focusedCardIndex}→{expectedIdx} — correcting");
            _focusedCardIndex = expectedIdx;
            UpdateCardStates();
        }

        // Deferred guard — runs AFTER any pending deferred LostFocus events from
        // Children.Clear().  Those events are posted to the dispatcher queue during
        // Clear() and fire in the next dispatcher cycle (after this method and its
        // caller have already returned), where _suppressFocusEvents is already false
        // and _reorderingIndex is already -1, causing OnCardLostFocus to clobber
        // _focusedCardIndex.  Because we enqueue THIS callback AFTER Clear() posted
        // its event, the dispatcher FIFO ordering guarantees we run LAST and can
        // re-assert the correct value.
        int guardIdx = expectedIdx;
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_reorderingIndex < 0 && _focusedCardIndex != guardIdx)
            {
                NavLog($"[CancelReorder deferred guard] focusIdx={_focusedCardIndex} expected={guardIdx} — correcting");
                _focusedCardIndex = guardIdx;
                UpdateCardStates();
            }
        });

        NavLog($"[CancelReorder] done — focusIdx={_focusedCardIndex} reorderIdx={_reorderingIndex}");
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

        // All cards are back in the tree — rebuild TabIndex and enqueue XYFocus update.
        // Deferred so WinUI completes its layout pass before we set the links.
        for (int i = 0; i < _cards.Length; i++)
            _cards[i].TabIndex = i;
        EnqueueXYFocusUpdate();

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
    /// Logs each card's link targets for post-mortem analysis.
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

            // Log link targets after every update — makes XYFocus corruption visible in the log.
            for (int i = 0; i < _cards.Length; i++)
            {
                var upCard   = _cards[i].XYFocusUp   as SlotCard;
                var downCard = _cards[i].XYFocusDown as SlotCard;
                int upIdx    = upCard   is not null ? Array.IndexOf(_cards, upCard)   : -1;
                int downIdx  = downCard is not null ? Array.IndexOf(_cards, downCard) : -1;
                NavLog($"[XYFocus] card[{i}] up={upIdx} down={downIdx}");
            }
        }
        catch (Exception ex)
        {
            NavLog($"[UpdateXYFocusLinks ERROR] {ex}");
        }
    }

    /// <summary>
    /// Posts UpdateXYFocusLinks to the dispatcher queue so it runs AFTER WinUI completes
    /// its layout pass for any pending Children.RemoveAt / Insert / Clear.
    /// Must be used instead of UpdateXYFocusLinks() whenever a Children modification
    /// has just occurred (MoveReorderingCard, CancelReorder, ConfirmReorder).
    /// </summary>
    private void EnqueueXYFocusUpdate()
    {
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            NavLog("[EnqueueXYFocusUpdate] deferred — running UpdateXYFocusLinks");
            UpdateXYFocusLinks();
        });
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
