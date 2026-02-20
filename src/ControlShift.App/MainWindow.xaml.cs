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
using ControlShift.Core.Profiles;

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
    private readonly IProfileStore   _profileStore;
    private readonly SlotOrderStore  _slotOrderStore = new();
    private readonly NicknameStore   _nicknameStore  = new();

    // ── Process watcher + anticheat ─────────────────────────────────────────

    private readonly IProcessWatcher  _processWatcher;
    private readonly AntiCheatDatabase _antiCheatDb;
    /// <summary>Profile that was auto-applied by process watcher (null = manual/none).</summary>
    private Profile? _autoAppliedProfile;

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

    public MainWindow(IInputForwardingService forwardingService, IProfileStore profileStore)
    {
        _forwardingService = forwardingService;
        _forwardingService.ForwardingError += OnForwardingError;
        _profileStore = profileStore;

        // Initialize anticheat database (best-effort — falls back to empty).
        string antiCheatPath = System.IO.Path.Combine(AppContext.BaseDirectory, "devices", "anticheat-games.json");
        try   { _antiCheatDb = AntiCheatDatabase.FromFile(antiCheatPath); }
        catch (Exception ex)
        {
            NavLog($"WARNING: Failed to load anticheat database — anticheat games will NOT be auto-detected: {ex.Message}");
            _antiCheatDb = new AntiCheatDatabase(Array.Empty<AntiCheatEntry>());
        }

        // Initialize WMI process watcher for auto-apply/auto-revert.
        _processWatcher = new WmiProcessWatcher();
        _processWatcher.ProcessStarted += OnProcessStarted;
        _processWatcher.ProcessStopped += OnProcessStopped;

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

        // Start watching for game processes that have saved profiles.
        InitializeProcessWatcher();
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
        SaveProfileButton.GotFocus  += (_, _) => OnElementGotFocus(SaveProfileButton);
        SaveProfileButton.LostFocus += (_, _) => OnElementLostFocus(SaveProfileButton);
        ProfilesButton.GotFocus     += (_, _) => OnElementGotFocus(ProfilesButton);
        ProfilesButton.LostFocus    += (_, _) => OnElementLostFocus(ProfilesButton);
        ExitButton.GotFocus         += (_, _) => OnElementGotFocus(ExitButton);
        ExitButton.LostFocus        += (_, _) => OnElementLostFocus(ExitButton);

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
        while (_cards.Count < count)
        {
            var card = new SlotCard { Margin = new Thickness(8, 4, 8, 4) };
            card.GotFocus        += (_, _) => OnElementGotFocus(card);
            card.LostFocus       += (_, _) => OnElementLostFocus(card);
            card.NicknameChanged += OnCardNicknameChanged;
            _cards.Add(card);
            SlotPanel.Children.Add(card);
        }

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
        SyncTabIndices();
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
    /// first all SlotCards from SlotPanel, then visible footer buttons.
    /// </summary>
    private List<UIElement> GetFocusableElementsInOrder()
    {
        var elements = new List<UIElement>();
        foreach (var card in _cards)
            elements.Add(card);

        if (RevertButton.Visibility == Visibility.Visible)
            elements.Add(RevertButton);
        elements.Add(SaveProfileButton);
        elements.Add(ProfilesButton);
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
        _processWatcher.Dispose();
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
            _viewModel.ExcludedSlotIndices = new HashSet<int>();
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

    // ── Profiles ──────────────────────────────────────────────────────────────

    /// <summary>
    /// P/Invoke helpers for detecting the foreground application's EXE name.
    /// Used by "Save Profile" to auto-populate the game exe field.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static string? GetForegroundExeName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            var proc = Process.GetProcessById((int)pid);
            return Path.GetFileName(proc.MainModule?.FileName);
        }
        catch { return null; }
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Auto-detect foreground EXE (likely the game the user wants to profile).
            // Note: ControlShift itself will often be the foreground app, so this is
            // a best-effort hint that the user can override.
            string? detectedExe = GetForegroundExeName();
            string defaultName = detectedExe is not null
                ? Path.GetFileNameWithoutExtension(detectedExe)
                : "New Profile";

            // Build a simple dialog for profile name and game exe.
            var nameBox = new TextBox
            {
                Text = defaultName,
                PlaceholderText = "Profile name",
                Margin = new Thickness(0, 0, 0, 8),
            };
            var exeBox = new TextBox
            {
                Text = detectedExe ?? string.Empty,
                PlaceholderText = "Game executable (e.g. game.exe) — optional",
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Profile Name:", Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "Game Executable:", Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(exeBox);

            var dialog = new ContentDialog
            {
                Title = "Save Profile",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            string profileName = nameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileName)) return;

            string? gameExe = string.IsNullOrWhiteSpace(exeBox.Text) ? null : exeBox.Text.Trim();

            // Build profile from current card order using VID:PID.
            var slotAssignments = new string?[4];
            for (int i = 0; i < _cards.Count && i < 4; i++)
            {
                var vidPid = _cards[i].VidPid;
                slotAssignments[i] = string.IsNullOrEmpty(vidPid) ? null : vidPid;
            }

            // Check anticheat database and auto-flag if needed.
            bool isAntiCheat = gameExe is not null && _antiCheatDb.IsAntiCheatGame(gameExe);
            if (isAntiCheat)
            {
                var warnDialog = new ContentDialog
                {
                    Title = "Anticheat Game Detected",
                    Content = $"{gameExe} uses kernel-level anticheat.\n\n" +
                              "ControlShift will automatically STOP forwarding when this game " +
                              "launches to avoid detection. The profile will still be saved for " +
                              "manual use in non-anticheat scenarios.",
                    PrimaryButtonText = "OK",
                    CloseButtonText = "Cancel",
                    XamlRoot = Content.XamlRoot,
                };

                var warnResult = await warnDialog.ShowAsync();
                if (warnResult != ContentDialogResult.Primary) return;
            }

            var profile = new Profile
            {
                ProfileName = profileName,
                GameExe = gameExe,
                SlotAssignments = slotAssignments,
                AntiCheatGame = isAntiCheat,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            _profileStore.Save(profile);
            NavLog($"[Profile] Saved: {profileName} (exe={gameExe ?? "none"}, anticheat={isAntiCheat})");

            // Refresh process watcher to include the new profile's exe.
            InitializeProcessWatcher();
        }
        catch (Exception ex)
        {
            NavLog($"[Profile] Save failed: {ex.Message}");
        }
    }

    private async void ProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profiles = _profileStore.LoadAll();

            if (profiles.Count == 0)
            {
                var emptyDialog = new ContentDialog
                {
                    Title = "Profiles",
                    Content = "No saved profiles. Reorder your controllers, then tap \"Save Profile\" to create one.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await emptyDialog.ShowAsync();
                return;
            }

            // Build a simple list of profile names for selection.
            var listView = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 300,
            };
            foreach (var p in profiles)
            {
                string label = p.ProfileName;
                if (p.GameExe is not null)
                    label += $" ({p.GameExe})";
                listView.Items.Add(label);
            }
            if (listView.Items.Count > 0)
                listView.SelectedIndex = 0;

            var dialog = new ContentDialog
            {
                Title = "Load Profile",
                Content = listView,
                PrimaryButtonText = "Load",
                SecondaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            int selectedIdx = listView.SelectedIndex;
            if (selectedIdx < 0 || selectedIdx >= profiles.Count) return;

            var selected = profiles[selectedIdx];

            if (result == ContentDialogResult.Primary)
            {
                await LoadProfileAsync(selected);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _profileStore.Delete(selected.ProfileName);
                NavLog($"[Profile] Deleted: {selected.ProfileName}");
                InitializeProcessWatcher();
            }
        }
        catch (Exception ex)
        {
            NavLog($"[Profile] Load/delete failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a profile's VID:PID assignments to current device paths,
    /// reorders the cards to match, and starts forwarding.
    /// </summary>
    private async Task LoadProfileAsync(Profile profile)
    {
        try
        {
            // Build connected-controller tuples from current cards.
            var connected = _cards
                .Select(c => (VidPid: c.VidPid, DevicePath: c.DevicePath))
                .Where(t => !string.IsNullOrEmpty(t.VidPid))
                .ToList();

            var assignments = ProfileResolver.Resolve(profile, connected);

            // Reorder _cards to match profile's slot order.
            // For each assignment that resolved a device path, find the matching card.
            _suppressFocusEvents = true;
            try
            {
                var newOrder = new List<SlotCard>();
                var remaining = new List<SlotCard>(_cards);

                foreach (var assignment in assignments)
                {
                    if (assignment.SourceDevicePath is null) continue;

                    var match = remaining.FirstOrDefault(c =>
                        string.Equals(c.DevicePath, assignment.SourceDevicePath,
                            StringComparison.OrdinalIgnoreCase));

                    if (match is not null)
                    {
                        newOrder.Add(match);
                        remaining.Remove(match);
                    }
                }

                // Append any remaining cards that weren't in the profile.
                newOrder.AddRange(remaining);

                ReplaceCardOrder(newOrder);
            }
            finally
            {
                _suppressFocusEvents = false;
            }

            // Start forwarding with the new order.
            await ApplyForwardingAsync();
            SaveCurrentOrder();
            NavLog($"[Profile] Loaded: {profile.ProfileName}");
        }
        catch (Exception ex)
        {
            NavLog($"[Profile] Load failed: {ex.Message}");
        }
    }

    // ── Process watcher ──────────────────────────────────────────────────────

    /// <summary>
    /// Collects all game exe names from saved profiles and starts the WMI
    /// process watcher. Called on startup and after profile save/delete.
    /// </summary>
    private void InitializeProcessWatcher()
    {
        try
        {
            var profiles = _profileStore.LoadAll();
            var exeNames = profiles
                .Where(p => p.GameExe is not null)
                .Select(p => p.GameExe!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (exeNames.Count > 0)
            {
                _processWatcher.StartWatching(exeNames);
                NavLog($"[ProcessWatcher] Watching {exeNames.Count} exe(s): [{string.Join(", ", exeNames)}]");
            }
            else
            {
                _processWatcher.StopWatching();
                NavLog("[ProcessWatcher] No profiles with game exe — stopped watching");
            }
        }
        catch (Exception ex)
        {
            NavLog($"[ProcessWatcher] Init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fires on a WMI worker thread when a watched process starts.
    /// Marshals to the UI thread via DispatcherQueue.
    /// </summary>
    private void OnProcessStarted(object? sender, ProcessEventArgs e)
    {
        NavLog($"[ProcessWatcher] Process started: {e.ProcessName} (PID {e.ProcessId})");

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var profile = _profileStore.FindByGameExe(e.ProcessName);
                if (profile is null)
                {
                    NavLog($"[ProcessWatcher] No profile for {e.ProcessName}");
                    return;
                }

                // ANTICHEAT SAFETY: If the game uses anticheat, STOP forwarding
                // immediately rather than starting it. Win32_ProcessStartTrace fires
                // at CreateProcess() time, giving ~2-5s before anticheat drivers scan.
                if (profile.AntiCheatGame || _antiCheatDb.IsAntiCheatGame(e.ProcessName))
                {
                    NavLog($"[ProcessWatcher] Anticheat game detected — stopping forwarding for {e.ProcessName}");
                    if (_forwardingService.IsForwarding)
                    {
                        await _forwardingService.StopForwardingAsync();
                        _autoAppliedProfile = null;
                        if (RevertButton is not null)
                            RevertButton.Visibility = Visibility.Collapsed;
                        NavLog("[ProcessWatcher] Forwarding stopped (anticheat safety)");
                    }
                    return;
                }

                // Non-anticheat game: auto-apply the profile.
                NavLog($"[ProcessWatcher] Auto-applying profile '{profile.ProfileName}' for {e.ProcessName}");
                _autoAppliedProfile = profile;
                await LoadProfileAsync(profile);
            }
            catch (Exception ex)
            {
                NavLog($"[ProcessWatcher] OnProcessStarted error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Fires on a WMI worker thread when a watched process exits.
    /// If an auto-applied profile is active, stops forwarding.
    /// </summary>
    private void OnProcessStopped(object? sender, ProcessEventArgs e)
    {
        NavLog($"[ProcessWatcher] Process stopped: {e.ProcessName} (PID {e.ProcessId})");

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                // Only auto-revert if the exiting process matches the auto-applied profile.
                if (_autoAppliedProfile?.GameExe is null) return;

                if (!string.Equals(_autoAppliedProfile.GameExe, e.ProcessName,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                NavLog($"[ProcessWatcher] Auto-applied game exited — stopping forwarding");
                _autoAppliedProfile = null;

                if (_forwardingService.IsForwarding)
                {
                    await _forwardingService.StopForwardingAsync();
                    if (RevertButton is not null)
                        RevertButton.Visibility = Visibility.Collapsed;
                    _ = RefreshAsync();
                }
            }
            catch (Exception ex)
            {
                NavLog($"[ProcessWatcher] OnProcessStopped error: {ex.Message}");
            }
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
            ReplaceCardOrder(sorted);
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
        else if (btn == SaveProfileButton) SaveProfileButton_Click(btn, new RoutedEventArgs());
        else if (btn == ProfilesButton) ProfilesButton_Click(btn, new RoutedEventArgs());
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
            SlotPanel.Children.RemoveAt(_reorderingIndex);
            SlotPanel.Children.Insert(newIdx, movingCard);

            RebuildCardsFromPanel();
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
        ApplyButtonFocusVisual(SaveProfileButton, ReferenceEquals(_focusedElement, SaveProfileButton));
        ApplyButtonFocusVisual(ProfilesButton, ReferenceEquals(_focusedElement, ProfilesButton));
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
