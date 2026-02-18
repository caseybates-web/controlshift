using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.Core.Devices;

/// <summary>
/// Matches XInput slots to HID devices using the known-devices database.
/// Produces a unified ControllerInfo list for the UI layer.
/// </summary>
public sealed class DeviceFingerprinter : IDeviceFingerprinter
{
    private static readonly ILogger Logger = Log.ForContext<DeviceFingerprinter>();
    private readonly KnownDeviceDatabase _knownDevices;

    public DeviceFingerprinter(KnownDeviceDatabase knownDevices)
    {
        _knownDevices = knownDevices;
    }

    public IReadOnlyList<ControllerInfo> IdentifyControllers(
        IReadOnlyList<XInputSlot> xinputSlots,
        IReadOnlyList<HidDeviceInfo> hidDevices)
    {
        var controllers = new List<ControllerInfo>();
        var usedDevicePaths = new HashSet<string>();

        foreach (var slot in xinputSlots)
        {
            if (!slot.IsConnected)
            {
                controllers.Add(new ControllerInfo
                {
                    SlotIndex = slot.SlotIndex,
                    IsConnected = false,
                    ConnectionType = ConnectionType.Unknown
                });
                continue;
            }

            // DECISION: XInput does not expose VID/PID. We match XInput slots to HID
            // devices using a heuristic approach:
            // 1. Known integrated gamepads (from known-devices.json) get priority at slot 0
            // 2. Remaining HID devices are matched to slots by order of discovery
            // This is imperfect and will be refined with real hardware testing.
            var matchedHid = TryMatchHidToSlot(slot, hidDevices, usedDevicePaths);

            if (matchedHid is not null)
                usedDevicePaths.Add(matchedHid.DevicePath);

            var knownDevice = matchedHid is not null
                ? _knownDevices.Lookup(matchedHid.Vid, matchedHid.Pid)
                : null;

            bool isIntegrated = knownDevice is not null;
            var connectionType = DetermineConnectionType(slot, isIntegrated);
            string displayName = knownDevice?.Name
                ?? matchedHid?.ProductName
                ?? $"Controller (Slot {slot.SlotIndex})";

            controllers.Add(new ControllerInfo
            {
                SlotIndex = slot.SlotIndex,
                IsConnected = true,
                DisplayName = displayName,
                ConnectionType = connectionType,
                IsIntegratedGamepad = isIntegrated,
                BatteryLevel = slot.BatteryLevel,
                BatteryType = slot.BatteryType,
                DevicePath = matchedHid?.DevicePath,
                Vid = matchedHid?.Vid,
                Pid = matchedHid?.Pid
            });
        }

        Logger.Information("Identified {Total} slots, {Connected} connected",
            controllers.Count,
            controllers.Count(c => c.IsConnected));

        return controllers.AsReadOnly();
    }

    private HidDeviceInfo? TryMatchHidToSlot(
        XInputSlot slot,
        IReadOnlyList<HidDeviceInfo> hidDevices,
        HashSet<string> usedPaths)
    {
        var available = hidDevices
            .Where(h => !usedPaths.Contains(h.DevicePath))
            .ToList();

        if (available.Count == 0)
            return null;

        // For slot 0, prefer matching a known integrated gamepad first
        if (slot.SlotIndex == 0)
        {
            var integrated = available.FirstOrDefault(h =>
                _knownDevices.IsKnownIntegratedGamepad(h.Vid, h.Pid));
            if (integrated is not null)
                return integrated;
        }

        // DECISION: Fallback to first available unmatched HID device.
        // This naive matching will be replaced with better heuristics
        // once real hardware testing validates the approach.
        return available.FirstOrDefault();
    }

    private static ConnectionType DetermineConnectionType(XInputSlot slot, bool isIntegrated)
    {
        if (isIntegrated)
            return ConnectionType.Integrated;

        // DECISION: Battery type "Wired" indicates USB connection.
        // Wireless battery types (Alkaline, NiMH, Unknown) suggest Bluetooth.
        // This heuristic works for most consumer controllers.
        return slot.BatteryType switch
        {
            "Wired" => ConnectionType.Usb,
            "Alkaline" or "NiMH" or "Unknown" => ConnectionType.Bluetooth,
            _ => ConnectionType.Unknown
        };
    }
}
