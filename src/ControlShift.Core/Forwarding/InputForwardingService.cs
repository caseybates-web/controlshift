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
/// DECISION: Uses a <see cref="SemaphoreSlim"/> to prevent concurrent Start/Stop calls.
/// All forwarding threads are dedicated (not thread-pool) with AboveNormal priority.
/// </remarks>
public sealed class InputForwardingService : IInputForwardingService
{
    private readonly IHidHideService _hidHide;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ForwardingPair> _pairs = new();
    private readonly List<SlotAssignment> _activeAssignments = new();

    private ViGEmClient? _vigemClient;
    private int _errorCount;

    public bool IsForwarding => _pairs.Count > 0;
    public IReadOnlyList<SlotAssignment> ActiveAssignments => _activeAssignments.AsReadOnly();

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

            // Create the ViGEmBus client (shared for all virtual controllers).
            _vigemClient = new ViGEmClient();

            // Whitelist our own process so we can still see hidden devices.
            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is not null)
                _hidHide.AddApplicationRule(exePath);

            var createdPairs = new List<ForwardingPair>();

            try
            {
                foreach (var assignment in assignments)
                {
                    if (assignment.SourceDevicePath is null)
                        continue;

                    // 1. Create ViGEm virtual controller.
                    var vigem = new ViGEmController(_vigemClient);
                    vigem.Connect();

                    // 2. Hide the physical device.
                    string instanceId = DevicePathConverter.ToInstanceId(assignment.SourceDevicePath);
                    _hidHide.HideDevice(instanceId);

                    // 3. Open the HID device for reading.
                    var hidDevice = DeviceList.Local.GetHidDevices()
                        .FirstOrDefault(d => string.Equals(d.DevicePath, assignment.SourceDevicePath,
                            StringComparison.OrdinalIgnoreCase));
                    if (hidDevice is null)
                    {
                        vigem.Dispose();
                        throw new InvalidOperationException(
                            $"HID device not found for path: {assignment.SourceDevicePath}");
                    }

                    var stream = hidDevice.Open();

                    // 4. Create and start the forwarding pair.
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
                _hidHide.SetActive(true);

                _pairs.AddRange(createdPairs);
                _activeAssignments.Clear();
                _activeAssignments.AddRange(assignments);
            }
            catch
            {
                // Rollback: destroy any pairs we already created.
                foreach (var pair in createdPairs)
                {
                    try { pair.Dispose(); } catch { /* best effort */ }
                }

                _hidHide.ClearAllRules();
                _vigemClient?.Dispose();
                _vigemClient = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopForwardingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Stop and dispose all forwarding pairs.
            foreach (var pair in _pairs)
            {
                try { pair.Dispose(); } catch { /* best effort */ }
            }
            _pairs.Clear();

            // Clear all HidHide rules and deactivate.
            _hidHide.ClearAllRules();

            // Dispose the ViGEmBus client.
            _vigemClient?.Dispose();
            _vigemClient = null;

            _activeAssignments.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnForwardingError(ForwardingErrorEventArgs e)
    {
        ForwardingError?.Invoke(this, e);

        // If all pairs have errored out, auto-stop.
        Interlocked.Increment(ref _errorCount);
        if (_errorCount >= _pairs.Count && _pairs.Count > 0)
        {
            _ = StopForwardingAsync();
        }
    }

    public void Dispose()
    {
        // Synchronous best-effort cleanup.
        foreach (var pair in _pairs)
        {
            try { pair.Dispose(); } catch { /* best effort */ }
        }
        _pairs.Clear();

        try { _hidHide.ClearAllRules(); } catch { /* best effort */ }

        _vigemClient?.Dispose();
        _vigemClient = null;

        _gate.Dispose();
    }
}
