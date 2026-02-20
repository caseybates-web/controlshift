using ControlShift.Core.Forwarding;

namespace ControlShift.App;

/// <summary>
/// Explicit entry point, required for unpackaged WinUI 3 apps.
/// The XAML-generated Main in App.g.i.cs is suppressed via
/// DISABLE_XAML_GENERATED_MAIN so this class is the sole entry point.
/// </summary>
/// <remarks>
/// Bootstrap.Initialize() is intentionally absent here.
///
/// With WindowsAppSDKSelfContained=true the Windows App SDK runtime DLLs are
/// copied flat next to the exe by MSBuild. The native bootstrap DLL
/// (Microsoft.WindowsAppRuntime.Bootstrap.dll) loads the rest of the SDK from
/// the application directory before any managed code runs — no MSIX framework
/// package lookup is performed and no explicit Bootstrap.Initialize() call is
/// needed or correct in self-contained mode.
///
/// Bootstrap.Initialize() is only for framework-dependent unpackaged apps where
/// the SDK must be located from an installed MSIX package at runtime.
/// </remarks>
class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // CRITICAL: HidHide crash safety — always clear stale state on startup.
        // If the previous run crashed while HidHide was active, physical controllers
        // would be invisible to all apps until we clear the rules.
        try
        {
            using var hidHide = new HidHideService();
            if (hidHide.IsAvailable)
                hidHide.ClearAll();
        }
        catch
        {
            // Best-effort — HidHide may not be installed
        }

        // Register crash handler to clear HidHide on unhandled exceptions.
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try
            {
                using var hidHide = new HidHideService();
                if (hidHide.IsAvailable)
                    hidHide.ClearAll();
            }
            catch { /* must not throw during crash */ }
        };

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
