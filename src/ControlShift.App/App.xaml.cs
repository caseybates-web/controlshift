using Microsoft.UI.Xaml;
using ControlShift.Core.Diagnostics;
using ControlShift.Core.Forwarding;

namespace ControlShift.App;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += (_, args) =>
        {
            DebugLog.Exception("UnhandledException", args.Exception);
            try { Program.HidHideService.ClearAllRules(); }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var forwardingService = new InputForwardingService(Program.HidHideService);
        var window = new MainWindow(forwardingService);
        window.Activate();
    }
}
