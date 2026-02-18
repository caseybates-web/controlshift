using Microsoft.UI.Xaml;

namespace ControlShift.App;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // DECISION: Tray-only app — no main window on launch.
        // System tray icon and popup window are wired up here in Phase 1, Step 5.
    }
}
