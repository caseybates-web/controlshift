using HidSharp;
using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates HID game controllers using the HidSharp library.
/// Filters to HID Usage Page 0x01 (Generic Desktop), Usage 0x05 (Gamepad) and 0x04 (Joystick).
/// </summary>
public sealed class HidEnumerator : IHidEnumerator
{
    private static readonly ILogger Logger = Log.ForContext<HidEnumerator>();

    private const uint UsagePageGenericDesktop = 0x0001;
    private const uint UsageGamepad = 0x0005;
    private const uint UsageJoystick = 0x0004;

    public IReadOnlyList<HidDeviceInfo> EnumerateGameControllers()
    {
        var results = new List<HidDeviceInfo>();

        try
        {
            var hidDevices = DeviceList.Local.GetHidDevices();

            foreach (var device in hidDevices)
            {
                try
                {
                    if (!IsGameController(device))
                        continue;

                    string? productName = SafeGetProductName(device);
                    string? serialNumber = SafeGetSerialNumber(device);

                    results.Add(new HidDeviceInfo
                    {
                        Vid = device.VendorID.ToString("X4"),
                        Pid = device.ProductID.ToString("X4"),
                        DevicePath = device.DevicePath,
                        ProductName = productName,
                        SerialNumber = serialNumber,
                        ReleaseNumber = device.ReleaseNumberBcd
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to query HID device at {DevicePath}",
                        device.DevicePath);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate HID devices");
        }

        Logger.Information("Discovered {Count} HID game controllers", results.Count);
        return results.AsReadOnly();
    }

    private static bool IsGameController(HidDevice device)
    {
        try
        {
            var reportDescriptor = device.GetReportDescriptor();
            return reportDescriptor.DeviceItems.Any(item =>
                item.Usages.GetAllValues().Any(usage =>
                {
                    uint usagePage = (uint)usage >> 16;
                    uint usageId = (uint)usage & 0xFFFF;
                    return usagePage == UsagePageGenericDesktop &&
                           (usageId == UsageGamepad || usageId == UsageJoystick);
                }));
        }
        catch
        {
            // Some devices don't support report descriptor queries
            return false;
        }
    }

    private static string? SafeGetProductName(HidDevice device)
    {
        try { return device.GetProductName(); }
        catch { return null; }
    }

    private static string? SafeGetSerialNumber(HidDevice device)
    {
        try { return device.GetSerialNumber(); }
        catch { return null; }
    }
}
