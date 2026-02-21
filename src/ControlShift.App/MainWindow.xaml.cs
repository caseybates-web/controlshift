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
using ControlShift.Core.Diagnostics;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Forwarding;
using ControlShift.Core.Models;

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

    private readonly MainViewModel     _viewModel;
    private readonly DispatcherTimer   _pollTimer;
    private readonly List<SlotCard>    _cards = new();
    private readonly XInputEnumerator  _xinputEnum;
    private readonly HidEnumerator     _hidEnum;
    private static MainWindow?         _instance;  // for static WndProc callback

    // ── Forwarding stack ─────────────────────────────────────────────────────

    private readonly IInputForwardingService _forwardingService;
    private readonly SlotOrderStore  _slotOrderStore = new();
    private readonly NicknameStore   _nicknameStore  = new();

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
    private          short           _prevLeftThumbX;
    private          bool            _prevViewButton;
    private          bool            _windowActive = true;

    // Full-screen / layout mode
    private bool   _isFullScreen;
    private double _layoutScale = 1.0;

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

    private static readonly object _logLock = new();

    private static void NavLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
        lock (_logLock)
        {
            try { System.IO.File.AppendAllText(NavLogPath, line + System.Environment.NewLine); }
            catch { /* log writes must never throw */ }
        }
    }

    // ── Construction ──────────────────────────────────────────────────────────

    public MainWindow(IInputForwardingService forwardingService)
    {
        _forwardingService = forwardingService;
        _forwardingService.ForwardingError += OnForwardingError;

        InitializeComponent();

        NavLog($"MainWindow ctor — log: {NavLogPath}");
        DebugLog.Log("MainWindow created");

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
        _xinputEnum     = new XInputEnumerator();
        _hidEnum        = new HidEnumerator();
        _viewModel      = new MainViewModel(_xinputEnum, _hidEnum, matcher);
        _instance       = this;

        SetWindowSize(WindowWidth, WindowHeight);

        // Cards are created dynamically in UpdateCards() based on how many
        // non-excluded slots the ViewModel returns (could be 1–4).

        // Defer XYFocus links, TabIndex, and focus events to ContentGrid.Loaded.
        // Setting XYFocusUp/Down or TabIndex before the compositor tree is ready
        // (before the first layout pass) triggers STATUS_ASSERTION_FAILURE in WinUI 3.
        ContentGrid.Loaded += OnContentGridLoaded;

        // Poll for XInput state changes every 5 seconds (battery, connection).
        // Only invalidates XInput cache — HID and bus detection are cached until
        // WM_DEVICECHANGE fires (avoids CfgMgr32 tree walks that trigger chimes).
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DevicePollSeconds) };
        _pollTimer.Tick += async (_, _) =>
        {
            _xinputEnum.InvalidateCache();
            await RefreshAsync();
        };
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

        // Hook WM_DEVICECHANGE to trigger full refresh when devices are added/removed.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        HookWmDeviceChange(hwnd);

        // Initial scan on window open.
        _ = RefreshAsync();
        RefreshWindowsSees();
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
        RevertButton.GotFocus       += (_, _) => OnElementGotFocus(RevertButton);
        RevertButton.LostFocus      += (_, _) => OnElementLostFocus(RevertButton);
        ExitButton.GotFocus         += (_, _) => OnElementGotFocus(ExitButton);
        ExitButton.LostFocus        += (_, _) => OnElementLostFocus(ExitButton);

        FullScreenToggle.GotFocus   += (_, _) => OnElementGotFocus(FullScreenToggle);
        FullScreenToggle.LostFocus  += (_, _) => OnElementLostFocus(FullScreenToggle);

        ConfigureGridForLayout();

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
            ApplyNicknames();
            return;
        }

        // Log controller connect/disconnect by comparing old vs new slots.
        var oldSlots = _cards.Select(c => c.SlotIndex).ToHashSet();
        var newSlots = _viewModel.Slots.Select(s => s.SlotIndex).ToHashSet();
        foreach (var slot in newSlots.Except(oldSlots))
        {
            var vm = _viewModel.Slots.FirstOrDefault(s => s.SlotIndex == slot);
            if (vm is not null)
                DebugLog.ControllerChange("connect", slot, vm.VidPid ?? "", vm.ConnectionLabel ?? "");
        }
        foreach (var slot in oldSlots.Except(newSlots))
            DebugLog.ControllerChange("disconnect", slot, "", "");

        EnsureCards(_viewModel.Slots.Count);

        for (int i = 0; i < _cards.Count && i < _viewModel.Slots.Count; i++)
            _cards[i].SetSlot(_viewModel.Slots[i]);

        ApplyNicknames();

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
        var margin = _isFullScreen ? new Thickness(12, 8, 12, 8) : new Thickness(8, 4, 8, 4);
        while (_cards.Count < count)
        {
            var card = new SlotCard { Margin = margin };
            card.GotFocus        += (_, _) => OnElementGotFocus(card);
            card.LostFocus       += (_, _) => OnElementLostFocus(card);
            card.NicknameChanged += OnCardNicknameChanged;
            card.SetLayoutScale(_layoutScale);
            _cards.Add(card);
            SlotPanel.Children.Add(card);
        }

        UpdateGridPositions();
        SyncTabIndices();
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

    /// <summary>Ensures each card's TabIndex matches its position in _cards.</summary>
    private void SyncTabIndices()
    {
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].TabIndex = i;
    }

    // ── Nicknames ───────────────────────────────────────────────────────────

    /// <summary>Applies saved nicknames to all slot view models after a refresh.</summary>
    private void ApplyNicknames()
    {
        for (int i = 0; i < _cards.Count && i < _viewModel.Slots.Count; i++)
        {
            var slot = _viewModel.Slots[i];
            if (!string.IsNullOrEmpty(slot.VidPid))
                slot.Nickname = _nicknameStore.GetNickname(slot.VidPid) ?? string.Empty;
        }
    }

    private void OnCardNicknameChanged(object? sender, string newNickname)
    {
        if (sender is not SlotCard card) return;
        var vidPid = card.VidPid;
        if (string.IsNullOrEmpty(vidPid)) return;

        _nicknameStore.SetNickname(vidPid, newNickname);

        // Update the view model so the card display refreshes immediately.
        var slot = _viewModel.Slots.FirstOrDefault(s => s.VidPid == vidPid);
        if (slot is not null)
            slot.Nickname = newNickname;

        NavLog($"[Nickname] Set '{vidPid}' → '{newNickname}'");
    }

    /// <summary>
    /// Replaces SlotPanel.Children and _cards with the given card list,
    /// then syncs TabIndex. Used by LoadProfile, ApplySavedOrder, CancelReorder.
    /// </summary>
    private void ReplaceCardOrder(IReadOnlyList<SlotCard> newOrder)
    {
        SlotPanel.Children.Clear();
        _cards.Clear();
        _cards.AddRange(newOrder);
        foreach (var card in _cards)
            SlotPanel.Children.Add(card);
        UpdateGridPositions();
        SyncTabIndices();
    }

    // ── Grid layout management ─────────────────────────────────────────────

    /// <summary>
    /// Configures SlotPanel's RowDefinitions and ColumnDefinitions for the current layout mode.
    /// Handheld: N Auto rows x 1 Star column (vertical stack). Full-screen: 1 Star row x N Star columns (horizontal).
    /// </summary>
    private void ConfigureGridForLayout()
    {
        SlotPanel.RowDefinitions.Clear();
        SlotPanel.ColumnDefinitions.Clear();

        int count = _cards.Count;
        if (count == 0) count = 4; // pre-allocate for expected 4 slots

        if (_isFullScreen)
        {
            SlotPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < count; i++)
                SlotPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            for (int i = 0; i < count; i++)
                SlotPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SlotPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        UpdateGridPositions();
    }

    /// <summary>
    /// Sets Grid.Row/Grid.Column on each card based on the current layout mode.
    /// Handheld: card[i] at row=i, col=0. Full-screen: card[i] at row=0, col=i.
    /// </summary>
    private void UpdateGridPositions()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            if (_isFullScreen)
            {
                Grid.SetRow(_cards[i], 0);
                Grid.SetColumn(_cards[i], i);
            }
            else
            {
                Grid.SetRow(_cards[i], i);
                Grid.SetColumn(_cards[i], 0);
            }
        }
    }

    /// <summary>
    /// Resets the A-button hold state — called on confirm, cancel, and focus changes.
    /// </summary>
    private void ResetHoldState()
    {
        _holdTimer.Stop();
        _holdTickCount       = 0;
        _aHoldStart          = null;
        _aHoldEnteredReorder = false;
    }

    /// <summary>
    /// Post-reorder focus safety: corrects focus drift immediately and via deferred dispatch.
    /// ConfirmReorder and CancelReorder both need this identical pattern.
    /// </summary>
    private void EnsureFocusConsistency(SlotCard expectedCard, string callerName)
    {
        // Sync drift check
        if (!ReferenceEquals(_focusedElement, expectedCard))
        {
            NavLog($"[{callerName}] sync drift — correcting");
            _focusedElement = expectedCard;
            UpdateCardStates();
        }

        // Deferred guard
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            if (_reorderingIndex < 0 && !ReferenceEquals(_focusedElement, expectedCard))
            {
                NavLog($"[{callerName} deferred guard] — correcting");
                _focusedElement = expectedCard;
                UpdateCardStates();
            }
        });
    }

    // ── Positional navigation helpers ──────────────────────────────────────

    /// <summary>
    /// Returns all visible, focusable elements in top-to-bottom visual order:
    /// FullScreenToggle (header), then SlotCards, then visible footer buttons.
    /// </summary>
    private List<UIElement> GetFocusableElementsInOrder()
    {
        var elements = new List<UIElement>();
        elements.Add(FullScreenToggle);

        foreach (var card in _cards)
            elements.Add(card);

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
        DebugLog.Shutdown("window closed");
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

            // Tell the ViewModel which XInput slots are virtual so they're excluded
            // from UI enumeration (prevents "Unknown Controller USB" ghost cards).
            _viewModel.ExcludedSlotIndices = _forwardingService.VirtualSlotIndices;
            NavLog($"[Forwarding] Started — virtual slots: [{string.Join(", ", _forwardingService.VirtualSlotIndices)}]");
            DebugLog.Log($"[Forwarding] Started — {assignments.Count} assignments, virtual slots: [{string.Join(", ", _forwardingService.VirtualSlotIndices)}]");
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
            await _forwardingService.RevertAllAsync();
            _viewModel.ExcludedSlotIndices = new HashSet<int>();
            _slotOrderStore.Clear();
            NavLog("[Forwarding] Reverted — ViGEm disconnected, physical order restored");
            DebugLog.Log("[Forwarding] Reverted — ViGEm disconnected, physical order restored");
            // Invalidate all caches so physical controllers reappear after HidHide clear.
            _xinputEnum.InvalidateAll();
            _hidEnum.InvalidateCache();
            _ = RefreshAsync();
            RefreshWindowsSees();
        }
        catch (Exception ex)
        {
            NavLog($"[Forwarding] Revert failed: {ex.Message}");
        }
    }

    private void OnForwardingError(object? sender, ForwardingErrorEventArgs e)
    {
        NavLog($"[Forwarding] Error on slot {e.TargetSlot} ({e.DevicePath}): {e.ErrorMessage}");
        DebugLog.Log($"[Forwarding] Error on slot {e.TargetSlot} ({e.DevicePath}): {e.ErrorMessage}");
        DispatcherQueue.TryEnqueue(() =>
        {
            // RevertButton stays always visible — no need to toggle.
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
        DebugLog.SlotMapChange(new[] { 0, 1, 2, 3 }, slotMap);
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
            ReplaceCardOrder(sorted);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        // Only start forwarding if not already active.
        // WM_DEVICECHANGE refreshes must NOT restart forwarding — that would
        // cause a ViGEm disconnect/reconnect chime loop.
        if (!_forwardingService.IsForwarding)
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
            short          maxThumbX = 0;

            for (uint i = 0; i < 4; i++)
            {
                // Skip virtual ViGEm slots — reading them would double-count
                // physical input that's being forwarded, causing phantom button presses.
                if (_forwardingService.VirtualSlotIndices.Contains((int)i))
                    continue;

                bool stateOk = XInput.GetState(i, out State state);
                if (stateOk)
                {
                    current |= state.Gamepad.Buttons;
                    if (Math.Abs(state.Gamepad.LeftThumbY) > Math.Abs(maxThumbY))
                        maxThumbY = state.Gamepad.LeftThumbY;
                    if (Math.Abs(state.Gamepad.LeftThumbX) > Math.Abs(maxThumbX))
                        maxThumbX = state.Gamepad.LeftThumbX;
                }
            }

            // Log if Guide button is ever seen — should never happen with virtual slot exclusion.
            if ((current & GamepadButtons.Guide) != 0)
                DebugLog.Log($"[NavTimer] GUIDE BUTTON DETECTED in XInput aggregate! buttons=0x{(ushort)current:X4}");

            GamepadButtons newPresses  = current            & ~_prevGamepadButtons;
            GamepadButtons newReleases = _prevGamepadButtons & ~current;
            _prevGamepadButtons = current;

            bool stickUp   = maxThumbY >  StickDeadzoneThreshold && _prevLeftThumbY <=  StickDeadzoneThreshold;
            bool stickDown = maxThumbY < -StickDeadzoneThreshold && _prevLeftThumbY >= -StickDeadzoneThreshold;
            _prevLeftThumbY = maxThumbY;

            bool stickLeft  = maxThumbX < -StickDeadzoneThreshold && _prevLeftThumbX >= -StickDeadzoneThreshold;
            bool stickRight = maxThumbX >  StickDeadzoneThreshold && _prevLeftThumbX <=  StickDeadzoneThreshold;
            _prevLeftThumbX = maxThumbX;

            bool navUp        = (newPresses  & GamepadButtons.DPadUp)   != 0 || stickUp;
            bool navDown      = (newPresses  & GamepadButtons.DPadDown)  != 0 || stickDown;
            bool navLeft      = (newPresses  & GamepadButtons.DPadLeft)  != 0 || stickLeft;
            bool navRight     = (newPresses  & GamepadButtons.DPadRight) != 0 || stickRight;
            bool pressB       = (newPresses  & GamepadButtons.B)         != 0;
            bool aJustPressed = (newPresses  & GamepadButtons.A)         != 0;
            bool aJustReleased= (newReleases & GamepadButtons.A)         != 0;

            // View button (Back) edge detection → toggle full-screen
            bool viewNow = (current & GamepadButtons.Back) != 0;
            if (viewNow && !_prevViewButton && _reorderingIndex < 0)
                ToggleFullScreen();
            _prevViewButton = viewNow;

            // ── Phase 2: UI mutations ─────────────────────────────────────────────────

            if (_reorderingIndex >= 0)
            {
                // ── Reorder mode: d-pad/stick moves card; A confirms; B cancels ──────
                if (_isFullScreen)
                {
                    // Horizontal layout: left/right moves card
                    if (navLeft)  { NavLog("[NavTick] → MoveReorderingCard(-1) [horiz]"); MoveReorderingCard(-1); }
                    if (navRight) { NavLog("[NavTick] → MoveReorderingCard(+1) [horiz]"); MoveReorderingCard(+1); }
                }
                else
                {
                    // Vertical layout: up/down moves card
                    if (navUp)    { NavLog("[NavTick] → MoveReorderingCard(-1)"); MoveReorderingCard(-1); }
                    if (navDown)  { NavLog("[NavTick] → MoveReorderingCard(+1)"); MoveReorderingCard(+1); }
                }
                if (aJustPressed) { NavLog("[NavTick] → ConfirmReorder");         ConfirmReorder(); }
                if (pressB)       { NavLog("[NavTick] → CancelReorder");          CancelReorder(); }
            }
            else
            {
                // ── Normal mode: d-pad/stick moves focus through flat element list ───
                var elements = GetFocusableElementsInOrder();
                int curIdx = _focusedElement is not null ? elements.IndexOf(_focusedElement) : -1;

                if (_isFullScreen)
                {
                    // Layout zones in elements list:
                    //   [0] = FullScreenToggle (header)
                    //   [1.._cards.Count] = cards (horizontal row)
                    //   [_cards.Count+1..] = footer buttons (RevertAll, Exit)
                    int fsCardIdx = FocusedCardIndex;
                    int footerStart = 1 + _cards.Count;

                    // Left/right between cards
                    if (navLeft && fsCardIdx > 0)
                    {
                        NavLog($"[NavTick] → Focus card {fsCardIdx - 1} (left)");
                        _cards[fsCardIdx - 1].Focus(FocusState.Programmatic);
                    }
                    if (navRight && fsCardIdx >= 0 && fsCardIdx < _cards.Count - 1)
                    {
                        NavLog($"[NavTick] → Focus card {fsCardIdx + 1} (right)");
                        _cards[fsCardIdx + 1].Focus(FocusState.Programmatic);
                    }

                    // Down from header → first card
                    if (navDown && curIdx == 0 && _cards.Count > 0)
                    {
                        NavLog("[NavTick] → Focus card 0 (down from header)");
                        _cards[0].Focus(FocusState.Programmatic);
                    }
                    // Down from card row → first footer button
                    else if (navDown && fsCardIdx >= 0)
                    {
                        if (footerStart < elements.Count)
                        {
                            NavLog("[NavTick] → Focus footer button (down from card)");
                            elements[footerStart].Focus(FocusState.Programmatic);
                        }
                    }
                    // Down within footer buttons
                    else if (navDown && curIdx >= footerStart && curIdx < elements.Count - 1)
                    {
                        NavLog($"[NavTick] → Focus element {curIdx + 1} (down in footer)");
                        elements[curIdx + 1].Focus(FocusState.Programmatic);
                    }

                    // Up from card → header (FullScreenToggle)
                    if (navUp && fsCardIdx >= 0)
                    {
                        NavLog("[NavTick] → Focus FullScreenToggle (up from card)");
                        elements[0].Focus(FocusState.Programmatic);
                    }
                    // Up from footer
                    else if (navUp && curIdx >= footerStart)
                    {
                        if (curIdx == footerStart)
                        {
                            // Up from first footer button → last card
                            NavLog($"[NavTick] → Focus card {_cards.Count - 1} (up to cards)");
                            _cards[_cards.Count - 1].Focus(FocusState.Programmatic);
                        }
                        else
                        {
                            // Up within footer buttons
                            NavLog($"[NavTick] → Focus element {curIdx - 1} (up in footer)");
                            elements[curIdx - 1].Focus(FocusState.Programmatic);
                        }
                    }

                    // Left/right in footer buttons
                    if (navLeft && curIdx >= footerStart && curIdx > footerStart)
                    {
                        NavLog($"[NavTick] → Focus element {curIdx - 1} (left in footer)");
                        elements[curIdx - 1].Focus(FocusState.Programmatic);
                    }
                    if (navRight && curIdx >= footerStart && curIdx < elements.Count - 1)
                    {
                        NavLog($"[NavTick] → Focus element {curIdx + 1} (right in footer)");
                        elements[curIdx + 1].Focus(FocusState.Programmatic);
                    }
                }
                else
                {
                    // Handheld: vertical list — up/down navigates everything linearly
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
            DebugLog.Exception("NavTimer_Tick", ex);
        }
    }

    /// <summary>Programmatically activates a footer button (simulates click).</summary>
    private void ActivateButton(Button btn)
    {
        if (btn == RevertButton) RevertButton_Click(btn, new RoutedEventArgs());
        else if (btn == FullScreenToggle) FullScreenToggle_Click(btn, new RoutedEventArgs());
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
            if (_isFullScreen)
            {
                // Horizontal layout: left/right moves card
                switch (e.Key)
                {
                    case VirtualKey.Left:
                    case VirtualKey.GamepadDPadLeft:
                    case VirtualKey.GamepadLeftThumbstickLeft:
                        NavLog("[KeyDown] MoveReorderingCard(-1) [horiz]");
                        MoveReorderingCard(-1);
                        e.Handled = true;
                        break;
                    case VirtualKey.Right:
                    case VirtualKey.GamepadDPadRight:
                    case VirtualKey.GamepadLeftThumbstickRight:
                        NavLog("[KeyDown] MoveReorderingCard(+1) [horiz]");
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
                // Vertical layout: up/down moves card
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
        }
        else
        {
            switch (e.Key)
            {
                case VirtualKey.F11:
                    NavLog("[KeyDown] F11 → ToggleFullScreen");
                    ToggleFullScreen();
                    e.Handled = true;
                    break;

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
        var cardIdx = element is SlotCard sc ? _cards.IndexOf(sc) : -1;
        DebugLog.FocusChange(element.GetType().Name, cardIdx);
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
            ResetHoldState();
            _focusedElement  = confirmCard;
            _reorderingIndex = -1;

            UpdateCardStates();
            confirmCard.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        EnsureFocusConsistency(confirmCard, "ConfirmReorder");
        NavLog($"[ConfirmReorder] done — reorderIdx={_reorderingIndex}");

        _ = ApplyForwardingAsync();
        SaveCurrentOrder();
        RefreshWindowsSees();
    }

    private void CancelReorder()
    {
        if (_reorderingIndex < 0) return;
        NavLog($"[CancelReorder] reorderIdx={_reorderingIndex}");

        var focusCard = _cards[_reorderingIndex];

        _suppressFocusEvents = true;
        try
        {
            ReplaceCardOrder(_savedOrder);
            RebuildCardsFromPanel();

            ResetHoldState();
            _focusedElement  = focusCard;
            _reorderingIndex = -1;

            UpdateCardStates();
            focusCard.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressFocusEvents = false;
        }

        EnsureFocusConsistency(focusCard, "CancelReorder");
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
            // Swap the two cards in the list
            (_cards[_reorderingIndex], _cards[newIdx]) = (_cards[newIdx], _cards[_reorderingIndex]);

            // Rebuild SlotPanel children from the new _cards order
            SlotPanel.Children.Clear();
            foreach (var c in _cards)
                SlotPanel.Children.Add(c);

            UpdateGridPositions();
            SyncTabIndices();

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
        ApplyButtonFocusVisual(FullScreenToggle, ReferenceEquals(_focusedElement, FullScreenToggle));
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

    // ── Full-screen toggle ──────────────────────────────────────────────────

    /// <summary>
    /// Toggles between handheld (400x700 windowed) and full-screen horizontal layout.
    /// </summary>
    private void ToggleFullScreen()
    {
        // Don't toggle while reordering — it would break the state machine.
        if (_reorderingIndex >= 0) return;

        _isFullScreen = !_isFullScreen;
        _layoutScale  = _isFullScreen ? 1.8 : 1.0;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        if (_isFullScreen)
            appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        else
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Default);
            SetWindowSize(WindowWidth, WindowHeight);
        }

        // Reconfigure Grid layout (vertical ↔ horizontal)
        ConfigureGridForLayout();

        // Update card margins and scale
        var margin = _isFullScreen ? new Thickness(12, 8, 12, 8) : new Thickness(8, 4, 8, 4);
        foreach (var card in _cards)
        {
            card.Margin = margin;
            card.SetLayoutScale(_layoutScale);
        }

        // Scale header/footer elements
        ApplyLayoutScale();

        // ScrollViewer: disabled in full-screen (everything fits), auto in handheld
        CardScrollViewer.VerticalScrollBarVisibility = _isFullScreen
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        // Update toggle button glyph
        FullScreenToggle.Content = _isFullScreen ? "\uE73F" : "\uE740";

        NavLog($"[ToggleFullScreen] isFullScreen={_isFullScreen} layoutScale={_layoutScale}");
    }

    /// <summary>
    /// Scales header title, version text, and footer button sizes proportionally to layoutScale.
    /// </summary>
    private void ApplyLayoutScale()
    {
        double s = _layoutScale;

        // Header
        HeaderTitle.FontSize   = 18 * s;
        HeaderVersion.FontSize = 11 * s;

        // Full-screen toggle button
        FullScreenToggle.FontSize = 14 * s;
        FullScreenToggle.Padding  = new Thickness(6 * s, 4 * s, 6 * s, 4 * s);

        // Footer buttons
        RevertButton.FontSize      = 14 * s;
        RevertButton.Padding       = new Thickness(16 * s, 8 * s, 16 * s, 8 * s);
        ExitButton.FontSize        = 14 * s;
        ExitButton.Padding         = new Thickness(16 * s, 10 * s, 16 * s, 16 * s);
    }

    private void FullScreenToggle_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_CAPABILITIES
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XINPUT_GAMEPAD Gamepad;
        public XINPUT_VIBRATION Vibration;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint RawXInputGetCapabilities(
        uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES pCapabilities);

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

    // ── "What Windows Sees" panel ─────────────────────────────────────────────

    /// <summary>
    /// Updates the "What Windows Sees" panel. When forwarding is active, shows
    /// the card order (what games see via ViGEm virtual controllers). When not
    /// forwarding, shows raw XInput state for each slot.
    /// </summary>
    private void RefreshWindowsSees()
    {
        TextBlock[] rows = { WindowsSeesP1, WindowsSeesP2, WindowsSeesP3, WindowsSeesP4 };
        var greenBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x10));
        var greyBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["XbTextMutedBrush"];

        if (_forwardingService.IsForwarding)
        {
            // Forwarding active: show the card order — this is what games see
            // through the ViGEm virtual controllers.
            for (int i = 0; i < 4; i++)
            {
                if (i < _cards.Count)
                {
                    var slotVm = _viewModel.Slots.FirstOrDefault(
                        s => s.SlotIndex == _cards[i].SlotIndex);
                    string name = slotVm?.DisplayName ?? "Controller";
                    string conn = slotVm?.ConnectionLabel ?? "";
                    string label = !string.IsNullOrEmpty(conn)
                        ? $"{name} ({conn})" : name;
                    rows[i].Text = $"\u2B24  P{i + 1}: {label}";
                    rows[i].Foreground = greenBrush;
                    rows[i].Visibility = Visibility.Visible;
                }
                else
                {
                    rows[i].Visibility = Visibility.Collapsed;
                }
            }
        }
        else
        {
            // Not forwarding: show raw XInput state directly.
            const uint ERROR_SUCCESS = 0;
            for (uint i = 0; i < 4; i++)
            {
                uint rc = RawXInputGetState(i, out _);
                if (rc == ERROR_SUCCESS)
                {
                    var slotVm = _viewModel.Slots.FirstOrDefault(
                        s => s.SlotIndex == (int)i);
                    string name = slotVm?.DisplayName ?? "Gamepad";
                    string conn = slotVm?.ConnectionLabel ?? "";
                    string label = !string.IsNullOrEmpty(conn)
                        ? $"{name} ({conn})" : name;
                    rows[i].Text = $"\u2B24  P{i + 1}: {label}";
                    rows[i].Foreground = greenBrush;
                    rows[i].Visibility = Visibility.Visible;
                }
                else
                {
                    rows[i].Text = $"\u2B24  P{i + 1}: Empty";
                    rows[i].Foreground = greyBrush;
                    rows[i].Visibility = Visibility.Visible;
                }
            }
        }
    }

    // ── WM_DEVICECHANGE hook ────────────────────────────────────────────────
    // Listens for device arrival/removal events and triggers a full cache-
    // invalidated refresh (debounced to coalesce rapid-fire events).

    private const int WM_DEVICECHANGE          = 0x0219;
    private const int DBT_DEVNODES_CHANGED     = 0x0007;
    private const int GWLP_WNDPROC             = -4;
    private const double DeviceChangeDebounceMs = 500;

    // Diagnostic: raw WM_DEVICECHANGE event log for chime loop detection.
    private static readonly string ChimeDumpPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "controlshift-chime-dump.txt");

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate? _wndProc; // prevent GC
    private static IntPtr _oldWndProc;
    private DispatcherTimer? _deviceChangeDebounce;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void HookWmDeviceChange(IntPtr hwnd)
    {
        _wndProc = new WndProcDelegate(DeviceChangeWndProc);
        _oldWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProc));
        NavLog("[WM_DEVICECHANGE] WndProc subclassed");
    }

    private static IntPtr DeviceChangeWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DEVICECHANGE && (int)wParam == DBT_DEVNODES_CHANGED)
        {
            // Log every raw WM_DEVICECHANGE event for chime loop diagnostics.
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] WM_DEVICECHANGE DBT_DEVNODES_CHANGED";
            lock (_logLock)
            {
                try { System.IO.File.AppendAllText(ChimeDumpPath, line + System.Environment.NewLine); }
                catch { /* diagnostic writes must never throw */ }
            }

            _instance?.OnDeviceChangeNotification();
        }

        return CallWindowProcW(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Called on every DBT_DEVNODES_CHANGED. Debounces: waits 500ms for events
    /// to settle, then invalidates all caches and triggers a full refresh.
    /// </summary>
    private void OnDeviceChangeNotification()
    {
        if (_deviceChangeDebounce == null)
        {
            _deviceChangeDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DeviceChangeDebounceMs)
            };
            _deviceChangeDebounce.Tick += async (_, _) =>
            {
                _deviceChangeDebounce!.Stop();
                int countBefore = _cards.Count;
                NavLog("[WM_DEVICECHANGE] Debounced full refresh — invalidating all caches");
                _xinputEnum.InvalidateAll();
                _hidEnum.InvalidateCache();
                // PnpBusDetector cache is permanent (additive) — new device paths
                // automatically get a fresh CfgMgr32 tree walk on cache miss.
                await RefreshAsync();
                int countAfter = _cards.Count;
                DebugLog.DeviceChange(countBefore, countAfter);
                RefreshWindowsSees();
            };
        }

        // Restart the debounce timer on each event to coalesce rapid-fire notifications.
        _deviceChangeDebounce.Stop();
        _deviceChangeDebounce.Start();
    }
}
