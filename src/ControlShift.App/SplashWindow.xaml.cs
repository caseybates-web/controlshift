using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;
using static ControlShift.App.Tray.NativeMethods;

namespace ControlShift.App;

/// <summary>
/// Xbox-styled splash screen shown on app launch. Displays a brief
/// animated boot sequence (green arc spin, title fade, underline sweep)
/// then auto-dismisses, signaling the app to proceed to tray mode.
/// </summary>
public sealed partial class SplashWindow : Window
{
    /// <summary>
    /// Raised when the splash animation completes and the window
    /// is ready to be closed. App.xaml.cs listens for this to
    /// proceed with tray icon setup.
    /// </summary>
    public event Action? SplashCompleted;

    public SplashWindow()
    {
        InitializeComponent();
        ConfigureWindow();

        // Start animation once the content is loaded and rendered.
        RootGrid.Loaded += OnRootGridLoaded;
    }

    private void ConfigureWindow()
    {
        // Remove the title bar entirely for a borderless look.
        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        // Hide from Alt+Tab — this is a transient splash, not a user-managed window.
        AppWindow.IsShownInSwitchers = false;

        Title = "ControlShift";

        CenterOnScreen();
    }

    private void CenterOnScreen()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);

        // Logical size: 480x300. Scale to physical pixels for current DPI.
        int pxW = (int)(480.0 * dpi / 96);
        int pxH = (int)(300.0 * dpi / 96);
        AppWindow.Resize(new SizeInt32(pxW, pxH));

        // Center on the nearest display.
        var display  = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var workArea = display.WorkArea;
        int x = workArea.X + (workArea.Width  - pxW) / 2;
        int y = workArea.Y + (workArea.Height - pxH) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        // Unhook so this only fires once.
        RootGrid.Loaded -= OnRootGridLoaded;

        // Wire up the Completed event before starting.
        SplashAnimation.Completed += OnSplashAnimationCompleted;
        SplashAnimation.Begin();
    }

    private void OnSplashAnimationCompleted(object? sender, object e)
    {
        // Signal the app that the splash is done.
        SplashCompleted?.Invoke();

        // Defer close to the next dispatcher tick so the PopupWindow
        // has time to fully register its HWND with WinUI.
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() => Close());
    }
}
