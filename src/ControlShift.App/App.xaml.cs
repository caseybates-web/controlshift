using ControlShift.App.Tray;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace ControlShift.App;

public partial class App : Application
{
    private PopupWindow?    _popup;
    private TrayIconService? _tray;

    // Used to debounce show-after-deactivation: when the user clicks the tray icon
    // while the popup is visible, the popup loses focus (which hides it) before
    // TrayIconActivated fires. Without the debounce we'd immediately reshow it.
    private long _hiddenByDeactivationAt = long.MinValue;
    private const long DebounceMs = 300;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create the popup window once. It is never destroyed while the app is running;
        // "closing" hides it. The tray icon keeps the app alive.
        _popup = new PopupWindow();

        // Auto-hide when the popup loses focus (click-away dismissal).
        _popup.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _hiddenByDeactivationAt = Environment.TickCount64;
                _popup.AppWindow.Hide();
            }
        };

        _popup.ExitRequested += OnExitRequested;

        // Set up the tray icon, subclassing the popup's HWND so we receive both
        // tray callbacks and WM_DEVICECHANGE on the same window.
        var hwnd       = WindowNative.GetWindowHandle(_popup);
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        _tray = new TrayIconService(hwnd, dispatcher);
        _tray.TrayIconActivated += OnTrayIconActivated;
        _tray.DeviceChanged     += OnDeviceChanged;

        // Register crash-safety cleanup so the tray icon is removed even if the
        // app crashes. (HidHide/ViGEm safety hooks are added in Phase 2.)
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Cleanup();
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private void OnTrayIconActivated()
    {
        if (_popup is null) return;

        if (_popup.AppWindow.IsVisible)
        {
            // Popup is already visible — the impending Deactivated event will hide it.
            // Don't toggle; let deactivation do its job.
            return;
        }

        // If the popup was just hidden by a deactivation event (user clicked the tray
        // icon to dismiss it), don't immediately reshow it.
        if (Environment.TickCount64 - _hiddenByDeactivationAt < DebounceMs)
            return;

        _popup.ShowNearTray();
    }

    // ── Device change ─────────────────────────────────────────────────────────

    private void OnDeviceChanged()
    {
        // Step 5: no-op — the popup shows static placeholder data.
        // Step 6 replaces this with a call to re-enumerate and refresh the slot cards.
    }

    // ── Exit ──────────────────────────────────────────────────────────────────

    private void OnExitRequested()
    {
        Cleanup();
        Exit();
    }

    private void Cleanup()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
