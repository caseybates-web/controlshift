using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using ControlShift.App.Controls;
using ControlShift.App.ViewModels;
using ControlShift.Core.Devices;
using ControlShift.Core.Enumeration;

namespace ControlShift.App;

/// <summary>
/// Standalone Xbox-aesthetic window — ~480×600 logical pixels, non-resizable.
/// Shows 4 controller slot cards. Click any card to rumble that controller.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _pollTimer;
    private readonly SlotCard[] _cards = new SlotCard[4];

    public MainWindow()
    {
        InitializeComponent();

        string dbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "devices", "known-devices.json");

        // DECISION: Fingerprinter falls back to an empty device list if known-devices.json
        // is missing (e.g. first run from a non-published build directory). Controllers still
        // enumerate correctly; only the INTEGRATED badge is suppressed.
        IDeviceFingerprinter fingerprinter;
        try
        {
            fingerprinter = DeviceFingerprinter.FromFile(dbPath);
        }
        catch
        {
            fingerprinter = new DeviceFingerprinter(Array.Empty<KnownDeviceEntry>());
        }

        _viewModel = new MainViewModel(new XInputEnumerator(), new HidEnumerator(), fingerprinter);

        SetWindowSize(480, 600);

        // Build 4 fixed slot cards — one per XInput slot P1–P4.
        for (int i = 0; i < 4; i++)
        {
            _cards[i] = new SlotCard();
            SlotPanel.Children.Add(_cards[i]);
        }

        // Poll for device changes every 5 seconds.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();

        Closed += (_, _) => _pollTimer.Stop();

        // Initial scan on window open.
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = "Scanning...";
        await _viewModel.RefreshAsync();
        UpdateCards();
    }

    private void UpdateCards()
    {
        for (int i = 0; i < _cards.Length; i++)
        {
            if (i < _viewModel.Slots.Count)
                _cards[i].SetSlot(_viewModel.Slots[i]);
        }

        int connected = _viewModel.Slots.Count(s => s.IsConnected);
        StatusText.Text = connected > 0
            ? $"{connected} controller{(connected != 1 ? "s" : "")} connected"
            : "No controllers detected";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    /// <summary>
    /// Resizes the window to the given logical pixel dimensions, scaled for the
    /// current display DPI. AppWindow.Resize takes physical pixels.
    /// </summary>
    private void SetWindowSize(int logicalWidth, int logicalHeight)
    {
        var hwnd  = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi   = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;

        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        appWindow.Resize(new SizeInt32(
            (int)(logicalWidth  * scale),
            (int)(logicalHeight * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
