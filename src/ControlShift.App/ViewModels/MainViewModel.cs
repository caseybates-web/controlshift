using System.Collections.ObjectModel;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.App.ViewModels;

/// <summary>
/// Main view model — owns the 4 controller slot VMs and drives enumeration.
/// </summary>
public sealed class MainViewModel
{
    private readonly IXInputEnumerator    _xinput;
    private readonly IHidEnumerator       _hid;
    private readonly IDeviceFingerprinter _fingerprinter;

    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    public MainViewModel(
        IXInputEnumerator    xinput,
        IHidEnumerator       hid,
        IDeviceFingerprinter fingerprinter)
    {
        _xinput        = xinput;
        _hid           = hid;
        _fingerprinter = fingerprinter;
    }

    /// <summary>
    /// Re-enumerates all controllers and refreshes <see cref="Slots"/>.
    /// Safe to call from the UI thread — enumeration runs on a background thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        var (xinputSlots, fingerprintedDevices) = await Task.Run(() =>
        {
            var x = _xinput.GetSlots();
            var h = _hid.GetDevices();
            var f = _fingerprinter.Fingerprint(h);
            return (x, f);
        });

        // First fingerprinted HID device that matches a known integrated gamepad.
        var integratedHid = fingerprintedDevices.FirstOrDefault(f => f.IsIntegratedGamepad);

        // Build a VM for each of the 4 XInput slots.
        // Heuristic: the first connected XInput slot is flagged as the integrated gamepad
        // when we found a matching integrated HID device.
        var vms = new List<SlotViewModel>(4);
        bool integratedAssigned = false;

        foreach (var slot in xinputSlots)
        {
            var vm = new SlotViewModel(slot.SlotIndex);

            FingerprintedDevice? match = null;
            if (integratedHid is not null && slot.IsConnected && !integratedAssigned)
            {
                match = integratedHid;
                integratedAssigned = true;
            }

            vm.UpdateFrom(slot, match);
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
}
