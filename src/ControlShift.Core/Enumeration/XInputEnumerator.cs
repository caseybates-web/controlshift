using Vortice.XInput;
using ControlShift.Core.Models;
using Serilog;

namespace ControlShift.Core.Enumeration;

/// <summary>
/// Enumerates XInput controllers across all 4 slots using Vortice.XInput.
/// </summary>
public sealed class XInputEnumerator : IXInputEnumerator
{
    private static readonly ILogger Logger = Log.ForContext<XInputEnumerator>();

    public IReadOnlyList<XInputSlot> EnumerateSlots()
    {
        var slots = new List<XInputSlot>(4);

        for (int i = 0; i < 4; i++)
        {
            try
            {
                bool connected = XInput.GetState(i, out _);

                if (connected)
                {
                    XInput.GetCapabilities(i, DeviceQueryType.Any, out Capabilities caps);

                    string? batteryType = null;
                    byte? batteryLevel = null;

                    if (XInput.GetBatteryInformation(i, BatteryDeviceType.Gamepad,
                        out BatteryInformation battery))
                    {
                        batteryType = battery.BatteryType.ToString();
                        batteryLevel = (byte)battery.BatteryLevel;
                    }

                    slots.Add(new XInputSlot
                    {
                        SlotIndex = i,
                        IsConnected = true,
                        DeviceType = caps.Type.ToString(),
                        DeviceSubType = caps.SubType.ToString(),
                        BatteryLevel = batteryLevel,
                        BatteryType = batteryType
                    });
                }
                else
                {
                    slots.Add(new XInputSlot
                    {
                        SlotIndex = i,
                        IsConnected = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to query XInput slot {Slot}", i);
                slots.Add(new XInputSlot
                {
                    SlotIndex = i,
                    IsConnected = false
                });
            }
        }

        return slots.AsReadOnly();
    }
}
