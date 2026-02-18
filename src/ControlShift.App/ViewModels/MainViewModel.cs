using System.Collections.ObjectModel;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Devices;
using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.App.ViewModels;

/// <summary>
/// Main view model for the tray popup window.
/// Manages the 4 controller slot view models and drives enumeration.
/// </summary>
public class MainViewModel
{
    private static readonly ILogger Logger = Log.ForContext<MainViewModel>();
    private readonly IXInputEnumerator _xinputEnumerator;
    private readonly IHidEnumerator _hidEnumerator;
    private readonly IDeviceFingerprinter _fingerprinter;

    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    public MainViewModel(
        IXInputEnumerator xinputEnumerator,
        IHidEnumerator hidEnumerator,
        IDeviceFingerprinter fingerprinter)
    {
        _xinputEnumerator = xinputEnumerator;
        _hidEnumerator = hidEnumerator;
        _fingerprinter = fingerprinter;

        // Initialize 4 empty slots (P1–P4)
        for (int i = 0; i < 4; i++)
        {
            Slots.Add(new SlotViewModel { SlotIndex = i });
        }
    }

    /// <summary>
    /// Re-enumerate all controllers and update slot view models.
    /// Called on window open, manual refresh, and WM_DEVICECHANGE.
    /// </summary>
    public void RefreshControllers()
    {
        try
        {
            var xinputSlots = _xinputEnumerator.EnumerateSlots();
            var hidDevices = _hidEnumerator.EnumerateGameControllers();
            var controllers = _fingerprinter.IdentifyControllers(xinputSlots, hidDevices);

            for (int i = 0; i < 4 && i < controllers.Count; i++)
            {
                Slots[i].UpdateFrom(controllers[i]);
            }

            int connected = controllers.Count(c => c.IsConnected);
            Logger.Information("Refreshed controllers: {Connected} of {Total} slots active",
                connected, controllers.Count);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to refresh controllers");
        }
    }
}
