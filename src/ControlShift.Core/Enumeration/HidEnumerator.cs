using HidSharp;
using ControlShift.Core.Devices;
using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates HID game controllers using the HidSharp library.
/// Two-pass approach:
/// 1. Standard filter: HID Usage Page 0x01 (Generic Desktop), Usage 0x05 (Gamepad) / 0x04 (Joystick)
/// 2. Known-VID fallback: includes devices from known handheld OEMs regardless of usage page,
///    since integrated gamepads (ROG Ally, Legion Go, etc.) often use vendor-specific usage pages.
/// </summary>
public sealed class HidEnumerator : IHidEnumerator
{
    private static readonly ILogger Logger = Log.ForContext<HidEnumerator>();

    private const uint UsagePageGenericDesktop = 0x0001;
    private const uint UsageGamepad = 0x0005;
    private const uint UsageJoystick = 0x0004;

    private readonly KnownDeviceDatabase _knownDevices;

    public HidEnumerator(KnownDeviceDatabase knownDevices)
    {
        _knownDevices = knownDevices;
    }

    public IReadOnlyList<HidDeviceInfo> EnumerateGameControllers()
    {
        var results = new List<HidDeviceInfo>();
        var seenPaths = new HashSet<string>();
        var knownVids = _knownDevices.GetKnownVids();

        try
        {
            var hidDevices = DeviceList.Local.GetHidDevices();

            foreach (var device in hidDevices)
            {
                try
                {
                    bool isStandardGamepad = IsGameController(device);
                    bool isKnownVendor = knownVids.Contains(device.VendorID.ToString("X4"));

                    if (!isStandardGamepad && !isKnownVendor)
                        continue;

                    // Avoid duplicates (same device can appear on multiple HID interfaces)
                    if (!seenPaths.Add(device.DevicePath))
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

                    if (!isStandardGamepad && isKnownVendor)
                    {
                        Logger.Debug("Included known-VID device {Vid}:{Pid} ({Name}) via vendor fallback",
                            device.VendorID.ToString("X4"), device.ProductID.ToString("X4"), productName);
                    }
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

        Logger.Information("Discovered {Count} HID game controllers ({KnownVids} known VIDs tracked)",
            results.Count, knownVids.Count);
        return results.AsReadOnly();
    }

    internal static bool IsGameController(HidDevice device)
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
