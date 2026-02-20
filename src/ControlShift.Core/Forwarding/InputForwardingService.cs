using System.Diagnostics;
using HidSharp;
using Nefarius.ViGEm.Client;
using Vortice.XInput;
using ControlShift.Core.Devices;
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
    private static readonly string DiagPath =
        Path.Combine(Path.GetTempPath(), "controlshift-forwarding-diag.txt");

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

    private static void DiagLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
        Debug.Write(line);
        try { File.AppendAllText(DiagPath, line); } catch { }
    }

    public async Task StartForwardingAsync(IReadOnlyList<SlotAssignment> assignments)
    {
        await _gate.WaitAsync();
        try
        {
            // Clear diagnostic file on each start.
            try { File.WriteAllText(DiagPath, ""); } catch { }

            if (IsForwarding)
                throw new InvalidOperationException("Forwarding is already active. Call StopForwardingAsync first.");

            _errorCount = 0;

            // Count how many non-null assignments need a ViGEm controller.
            int neededCount = assignments.Count(a => a.SourceDevicePath is not null);
            DiagLog($"Needed ViGEm count: {neededCount}, pool size: {_vigemPool.Count}");

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
            }
            else if (!poolGrew)
            {
                // Pool reuse with no growth: cached values are correct.
                // ViGEm controllers are persistent and their slots don't change.
                DiagLog($"Virtual slots (cached): [{string.Join(", ", _virtualSlotIndices)}]");
            }

            // Whitelist our own process so we can still see hidden devices.
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is not null)
            {
                DiagLog($"HidHide allowlist: {exePath}");
                _hidHide.AddApplicationRule(exePath);
            }

            var createdPairs = new List<ForwardingPair>();

            try
            {
                int poolIdx = 0;
                foreach (var assignment in assignments)
                {
                    if (assignment.SourceDevicePath is null)
                        continue;

                    // Reuse ViGEm controller from the persistent pool.
                    var vigem = _vigemPool[poolIdx++];

                    // Hide the physical device via HidHide.
                    string instanceId = DevicePathConverter.ToInstanceId(assignment.SourceDevicePath);
                    DiagLog($"HidHide: slot {assignment.TargetSlot} → {instanceId}");
                    _hidHide.HideDevice(instanceId);

                    // Open the HID device for reading.
                    var hidDevice = DeviceList.Local.GetHidDevices()
                        .FirstOrDefault(d => string.Equals(d.DevicePath, assignment.SourceDevicePath,
                            StringComparison.OrdinalIgnoreCase));
                    if (hidDevice is null)
                    {
                        throw new InvalidOperationException(
                            $"HID device not found for path: {assignment.SourceDevicePath}");
                    }

                    var stream = hidDevice.Open();

                    // Create and start the forwarding pair.
                    var pair = new ForwardingPair(
                        assignment.TargetSlot,
                        assignment.SourceDevicePath,
                        vigem,
                        stream,
                        OnForwardingError);

                    pair.Start();
                    createdPairs.Add(pair);
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
            // Stop and dispose all forwarding pairs (HID streams only).
            foreach (var pair in _pairs)
            {
                try { pair.Dispose(); } catch { /* best effort */ }
            }
            _pairs.Clear();

            // Clear all HidHide rules and deactivate.
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
    /// Full revert: stops forwarding, disconnects all ViGEm controllers, clears HidHide.
    /// Called by "Revert All" to restore physical controllers to their original slots.
    /// </summary>
    public async Task RevertAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
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

    private void OnForwardingError(ForwardingErrorEventArgs e)
    {
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
