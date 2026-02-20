using Microsoft.UI.Xaml;
using ControlShift.Core.Forwarding;

namespace ControlShift.App;

public partial class App : Application
{
    private SplashWindow? _splash;
    private IInputForwardingService? _forwarding;

    public App()
    {
        this.InitializeComponent();

        // Wire WinUI-specific unhandled exception handler for HidHide crash safety.
        this.UnhandledException += (_, _) =>
        {
            try { Program.HidHideService.ClearAllRules(); }
            catch { /* best effort */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create the forwarding service with the shared HidHide service.
        _forwarding = new InputForwardingService(Program.HidHideService);

        // Show the splash screen first. The rest of initialization
        // happens in OnSplashCompleted after the animation finishes.
        _splash = new SplashWindow();
        _splash.SplashCompleted += OnSplashCompleted;
        _splash.Activate();
    }

    private void OnSplashCompleted()
    {
        // Splash is done and closing itself.
        _splash = null;

        var window = new MainWindow(_forwarding!);
        window.Activate();
    }
}
