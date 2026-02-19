using System.Diagnostics;
using Vortice.XInput;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Polls XInput slots 0–3 via Vortice.XInput and returns live state.
/// Can be called from any thread.
/// </summary>
/// <remarks>
/// API notes (Vortice.XInput 3.8.2):
/// - GetCapabilities / GetBatteryInformation both return bool (true = success / connected).
/// - userIndex is uint, not int.
/// - BatteryType.Nimh (not NiMH) is the NiMH enum value.
/// - DeviceType enum only has one value (Gamepad = 1); SubType carries the finer
///   distinction (Gamepad, Wheel, etc.) but we don't surface that in v1.
/// </remarks>
public sealed class XInputEnumerator : IXInputEnumerator
{
    public IReadOnlyList<XInputSlotInfo> GetSlots()
    {
        var results = new XInputSlotInfo[4];
        for (int i = 0; i < 4; i++)
            results[i] = QuerySlot(i);
        return results;
    }

    private static XInputSlotInfo QuerySlot(int slotIndex)
    {
        var userIndex = (uint)slotIndex;

        // GetCapabilities returns true when a controller is connected at this slot.
        bool isConnected = XInput.GetCapabilities(userIndex, DeviceQueryType.Any, out Capabilities caps);

        if (!isConnected)
        {
            return new XInputSlotInfo(
                slotIndex,
                IsConnected: false,
                XInputDeviceType.Unknown,
                XInputConnectionType.Wired,
                BatteryPercent: null);
        }

        // DeviceType enum only has Gamepad = 1 in this library version.
        // Everything else falls through to Unknown.
        var deviceType = caps.Type == DeviceType.Gamepad
            ? XInputDeviceType.Gamepad
            : XInputDeviceType.Unknown;

        // GetBatteryInformation returns true when information is available.
        // BatteryType.Wired (1) and BatteryType.Disconnected (0) / Unknown (255)
        // indicate no battery; Alkaline (2) and Nimh (3) indicate wireless.
        bool hasBattery = XInput.GetBatteryInformation(
            userIndex, BatteryDeviceType.Gamepad, out BatteryInformation battery);

        Debug.WriteLine($"[XInput] Slot{slotIndex}: hasBattery={hasBattery} " +
                        $"type={(int)battery.BatteryType}({battery.BatteryType}) " +
                        $"level={(int)battery.BatteryLevel}({battery.BatteryLevel})");

        bool isWireless = hasBattery
            && battery.BatteryType != BatteryType.Wired
            && battery.BatteryType != BatteryType.Disconnected
            && battery.BatteryType != BatteryType.Unknown;

        int? batteryPercent = null;
        if (isWireless)
        {
            // DECISION: XInput reports 4 discrete battery levels. We map them to
            // representative percentages so the UI can display a meaningful value
            // without implying false precision.
            batteryPercent = battery.BatteryLevel switch
            {
                BatteryLevel.Full   => 100,
                BatteryLevel.Medium => 60,
                BatteryLevel.Low    => 20,
                BatteryLevel.Empty  => 0,
                _                   => null,
            };
        }

        return new XInputSlotInfo(
            slotIndex,
            IsConnected: true,
            deviceType,
            isWireless ? XInputConnectionType.Wireless : XInputConnectionType.Wired,
            batteryPercent);
    }
}
