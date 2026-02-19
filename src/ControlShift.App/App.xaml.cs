using Microsoft.UI.Xaml;

namespace ControlShift.App;

public partial class App : Application
{
    private SplashWindow? _splash;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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

        var window = new MainWindow();
        window.Activate();
    }
}
