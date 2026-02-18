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

        // Load known-devices database (bundled to output via csproj Content item)
        var knownDb = Services.GetRequiredService<KnownDeviceDatabase>();
        var devicesJsonPath = Path.Combine(AppContext.BaseDirectory, "devices", "known-devices.json");
        knownDb.Load(devicesJsonPath);

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
