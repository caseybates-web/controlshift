using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ControlShift.App.ViewModels;
using ControlShift.App.Controls;
using ControlShift.App.Services;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Devices;
using Serilog;

namespace ControlShift.App;

/// <summary>
/// Main tray popup window — 320x480px dark-themed controller slot display.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<MainWindow>();

    public MainViewModel ViewModel { get; }
    private TrayIconService? _trayIconService;

    public MainWindow()
    {
        this.InitializeComponent();

        ViewModel = new MainViewModel(
            App.Services.GetRequiredService<IXInputEnumerator>(),
            App.Services.GetRequiredService<IHidEnumerator>(),
            App.Services.GetRequiredService<IDeviceFingerprinter>());

        // Set window size to match PRD spec: 320x480
        SetWindowSize(320, 480);

        // Initialize tray icon
        InitializeTrayIcon();

        // Build the slot cards in the UI
        BuildSlotCards();

        // Initial enumeration
        ViewModel.RefreshControllers();
        UpdateSlotCards();
    }

    private void SetWindowSize(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIconService = new TrayIconService(this);
            _trayIconService.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to initialize tray icon — running without tray support");
        }
    }

    private void BuildSlotCards()
    {
        SlotPanel.Children.Clear();

        foreach (var slot in ViewModel.Slots)
        {
            var card = new SlotCard();
            card.SetSlot(slot);
            SlotPanel.Children.Add(card);
        }
    }

    private void UpdateSlotCards()
    {
        for (int i = 0; i < SlotPanel.Children.Count && i < ViewModel.Slots.Count; i++)
        {
            if (SlotPanel.Children[i] is SlotCard card)
            {
                card.SetSlot(ViewModel.Slots[i]);
            }
        }

        int connected = ViewModel.Slots.Count(s => s.IsConnected);
        StatusText.Text = connected > 0
            ? $"{connected} controller{(connected != 1 ? "s" : "")} connected"
            : "No controllers detected";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshControllers();
        UpdateSlotCards();
        Logger.Information("Manual controller refresh triggered");
    }
}
