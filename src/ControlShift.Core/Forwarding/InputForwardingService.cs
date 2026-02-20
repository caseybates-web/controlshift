using System.Diagnostics;
using HidSharp;
using Nefarius.ViGEm.Client;
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

            // Create ViGEm pool on first call; grow if more controllers are needed.
            bool firstInit = _vigemClient == null;
            if (firstInit)
            {
                _vigemClient = new ViGEmClient();
                Debug.WriteLine("[Forwarding] Created ViGEmClient (first init)");
            }

            // Grow pool to match needed count (never shrink — reuse existing).
            int poolStartSize = _vigemPool.Count;
            while (_vigemPool.Count < neededCount)
            {
                var vigem = new ViGEmController(_vigemClient!);
                vigem.Connect();
                _vigemPool.Add(vigem);
                Debug.WriteLine($"[Forwarding] ViGEm pool: connected controller #{_vigemPool.Count} (slot {vigem.UserIndex})");
            }

            // Rebuild virtual slot indices from pool.
            _virtualSlotIndices.Clear();
            foreach (var v in _vigemPool)
            {
                if (v.UserIndex >= 0)
                    _virtualSlotIndices.Add(v.UserIndex);
            }

            // Whitelist our own process so we can still see hidden devices.
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is not null)
            {
                Debug.WriteLine($"[Forwarding] HidHide allowlist: {exePath}");
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
                    Debug.WriteLine($"[Forwarding] HidHide: hiding device for slot {assignment.TargetSlot}");
                    Debug.WriteLine($"[Forwarding]   HidSharp path: {assignment.SourceDevicePath}");
                    Debug.WriteLine($"[Forwarding]   Instance ID:   {instanceId}");
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

                    HidStream stream;
                    try
                    {
                        stream = hidDevice.Open();
                    }
                    catch
                    {
                        throw;
                    }

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
                Debug.WriteLine($"[Forwarding] Activating HidHide ({createdPairs.Count} device(s) hidden)");
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

                // If this was the first init and it failed, clean up the pool too.
                if (firstInit)
                {
                    for (int i = _vigemPool.Count - 1; i >= poolStartSize; i--)
                    {
                        try { _vigemPool[i].Disconnect(); _vigemPool[i].Dispose(); } catch { }
                    }
                    _vigemPool.RemoveRange(poolStartSize, _vigemPool.Count - poolStartSize);

                    if (_vigemPool.Count == 0)
                    {
                        _vigemClient?.Dispose();
                        _vigemClient = null;
                    }
                    _virtualSlotIndices.Clear();
                }

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
