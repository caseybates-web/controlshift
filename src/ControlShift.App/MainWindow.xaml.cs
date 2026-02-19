using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ControlShift.App.ViewModels;
using ControlShift.App.Controls;
using ControlShift.App.Services;
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

        ViewModel = App.Services.GetRequiredService<MainViewModel>();

        // Set window size to match PRD spec: 320x480
        SetWindowSize(320, 480);

        // Initialize tray icon
        InitializeTrayIcon();

        // Build the slot cards in the UI
        BuildSlotCards();

        // Dispose tray icon on window close to prevent ghost icons
        this.Closed += OnClosed;

        // Initial enumeration (fire-and-forget; errors logged inside)
        _ = InitialRefreshAsync();
    }

    private async Task InitialRefreshAsync()
    {
        await ViewModel.RefreshControllersAsync();
        UpdateSlotCards();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _trayIconService?.Dispose();
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
            var factory = App.Services.GetRequiredService<Func<Window, TrayIconService>>();
            _trayIconService = factory(this);
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

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshControllersAsync();
        UpdateSlotCards();
        Logger.Information("Manual controller refresh triggered");
    }
}
