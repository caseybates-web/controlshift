using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Serilog;

namespace ControlShift.App.Services;

/// <summary>
/// Manages the system tray (notification area) icon using H.NotifyIcon.WinUI.
/// Left-clicking the tray icon toggles the main popup window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<TrayIconService>();
    private readonly Window _window;
    private TaskbarIcon? _trayIcon;

    public TrayIconService(Window window)
    {
        _window = window;
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "ControlShift — Controller Manager"
        };

        // DECISION: Using H.NotifyIcon.WinUI because WinUI 3 does not ship
        // with a native NotifyIcon. H.NotifyIcon is the most widely used
        // community library for WinUI 3 tray icon support.

        // Load the tray icon from the bundled .ico file.
        // H.NotifyIcon uses GeneratedIconSource to render the .ico in the system tray.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "controlshift.ico");
        if (File.Exists(iconPath))
        {
            using var stream = File.OpenRead(iconPath);
            _trayIcon.Icon = new System.Drawing.Icon(stream);
        }
        else
        {
            Logger.Warning("Tray icon not found at {Path}, using default", iconPath);
        }

        _trayIcon.LeftClickCommand = new RelayCommand(ToggleWindow);

        Logger.Information("Tray icon initialized");
    }

    private void ToggleWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            if (appWindow.IsVisible)
            {
                appWindow.Hide();
            }
            else
            {
                // DECISION: Position the popup near the system tray area.
                // For Phase 1, we simply show/activate the window.
                // Phase 2 should position it anchored to the tray icon location.
                appWindow.Show();
                _window.Activate();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to toggle main window visibility");
        }
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }

    /// <summary>
    /// Minimal ICommand implementation for tray icon click binding.
    /// </summary>
    private sealed class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
#pragma warning disable CS0067 // Event is never used — required by ICommand interface
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }
}
