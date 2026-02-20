using Microsoft.UI.Xaml;
using ControlShift.Core.Forwarding;
using ControlShift.Core.Profiles;

namespace ControlShift.App;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += (_, args) =>
        {
            try { Program.HidHideService.ClearAllRules(); }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var forwardingService = new InputForwardingService(Program.HidHideService);
        var profileStore = new ProfileStore();
        var window = new MainWindow(forwardingService, profileStore);
        window.Activate();
    }
}
