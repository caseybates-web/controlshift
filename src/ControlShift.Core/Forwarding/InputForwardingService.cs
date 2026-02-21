using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Vortice.XInput;
using ControlShift.Core.Devices;
using ControlShift.Core.Diagnostics;
using ControlShift.Core.Models;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Orchestrates controller reordering by managing ViGEm virtual controllers,
/// HidHide device suppression, and HID→ViGEm forwarding pairs.
/// </summary>
/// <remarks>
/// ViGEm controllers are connected ONCE and persisted across stop/start cycles.
/// Only <see cref="RevertAllAsync"/> and <see cref="Dispose"/> disconnect them.
/// This prevents WM_DEVICECHANGE chime loops caused by ViGEm connect/disconnect.
///
/// Virtual slot detection uses a before/after XInput snapshot to identify which
/// slots were added by ViGEm. On pool reuse, cached values are kept.
///
/// IMPORTANT: ViGEm's UserIndex reports the internal connection order (0-based),
/// NOT the actual XInput slot assigned by Windows. Do not use UserIndex for
/// virtual slot detection — use XInput GetState() snapshots instead.
/// </remarks>
public sealed class InputForwardingService : IInputForwardingService
{
    private readonly IHidHideService _hidHide;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ForwardingPair> _pairs = new();
    private readonly List<SlotAssignment> _activeAssignments = new();
    private readonly HashSet<int> _virtualSlotIndices = new();

    // Persistent ViGEm pool — created once, reused across stop/start cycles.
    private ViGEmClient? _vigemClient;
    private readonly List<IViGEmController> _vigemPool = new();
    private int _errorCount;

    public bool IsForwarding => _pairs.Count > 0;
    public IReadOnlyList<SlotAssignment> ActiveAssignments => _activeAssignments.AsReadOnly();
    public IReadOnlySet<int> VirtualSlotIndices => _virtualSlotIndices;

    public event EventHandler<ForwardingErrorEventArgs>? ForwardingError;

    public InputForwardingService(IHidHideService hidHide)
    {
        _hidHide = hidHide;
    }

    private static void DiagLog(string msg) => DebugLog.Log($"[ForwardingSvc] {msg}");

