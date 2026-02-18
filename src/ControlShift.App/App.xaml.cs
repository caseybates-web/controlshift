using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ControlShift.Core.Enumeration;
using ControlShift.Core.Devices;
using Serilog;

namespace ControlShift.App;

/// <summary>
/// WinUI 3 application entry point. Configures DI, logging, and the main window.
/// </summary>
public partial class App : Application
{
    private Window? _mainWindow;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        this.InitializeComponent();

        // Configure Serilog to write diagnostics to %APPDATA%\ControlShift\logs\
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ControlShift", "logs", "controlshift-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("ControlShift starting up");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Configure dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Load known-devices database
        var knownDb = Services.GetRequiredService<KnownDeviceDatabase>();
        var devicesJsonPath = Path.Combine(AppContext.BaseDirectory, "devices", "known-devices.json");

        // DECISION: In development, known-devices.json lives alongside the solution root.
        // In production builds, it should be bundled as a content file next to the exe.
        // Try both paths for flexibility during development.
        if (!File.Exists(devicesJsonPath))
        {
            devicesJsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "devices", "known-devices.json");
        }

        if (File.Exists(devicesJsonPath))
        {
            knownDb.Load(devicesJsonPath);
        }
        else
        {
            Log.Warning("Could not find known-devices.json at expected paths");
        }

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<KnownDeviceDatabase>();
        services.AddSingleton<IDeviceFingerprinter, DeviceFingerprinter>();
        services.AddTransient<IXInputEnumerator, XInputEnumerator>();
        services.AddTransient<IHidEnumerator, HidEnumerator>();
    }
}
