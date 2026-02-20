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
using ControlShift.Core.Models;

namespace ControlShift.App;

/// <summary>
/// Standalone Xbox-aesthetic window (~480×600 logical px).
/// Shows 4 controller slot cards. Click a card to rumble it.
/// D-pad / Tab navigates cards; A / Enter selects for reordering; B / Escape cancels.
/// Polls for device changes every 5 seconds.
/// Supports controller reordering via ViGEm + HidHide forwarding.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Core ──────────────────────────────────────────────────────────────────

    private readonly MainViewModel   _viewModel;
    private readonly IInputForwardingService _forwarding;
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

    public MainWindow(IInputForwardingService forwarding)
    {
        InitializeComponent();

        NavLog($"MainWindow ctor — log: {NavLogPath}");

        _forwarding = forwarding;
        _forwarding.ForwardingError += OnForwardingError;

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
        _viewModel  = new MainViewModel(new XInputEnumerator(), new HidEnumerator(), matcher, forwarding);

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
        _pollTimer.Tick += async (_, _) =>
        {
            // Don't re-enumerate while forwarding — hidden devices would show as disconnected.
            if (!_viewModel.IsForwarding)
                await RefreshAsync();
        };
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

        Closed += async (_, _) =>
        {
            _pollTimer.Stop();
            _navTimer.Stop();
            _watchdogTimer.Stop();
            _holdTimer.Stop();
            // Ensure all forwarding stops and HidHide rules are cleared on close.
            if (_viewModel.IsForwarding)
                await _viewModel.RevertAsync();
        };

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

        if (_viewModel.IsForwarding)
        {
            int fwdCount = _forwarding.ActiveAssignments.Count(a => a.SourceDevicePath is not null);
            StatusText.Text = $"Forwarding active — {fwdCount} controller{(fwdCount != 1 ? "s" : "")}";
            RevertButton.Visibility = Visibility.Visible;
        }
        else
        {
            int connected = _viewModel.Slots.Count(s => s.IsConnected);
            StatusText.Text = connected > 0
                ? $"{connected} controller{(connected != 1 ? "s" : "")} connected"
                : "No controllers detected";
            RevertButton.Visibility = Visibility.Collapsed;
        }
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await RefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Reverts all forwarding — stops ViGEm controllers, unhides physical devices.
    /// </summary>
    private async void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        RevertButton.IsEnabled = false;
        try
        {
            await _viewModel.RevertAsync();
            UpdateCards();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("Revert Failed", ex.Message);
        }
        finally
        {
            RevertButton.IsEnabled = true;
        }
    }

    // ── Forwarding: apply reorder ───────────────────────────────────────────

    /// <summary>
    /// Builds <see cref="SlotAssignment"/>s from the current visual card order
    /// and starts forwarding. Called after the user confirms a reorder.
    /// </summary>
    public async Task ApplyReorderAsync()
    {
        // Build slot assignments from the visual card order.
        var assignments = new List<SlotAssignment>();
        for (int i = 0; i < _cards.Length; i++)
        {
            if (i >= _viewModel.Slots.Count) break;
            var slot = _viewModel.Slots[i];

            assignments.Add(new SlotAssignment
            {
                TargetSlot       = i,
                SourceDevicePath = slot.IsConnected ? slot.DevicePath : null,
                IsForwarding     = slot.IsConnected && slot.DevicePath is not null,
            });
        }

        // Check if any actual reorder happened (a connected device moved slots).
        bool hasChanges = assignments.Any(a =>
            a.SourceDevicePath is not null &&
            _viewModel.Slots.FirstOrDefault(s => s.DevicePath == a.SourceDevicePath)
                ?.OriginalSlotIndex != a.TargetSlot);

        if (!hasChanges)
        {
            await ShowErrorDialogAsync("No Changes", "The controller order has not changed.");
            return;
        }

        // Confirm with the user.
        bool confirmed = await ShowConfirmDialogAsync(assignments);
        if (!confirmed) return;

        try
        {
            await _viewModel.ApplyReorderAsync(assignments);
            UpdateCards();
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("Reorder Failed", ex.Message);
        }
    }

    // ── Dialogs ─────────────────────────────────────────────────────────────

    private async Task<bool> ShowConfirmDialogAsync(IReadOnlyList<SlotAssignment> assignments)
    {
        var lines = new List<string>();
        for (int i = 0; i < assignments.Count; i++)
        {
            var a = assignments[i];
            if (a.SourceDevicePath is null)
            {
                lines.Add($"P{i + 1}: Empty");
                continue;
            }

            var slot = _viewModel.Slots.FirstOrDefault(s => s.DevicePath == a.SourceDevicePath);
            string name = slot?.DeviceName ?? "Controller";
            string conn = slot?.ConnectionLabel ?? "";
            string from = slot is not null ? $" — was P{slot.OriginalSlotIndex + 1}" : "";
            lines.Add($"P{i + 1}: {name} ({conn}){from}");
        }

        var content = string.Join("\n", lines) +
            "\n\nPhysical controllers will be hidden from games.\nControlShift must stay running while forwarding.";

        var dialog = new ContentDialog
        {
            Title = "Apply Controller Reorder?",
            Content = content,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task ShowErrorDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    // ── Forwarding error handler ────────────────────────────────────────────

    private void OnForwardingError(object? sender, ForwardingErrorEventArgs e)
    {
        // Marshal to UI thread.
        DispatcherQueue.TryEnqueue(async () =>
        {
            StatusText.Text = $"Controller disconnected from P{e.TargetSlot + 1}";

            // If all forwarding has stopped, auto-revert.
            if (!_forwarding.IsForwarding)
            {
                await _viewModel.RevertAsync();
                UpdateCards();
            }
        });
    }

    // ── Navigation: XInput polling ────────────────────────────────────────────

    private void NavTimer_Tick(object? sender, object e)
    {
        // Confirm the timer is still alive — visible in Debug Output and log file.
        Debug.WriteLine($"NAV TICK #{_navTickCount} navTimer=0x{_navTimer.GetHashCode():X} " +
                        $"| windowActive={_windowActive} focusIdx={_focusedCardIndex} " +
                        $"reorderIdx={_reorderingIndex} aHoldStart={_aHoldStart.HasValue} " +
                        $"aEnteredReorder={_aHoldEnteredReorder} suppress={_suppressFocusEvents}");
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
                    _aHoldStart          = DateTime.UtcNow;
                    _aHoldEnteredReorder = false;
                    _holdTimer.Start();
                    NavLog($"[NavTick] A pressed on card {_focusedCardIndex} — " +
                           $"holdTimer hashCode=0x{_holdTimer.GetHashCode():X}");
                }

                if (aJustReleased)
                {
                    _holdTimer.Stop();
                    if (_aHoldStart.HasValue && !_aHoldEnteredReorder && _focusedCardIndex >= 0)
                    {
                        // Tap (released before 500ms) — rumble to identify, no reorder.
                        NavLog("[NavTick] → TriggerRumble (tap <500ms)");
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
            if (_aHoldStart is null || _focusedCardIndex < 0)
            {
                // Guard: shouldn't happen, but stop cleanly if state is inconsistent.
                _holdTimer.Stop();
                return;
            }

            double elapsedMs = (DateTime.UtcNow - _aHoldStart.Value).TotalMilliseconds;

            // Show progress only after 200ms delay; map [200ms, 500ms] → [0, 1].
            if (elapsedMs >= 200.0)
                _cards[_focusedCardIndex].ShowHoldProgress((elapsedMs - 200.0) / 300.0);

            if (elapsedMs >= 500.0 && !_aHoldEnteredReorder)
            {
                _aHoldEnteredReorder = true;
                _holdTimer.Stop();
                _cards[_focusedCardIndex].HideHoldProgress();
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
        NavLog($"[ConfirmReorder] card now at idx {confirmIdx}");

        // Rebuild XY focus links for the new card order before releasing focus suppression.
        // MoveReorderingCard maintains them per-move, but an explicit rebuild here is cheap
        // and ensures correctness even if the user confirmed without moving.
        UpdateXYFocusLinks();

        _suppressFocusEvents = true;
        try
        {
            _holdTimer.Stop();
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

        // Re-assert after suppress ends: deferred LostFocus events queued by earlier
        // Children.RemoveAt calls inside MoveReorderingCard can fire here and set
        // _focusedCardIndex = -1, permanently killing d-pad/thumbstick navigation.
        if (_focusedCardIndex != confirmIdx)
        {
            NavLog($"[ConfirmReorder] *** focus drift {_focusedCardIndex}→{confirmIdx} (deferred LostFocus) — correcting");
            _focusedCardIndex = confirmIdx;
            UpdateCardStates();
        }

        NavLog($"[ConfirmReorder] done — focusIdx={_focusedCardIndex}");
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[CancelReorder] reorderIdx={_reorderingIndex}");

        // Capture the card being moved BY REFERENCE before we restore _cards[] from
        // _savedOrder — the index may point to a different card after the copy.
        var focusCard = _cards[_reorderingIndex];

        // Clear XYFocus links before removing any element from the visual tree.
        ClearXYFocusLinks();

        SlotPanel.Children.Clear();
        Array.Copy(_savedOrder, _cards, _cards.Length);
        foreach (var card in _cards)
            SlotPanel.Children.Add(card);

        UpdateXYFocusLinks();

        // Find where the moved card landed after the order is restored.
        int restoreIdx = Array.IndexOf(_cards, focusCard);
        if (restoreIdx < 0) restoreIdx = 0;

        // Suppress focus events for the duration of this operation.
        // The Focus() call below will fire LostFocus then GotFocus events; without
        // suppression, OnCardLostFocus would set _focusedCardIndex = -1 (because
        // _reorderingIndex is already -1 at that point), breaking navigation.
        _suppressFocusEvents = true;
        try
        {
            _holdTimer.Stop();
            _aHoldStart          = null;
            _aHoldEnteredReorder = false;
            _focusedCardIndex    = restoreIdx;
            _reorderingIndex     = -1;

            UpdateCardStates();
            _cards[_focusedCardIndex].Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Same deferred-LostFocus guard as ConfirmReorder.
        if (_focusedCardIndex != restoreIdx)
        {
            NavLog($"[CancelReorder] *** focus drift {_focusedCardIndex}→{restoreIdx} (deferred LostFocus) — correcting");
            _focusedCardIndex = restoreIdx;
            UpdateCardStates();
        }

        NavLog($"[CancelReorder] done — focusIdx={_focusedCardIndex}");
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
