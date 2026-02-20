using System.Buffers.Binary;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Parses raw HID reports from Xbox-compatible controllers into <see cref="GamepadReport"/>.
/// </summary>
/// <remarks>
/// DECISION: Fixed-offset parser for the standard Xbox 360 wired controller HID report
/// layout. XInput devices exposed via the IG_0N marker use the xinput driver, which
/// normalizes reports to this format. Non-standard controllers (DualSense raw mode,
/// Switch Pro raw mode) would need per-device parsers — out of scope for v1.
///
/// Standard Xbox 360 HID report layout (20 bytes):
///   [0]    Report ID (0x00)
///   [1]    Report size (0x14 = 20)
///   [2-3]  Buttons (LE uint16 bitmask)
///   [4]    Left trigger  (0–255)
///   [5]    Right trigger (0–255)
///   [6-7]  Left  thumbstick X (LE int16)
///   [8-9]  Left  thumbstick Y (LE int16)
///   [10-11] Right thumbstick X (LE int16)
///   [12-13] Right thumbstick Y (LE int16)
///   [14-19] Reserved / padding
/// </remarks>
public static class HidReportParser
{
    /// <summary>Minimum number of bytes for a valid Xbox 360 HID report.</summary>
    public const int MinReportLength = 14;

    /// <summary>
    /// Parses a raw HID report byte span into a <see cref="GamepadReport"/>.
    /// Returns a zeroed report if the span is too short.
    /// </summary>
    public static GamepadReport Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < MinReportLength)
            return default;

        return new GamepadReport(
            Buttons:      BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]),
            LeftTrigger:  raw[4],
            RightTrigger: raw[5],
            ThumbLX:      BinaryPrimitives.ReadInt16LittleEndian(raw[6..]),
            ThumbLY:      BinaryPrimitives.ReadInt16LittleEndian(raw[8..]),
            ThumbRX:      BinaryPrimitives.ReadInt16LittleEndian(raw[10..]),
            ThumbRY:      BinaryPrimitives.ReadInt16LittleEndian(raw[12..]));
    }
}
