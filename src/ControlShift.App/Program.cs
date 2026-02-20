using ControlShift.Core.Devices;

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
    /// <summary>
    /// Shared HidHide service — created once, used by CrashSafetyGuard and InputForwardingService.
    /// </summary>
    internal static IHidHideService HidHideService { get; private set; } = default!;

    [STAThread]
    static void Main(string[] args)
    {
        // Create the HidHide service once. Falls back to NullHidHideService if driver is missing.
        try
        {
            var svc = new HidHideService();
            if (svc.IsDriverInstalled)
                HidHideService = svc;
            else
                HidHideService = new NullHidHideService();
        }
        catch
        {
            HidHideService = new NullHidHideService();
        }

        // CRITICAL: CrashSafetyGuard clears stale HidHide state on startup AND
        // registers crash/exit handlers for defense-in-depth.
        CrashSafetyGuard.Install(HidHideService);

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
