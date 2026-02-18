using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Microsoft.UI.Xaml;

namespace ControlShift.App;

/// <summary>
/// Explicit entry point, required for unpackaged WinUI 3 apps.
/// The XAML-generated Main in App.g.i.cs is suppressed via
/// DISABLE_XAML_GENERATED_MAIN so this class is the sole entry point.
/// </summary>
class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Bootstrap the Windows App SDK for unpackaged execution.
        // This enables ms-appx:// URI resolution, MRT Core, and all other
        // Windows App SDK services. Must be called before Application.Start().
        // 0x00010006 = major 1, minor 6 (SDK 1.6.x).
        Bootstrap.Initialize(0x00010006);

        // Mirror the initialization the XAML compiler would have generated.
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        Bootstrap.Shutdown();
    }
}
