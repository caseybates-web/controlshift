using System.Collections.ObjectModel;
using ControlShift.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;
using static ControlShift.App.Tray.NativeMethods;

namespace ControlShift.App;

/// <summary>
/// The 320×480 tray popup. Created once in App.OnLaunched and hidden/shown
/// on tray icon click. Never destroyed while the app is running.
/// </summary>
public sealed partial class PopupWindow : Window
{
    // ── Data-bound collections (x:Bind targets) ───────────────────────────────

    /// <summary>Always contains exactly 4 entries — one per XInput slot.</summary>
    public ObservableCollection<SlotViewModel> Slots { get; } = [];

    /// <summary>
    /// Devices detected but not occupying a numbered slot.
    /// Populated in Step 6; empty in Step 5.
    /// Using string as a placeholder type — Step 6 will replace with a real VM.
    /// </summary>
    public ObservableCollection<string> UnassignedDevices { get; } = [];

    /// <summary>Shows the "No unassigned controllers" placeholder when the list is empty.</summary>
    public Visibility UnassignedEmptyVisibility =>
        UnassignedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ── Exit signal ───────────────────────────────────────────────────────────

    /// <summary>Raised when the user clicks "Exit ControlShift" in the popup footer.</summary>
    public event Action? ExitRequested;

    // ── Construction ──────────────────────────────────────────────────────────

    public PopupWindow()
    {
        InitializeComponent();

        // Populate 4 empty slot view models.
        for (int i = 0; i < 4; i++)
            Slots.Add(new SlotViewModel(i));

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        // Extend content into the title bar so our custom header fills the whole window.
        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable    = false;
        presenter.IsMaximizable  = false;
        presenter.IsMinimizable  = false;
        presenter.IsAlwaysOnTop  = true;
        AppWindow.SetPresenter(presenter);

        // Hide from taskbar and Alt+Tab.
        AppWindow.IsShownInSwitchers = false;

        // Intercept the OS close action: hide the window instead of destroying it.
        // The app exits only via the in-popup "Exit ControlShift" button.
        AppWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        Title = "ControlShift";

        PositionNearTray();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Recalculates the popup position and size for the current DPI and work area,
    /// then makes the window visible and brings it to the foreground.
    /// Call this every time the popup is shown (not just once at startup).
    /// </summary>
    public void ShowNearTray()
    {
        PositionNearTray();
        AppWindow.Show();
        Activate();
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private void PositionNearTray()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        uint dpi  = GetDpiForWindow(hwnd);

        // Scale the logical 320×480 design size to physical pixels.
        int pxW = (int)(320.0 * dpi / 96);
        int pxH = (int)(480.0 * dpi / 96);
        AppWindow.Resize(new SizeInt32(pxW, pxH));

        // Position above the bottom-right corner of the work area (excludes taskbar).
        // DECISION: We assume the taskbar is docked at the bottom, which covers >95% of
        // users. Proper taskbar-edge detection can be added in a follow-up if needed.
        var display  = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        AppWindow.Move(new PointInt32(
            workArea.X + workArea.Width  - pxW - 12,
            workArea.Y + workArea.Height - pxH - 12));
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => AppWindow.Hide();

    private void ExitButton_Click(object sender, RoutedEventArgs e)
        => ExitRequested?.Invoke();
}
