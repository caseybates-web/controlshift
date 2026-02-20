using System.Diagnostics;
using System.Runtime.InteropServices;
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
///
/// Wireless detection: <see cref="IsWirelessViaCaps"/> uses a raw P/Invoke on
/// xinput1_4.dll to read XINPUT_CAPABILITIES.Flags bit 0x0002 (XINPUT_CAPS_WIRELESS).
/// This is more reliable than BatteryInformation.BatteryType, which reports
/// BatteryType.Wired for BLE Xbox controllers despite them being wireless.
/// </remarks>
public sealed class XInputEnumerator : IXInputEnumerator
{
    // ── Raw XINPUT_CAPABILITIES P/Invoke ──────────────────────────────────────
    // Vortice.XInput 3.8.2 does not expose XINPUT_CAPABILITIES.Flags directly.
    // We declare our own struct + import to access the XINPUT_CAPS_WIRELESS flag.

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct XINPUT_CAPABILITIES
    {
        public byte   Type;
        public byte   SubType;
        public ushort Flags;
        // XINPUT_GAMEPAD (12 bytes)
        public ushort Buttons;
        public byte   LeftTrigger;
        public byte   RightTrigger;
        public short  ThumbLX;
        public short  ThumbLY;
        public short  ThumbRX;
        public short  ThumbRY;
        // XINPUT_VIBRATION (4 bytes)
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    private const ushort XINPUT_CAPS_WIRELESS = 0x0002;
    private const uint   XINPUT_FLAG_GAMEPAD  = 1;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetCapabilities")]
    private static extern uint RawGetCapabilities(
        uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES pCapabilities);

    /// <summary>
    /// Returns true when the XINPUT_CAPS_WIRELESS flag is set for the given slot.
    /// This is the reliable wireless indicator — BatteryInformation.BatteryType
    /// incorrectly reports Wired for BLE Xbox controllers.
    /// </summary>
    private static bool IsWirelessViaCaps(int slotIndex)
    {
        try
        {
            uint result = RawGetCapabilities((uint)slotIndex, XINPUT_FLAG_GAMEPAD, out var caps);
            return result == 0 && (caps.Flags & XINPUT_CAPS_WIRELESS) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ── IXInputEnumerator ─────────────────────────────────────────────────────

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

        // DECISION: Ghost ViGEm device nodes (from previous sessions) can cause
        // GetCapabilities to report a slot as connected even though no physical
        // controller is present. Cross-check with GetState — if the device can't
        // produce input state, it's a ghost and should be treated as disconnected.
        if (isConnected)
        {
            bool hasState = XInput.GetState(userIndex, out _);
            if (!hasState)
            {
                Debug.WriteLine($"[XInput] Slot{slotIndex}: GetCapabilities=connected but " +
                                "GetState=ERROR_DEVICE_NOT_CONNECTED — treating as ghost/disconnected");
                isConnected = false;
            }
        }

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

        // DECISION: Use XINPUT_CAPS_WIRELESS flag (bit 0x0002 in XINPUT_CAPABILITIES.Flags)
        // as the authoritative wireless indicator. BatteryInformation.BatteryType is NOT
        // reliable — Xbox BLE controllers report BatteryType.Wired even when wireless.
        bool isWireless = IsWirelessViaCaps(slotIndex);

        // Battery query: still needed for level display on truly wireless controllers.
        bool hasBattery = XInput.GetBatteryInformation(
            userIndex, BatteryDeviceType.Gamepad, out BatteryInformation battery);

        Debug.WriteLine($"[XInput] Slot{slotIndex}: isWireless={isWireless} hasBattery={hasBattery} " +
                        $"type={(int)battery.BatteryType}({battery.BatteryType}) " +
                        $"level={(int)battery.BatteryLevel}({battery.BatteryLevel})");

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
