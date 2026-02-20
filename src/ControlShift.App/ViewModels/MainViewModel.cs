using System.Collections.ObjectModel;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Forwarding;
using ControlShift.Core.Models;

namespace ControlShift.App.ViewModels;

/// <summary>
/// Main view model — owns the 4 controller slot VMs and drives enumeration + forwarding.
/// </summary>
public sealed class MainViewModel
{
    private readonly IXInputEnumerator    _xinput;
    private readonly IHidEnumerator       _hid;
    private readonly IControllerMatcher   _matcher;
    private readonly IInputForwardingService _forwarding;

    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    /// <summary>Whether any controller forwarding is currently active.</summary>
    public bool IsForwarding => _forwarding.IsForwarding;

    public MainViewModel(
        IXInputEnumerator      xinput,
        IHidEnumerator         hid,
        IControllerMatcher     matcher,
        IInputForwardingService forwarding)
    {
        _xinput     = xinput;
        _hid        = hid;
        _matcher    = matcher;
        _forwarding = forwarding;
    }

    /// <summary>
    /// Re-enumerates all controllers and refreshes <see cref="Slots"/>.
    /// Safe to call from the UI thread — enumeration runs on a background thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        IReadOnlyList<HidDeviceInfo> hidDevices = Array.Empty<HidDeviceInfo>();

        var matched = await Task.Run(() =>
        {
            var xinputSlots = _xinput.GetSlots();
            hidDevices      = _hid.GetDevices();
            return _matcher.Match(xinputSlots, hidDevices);
        });

        // ── HID diagnostic path dump ───────────────────────────────────────────
        // Writes every raw HID device path (no filtering) to a temp file so
        // Bluetooth detection issues can be diagnosed without guessing.
        try
        {
            var dumpPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "controlshift-hid-dump.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ControlShift HID dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"{hidDevices.Count} HID device(s) enumerated:");
            sb.AppendLine();
            for (int d = 0; d < hidDevices.Count; d++)
            {
                var h = hidDevices[d];
                sb.AppendLine($"[{d}] VID={h.Vid}  PID={h.Pid}  name='{h.ProductName ?? "(null)"}'");
                sb.AppendLine($"     {h.DevicePath}");
                sb.AppendLine();
            }

            sb.AppendLine("=== MATCH RESULTS ===");
            sb.AppendLine();
            foreach (var mc in matched)
            {
                if (!mc.IsConnected)
                {
                    sb.AppendLine($"Slot {mc.SlotIndex}: disconnected");
                    continue;
                }

                // Find which index in hidDevices this match came from.
                int hidIdx = -1;
                if (mc.Hid is not null)
                    for (int d = 0; d < hidDevices.Count; d++)
                        if (string.Equals(hidDevices[d].DevicePath, mc.Hid.DevicePath,
                                          StringComparison.OrdinalIgnoreCase))
                        { hidIdx = d; break; }

                sb.AppendLine($"Slot {mc.SlotIndex}: hidIndex={hidIdx}  VID={mc.Hid?.Vid ?? "(none)"}  PID={mc.Hid?.Pid ?? "(none)"}  hidConn={mc.HidConnectionType}  xinputConn={mc.XInputConnectionType}");
                if (mc.Hid is not null)
                    sb.AppendLine($"         path={mc.Hid.DevicePath}");
                sb.AppendLine();
            }

            System.IO.File.WriteAllText(dumpPath, sb.ToString());
        }
        catch { /* diagnostic writes must never throw */ }

        // Build VMs from matched data.
        var vms = new List<SlotViewModel>(matched.Count);
        for (int i = 0; i < matched.Count; i++)
        {
            var vm = new SlotViewModel(matched[i].SlotIndex);
            vm.OriginalSlotIndex = matched[i].SlotIndex;
            vm.UpdateFrom(matched[i]);
            vms.Add(vm);
        }

        // Sort: integrated first → connected by slot index → disconnected.
        var sorted = vms
            .OrderByDescending(v => v.IsIntegrated)
            .ThenByDescending(v => v.IsConnected)
            .ThenBy(v => v.SlotIndex)
            .ToList();

        Slots.Clear();
        foreach (var vm in sorted)
            Slots.Add(vm);
    }

    /// <summary>
    /// Applies a controller reorder by starting ViGEm + HidHide forwarding.
    /// </summary>
    public async Task ApplyReorderAsync(IReadOnlyList<SlotAssignment> assignments)
    {
        await _forwarding.StartForwardingAsync(assignments);

        // Mark all slots that have active forwarding.
        foreach (var slot in Slots)
        {
            var match = assignments.FirstOrDefault(a => a.TargetSlot == slot.SlotIndex);
            slot.IsForwarding = match?.SourceDevicePath is not null;
        }
    }

    /// <summary>
    /// Reverts all forwarding and refreshes the controller list.
    /// </summary>
    public async Task RevertAsync()
    {
        await _forwarding.StopForwardingAsync();
        foreach (var slot in Slots)
            slot.IsForwarding = false;
        await RefreshAsync();
    }
}
