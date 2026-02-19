using System.Collections.ObjectModel;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.App.ViewModels;

/// <summary>
/// Main view model — owns the 4 controller slot VMs and drives enumeration.
/// </summary>
public sealed class MainViewModel
{
    private readonly IXInputEnumerator _xinput;
    private readonly IHidEnumerator    _hid;
    private readonly IControllerMatcher _matcher;

    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    public MainViewModel(
        IXInputEnumerator  xinput,
        IHidEnumerator     hid,
        IControllerMatcher matcher)
    {
        _xinput  = xinput;
        _hid     = hid;
        _matcher = matcher;
    }

    /// <summary>
    /// Re-enumerates all controllers and refreshes <see cref="Slots"/>.
    /// Safe to call from the UI thread — enumeration runs on a background thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        var (xinputSlots, hidDevices) = await Task.Run(() =>
            (_xinput.GetSlots(), _hid.GetDevices()));

        var matched = _matcher.Match(xinputSlots, hidDevices);

        var vms = matched.Select(mc =>
        {
            var vm = new SlotViewModel(mc.SlotIndex);
            vm.UpdateFrom(mc);
            return vm;
        });

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