    public async Task StartForwardingAsync(IReadOnlyList<SlotAssignment> assignments)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsForwarding)
                throw new InvalidOperationException("Forwarding is already active. Call StopForwardingAsync first.");

            _errorCount = 0;

            // Count how many non-null assignments need a ViGEm controller.
            int neededCount = assignments.Count(a => a.SourceDevicePath is not null);
            DiagLog($"Needed ViGEm count: {neededCount}, pool size: {_vigemPool.Count}");
            InputTrace.Log($"[FwdSvc] StartForwardingAsync — {assignments.Count} assignments, pool={_vigemPool.Count}");

            // Create ViGEm pool on first call; grow if more controllers are needed.
            bool firstInit = _vigemClient == null;
            int poolStartSize = _vigemPool.Count;
            bool poolGrew = false;

            // Snapshot XInput slots BEFORE ViGEm connects.
            // Any slot occupied now is a REAL controller — never treat it as virtual.
            HashSet<int>? preVigemSlots = null;
            if (_vigemPool.Count < neededCount)
            {
                preVigemSlots = SnapshotOccupiedXInputSlots();
                DiagLog($"Pre-ViGEm occupied slots: [{string.Join(", ", preVigemSlots)}]");
            }

            if (firstInit)
            {
                _vigemClient = new ViGEmClient();
                DiagLog("Created ViGEmClient (first init)");
            }

            // Grow pool to match needed count (never shrink — reuse existing).
            // Connect() with retry — driver may need time to assign slots.
            try
            {
                while (_vigemPool.Count < neededCount)
                {
                    var vigem = new ViGEmController(_vigemClient!);
                    bool connected = false;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            if (attempt > 0)
                                await Task.Delay(300 * attempt);
                            vigem.Connect();
                            connected = true;
                            DiagLog($"ViGEm Connect() succeeded on attempt {attempt + 1}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            DiagLog($"ViGEm Connect() attempt {attempt + 1}/5 failed: {ex.GetType().Name}");
                            try { vigem.Disconnect(); } catch { }
                        }
                    }

                    if (!connected)
                    {
                        vigem.Dispose();
                        throw new InvalidOperationException(
                            "ViGEm failed to assign an XInput slot after 5 attempts.");
                    }

                    _vigemPool.Add(vigem);
                    poolGrew = true;
                    DiagLog($"ViGEm pool: connected controller #{_vigemPool.Count}");
                }
            }
            catch
            {
                // Clean up any controllers added during this failed growth attempt.
                for (int i = _vigemPool.Count - 1; i >= poolStartSize; i--)
                {
                    try { _vigemPool[i].Disconnect(); _vigemPool[i].Dispose(); } catch { }
                }
                _vigemPool.RemoveRange(poolStartSize, _vigemPool.Count - poolStartSize);

                if (firstInit && _vigemPool.Count == 0)
                {
                    _vigemClient?.Dispose();
                    _vigemClient = null;
                }
                _virtualSlotIndices.Clear();
                throw;
            }

            // Detect virtual slots via before/after XInput snapshot.
            // Virtual = slots that appeared AFTER ViGEm connected.
            // NOTE: Do NOT use ViGEm UserIndex — it reports internal connection order,
            // not the actual XInput slot assigned by Windows.
            if (preVigemSlots is not null && poolGrew)
            {
                // Wait for Windows to register new ViGEm device nodes in XInput.
                await Task.Delay(300);

                var postVigemSlots = SnapshotOccupiedXInputSlots();
                DiagLog($"Post-ViGEm occupied slots: [{string.Join(", ", postVigemSlots)}]");

                _virtualSlotIndices.Clear();
                foreach (var slot in postVigemSlots)
                {
                    if (!preVigemSlots.Contains(slot))
                        _virtualSlotIndices.Add(slot);
                }
                DiagLog($"Virtual slots (snapshot diff): [{string.Join(", ", _virtualSlotIndices)}]");
                DebugLog.Log($"[ViGEm] Virtual slots detected: [{string.Join(", ", _virtualSlotIndices)}]");
                InputTrace.Log($"[FwdSvc] VirtualSlotIndices (snapshot diff): [{string.Join(", ", _virtualSlotIndices)}]");
            }
            else if (!poolGrew)
            {
                // Pool reuse with no growth: cached values are correct.
                // ViGEm controllers are persistent and their slots don't change.
                DiagLog($"Virtual slots (cached): [{string.Join(", ", _virtualSlotIndices)}]");
                InputTrace.Log($"[FwdSvc] VirtualSlotIndices (cached): [{string.Join(", ", _virtualSlotIndices)}]");
            }

            // Whitelist our own process so we can still see hidden devices.
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is not null)
            {
                DiagLog($"HidHide allowlist: {exePath}");
                _hidHide.AddApplicationRule(exePath);
            }

            // Whitelist Xbox Game Bar processes so the Guide button still
            // reaches Windows when physical controllers are hidden.
            AllowlistGameBarProcesses();

            var createdPairs = new List<ForwardingPair>();

            try
            {
                int poolIdx = 0;
                foreach (var assignment in assignments)
                {
                    if (assignment.SourceDevicePath is null || assignment.SourceSlotIndex < 0)
                        continue;

                    // Reuse ViGEm controller from the persistent pool.
                    var vigem = _vigemPool[poolIdx++];

                    // Hide the physical device via HidHide.
                    string instanceId = DevicePathConverter.ToInstanceId(assignment.SourceDevicePath);
                    DiagLog($"HidHide: slot {assignment.TargetSlot} → {instanceId}");
                    DebugLog.HidHide("hide", instanceId);
                    _hidHide.HideDevice(instanceId);

                    // Create and start the forwarding pair.
                    // Reads XInput (via XInputGetStateEx for Guide button) from the
                    // physical slot — no HID stream needed. This avoids HID report
                    // format mismatches across USB, BT, and integrated gamepads.
                    var pair = new ForwardingPair(
                        assignment.SourceSlotIndex,
                        assignment.TargetSlot,
                        assignment.SourceDevicePath,
                        vigem,
                        OnForwardingError);

                    pair.Start();
                    createdPairs.Add(pair);
                    DiagLog($"ForwardingPair: physSlot={assignment.SourceSlotIndex} → targetSlot={assignment.TargetSlot}");
                    DebugLog.ViGEmSlotAssigned(assignment.TargetSlot, vigem.UserIndex);
                }

                // Activate HidHide now that all devices are hidden.
                DiagLog($"Activating HidHide ({createdPairs.Count} device(s) hidden)");
                _hidHide.SetActive(true);

                _pairs.AddRange(createdPairs);
                _activeAssignments.Clear();
                _activeAssignments.AddRange(assignments);
            }
            catch
            {
                // Rollback: destroy pairs (HID streams only — ViGEm pool stays).
                foreach (var pair in createdPairs)
                {
                    try { pair.Dispose(); } catch { /* best effort */ }
                }

                _hidHide.ClearAllRules();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops HID forwarding and clears HidHide rules.
    /// ViGEm virtual controllers remain connected for reuse on next StartForwardingAsync.
    /// </summary>
    public async Task StopForwardingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DebugLog.Log("[Forwarding] StopForwardingAsync — clearing pairs and HidHide rules");
            // Stop and dispose all forwarding pairs (HID streams only).
            foreach (var pair in _pairs)
            {
                try { pair.Dispose(); } catch { /* best effort */ }
            }
            _pairs.Clear();

            // Clear all HidHide rules and deactivate.
            DebugLog.HidHide("clearAll", "(all rules)");
            _hidHide.ClearAllRules();

            _activeAssignments.Clear();
            // NOTE: ViGEm pool and _virtualSlotIndices are intentionally kept alive.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hot-swaps the physical→virtual mapping on running forwarding pairs.
    /// Threads keep running, HidHide stays active, ViGEm controllers stay connected.
    /// Only the ViGEm controller reference on each pair changes.
    /// </summary>
    public async Task UpdateMappingAsync(IReadOnlyList<SlotAssignment> assignments)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsForwarding)
                throw new InvalidOperationException("Cannot update mapping — forwarding is not active.");

            DiagLog($"UpdateMappingAsync — {assignments.Count} assignments, {_pairs.Count} active pairs");
            InputTrace.Log($"[FwdSvc] UpdateMappingAsync — remapping {_pairs.Count} pairs");

            // Build lookup: physicalSlot → ForwardingPair
            var pairsByPhysSlot = new Dictionary<int, ForwardingPair>();
            foreach (var pair in _pairs)
                pairsByPhysSlot[pair.PhysicalSlot] = pair;

            // Reassign ViGEm controllers: assignment at index i gets pool[i].
            int poolIdx = 0;
            foreach (var assignment in assignments)
            {
                if (assignment.SourceDevicePath is null || assignment.SourceSlotIndex < 0)
                    continue;

                if (!pairsByPhysSlot.TryGetValue(assignment.SourceSlotIndex, out var pair))
                {
                    DiagLog($"WARNING: no pair found for physSlot={assignment.SourceSlotIndex}");
                    continue;
                }

                var vigem = _vigemPool[poolIdx++];
                pair.SwapTarget(assignment.TargetSlot, vigem);
            }

            _activeAssignments.Clear();
            _activeAssignments.AddRange(assignments);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Full revert: stops forwarding, disconnects all ViGEm controllers, clears HidHide.
    /// Called by "Revert All" to restore physical controllers to their original slots.
    /// </summary>
    public async Task RevertAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DebugLog.Log("[Forwarding] RevertAllAsync — full teardown");
            // Stop all forwarding pairs.
            foreach (var pair in _pairs)
            {
                try { pair.Dispose(); } catch { /* best effort */ }
            }
            _pairs.Clear();

            // Clear HidHide.
            _hidHide.ClearAllRules();

            // Disconnect and dispose all ViGEm controllers.
            foreach (var vigem in _vigemPool)
            {
                try { vigem.Disconnect(); vigem.Dispose(); } catch { /* best effort */ }
            }
            _vigemPool.Clear();

            // Dispose the ViGEmBus client.
            _vigemClient?.Dispose();
            _vigemClient = null;

            _activeAssignments.Clear();
            _virtualSlotIndices.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Virtual slot detection ───────────────────────────────────────────────

    /// <summary>
    /// Returns the set of XInput slots that currently respond to GetState.
    /// Ghost ViGEm nodes from previous sessions fail GetState and are excluded.
    /// </summary>
    private static HashSet<int> SnapshotOccupiedXInputSlots()
    {
        var occupied = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            if (XInput.GetState((uint)i, out _))
                occupied.Add(i);
        }
        return occupied;
    }

    /// <summary>
    /// Adds Xbox Game Bar executables to the HidHide allowlist so the Guide
    /// button on physical controllers still reaches Windows when hidden.
    /// </summary>
    private void AllowlistGameBarProcesses()
    {
        // GameBarPresenceWriter in System32 — always at a fixed path.
        TryAllowlist(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "GameBarPresenceWriter.exe"));

        // GameBar, GameBarFTServer, GameBarElevatedFT live in the versioned
        // WindowsApps folder. Resolve the wildcard directory at runtime.
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var appsDir = Path.Combine(programFiles, "WindowsApps");
            if (Directory.Exists(appsDir))
            {
                var overlayDirs = Directory.GetDirectories(appsDir, "Microsoft.XboxGamingOverlay_*");
                foreach (var dir in overlayDirs)
                {
                    TryAllowlist(Path.Combine(dir, "GameBar.exe"));
                    TryAllowlist(Path.Combine(dir, "GameBarFTServer.exe"));
                    TryAllowlist(Path.Combine(dir, "GameBarElevatedFT.exe"));
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog($"GameBar allowlist scan failed: {ex.Message}");
        }
    }

    private void TryAllowlist(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            _hidHide.AddApplicationRule(path);
            DiagLog($"HidHide allowlist (GameBar): {path}");
        }
        catch (Exception ex)
        {
            DiagLog($"HidHide allowlist failed for {path}: {ex.Message}");
        }
    }

    private void OnForwardingError(ForwardingErrorEventArgs e)
    {
        DebugLog.Log($"[Forwarding] Error slot={e.TargetSlot} path={e.DevicePath}: {e.ErrorMessage}");
        ForwardingError?.Invoke(this, e);

        // If all pairs have errored out, auto-stop (lightweight — keeps ViGEm).
        Interlocked.Increment(ref _errorCount);
        if (_errorCount >= _pairs.Count && _pairs.Count > 0)
        {
            _ = StopForwardingAsync();
        }
    }

    public void Dispose()
    {
        // Synchronous best-effort cleanup — full teardown including ViGEm.
        foreach (var pair in _pairs)
        {
            try { pair.Dispose(); } catch { /* best effort */ }
        }
        _pairs.Clear();

        try { _hidHide.ClearAllRules(); } catch { /* best effort */ }

        foreach (var vigem in _vigemPool)
        {
            try { vigem.Disconnect(); vigem.Dispose(); } catch { /* best effort */ }
        }
        _vigemPool.Clear();

        _vigemClient?.Dispose();
        _vigemClient = null;
        _virtualSlotIndices.Clear();

        _gate.Dispose();
    }
}
