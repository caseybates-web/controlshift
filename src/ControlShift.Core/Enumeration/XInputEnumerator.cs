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

        for (uint i = 0; i < 4; i++)
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
                        batteryType = NormalizeBatteryType(battery.BatteryType);
                        // Wired controllers report BatteryLevel=Full which is misleading
                        batteryLevel = battery.BatteryType == Vortice.XInput.BatteryType.Wired
                            ? null
                            : (byte)battery.BatteryLevel;
                    }

                    slots.Add(new XInputSlot
                    {
                        SlotIndex = (int)i,
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
                        SlotIndex = (int)i,
                        IsConnected = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to query XInput slot {Slot}", i);
                slots.Add(new XInputSlot
                {
                    SlotIndex = (int)i,
                    IsConnected = false
                });
            }
        }

        return slots.AsReadOnly();
    }

    /// <summary>
    /// Normalize Vortice BatteryType enum ToString() to canonical casing.
    /// Vortice outputs "Nimh" but we use "NiMH" throughout the codebase.
    /// </summary>
    internal static string NormalizeBatteryType(Vortice.XInput.BatteryType type) => type switch
    {
        Vortice.XInput.BatteryType.Wired => "Wired",
        Vortice.XInput.BatteryType.Alkaline => "Alkaline",
        Vortice.XInput.BatteryType.Nimh => "NiMH",
        _ => "Unknown"
    };
}
