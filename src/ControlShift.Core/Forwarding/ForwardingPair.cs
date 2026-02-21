using System.Runtime.InteropServices;
using ControlShift.Core.Devices;
using ControlShift.Core.Diagnostics;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// One physical→virtual forwarding channel: reads XInput state from a physical slot
/// and submits it to a ViGEm virtual controller at ~250Hz on a dedicated thread.
/// </summary>
/// <remarks>
/// Uses XInputGetStateEx (ordinal #100) to include the Guide button (0x0400)
/// which standard XInputGetState masks out. This is the same undocumented API
/// used by Steam, DS4Windows, and other controller remappers.
/// </remarks>
internal sealed class ForwardingPair : IDisposable
{
    private readonly int _physicalSlot;
    private volatile int _targetSlot;
    private readonly string _devicePath;
    private volatile IViGEmController _vigem;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<ForwardingErrorEventArgs>? _onError;
    private Thread? _thread;
    private bool _disposed;

    /// <summary>The physical XInput slot this pair reads from (immutable).</summary>
    public int PhysicalSlot => _physicalSlot;

    // XInputGetStateEx — undocumented ordinal #100 in xinput1_4.dll.
    // Returns Guide button (0x0400) which standard XInputGetState masks out.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern int XInputGetStateEx(uint userIndex, out XInputStateEx state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputStateEx
    {
        public uint PacketNumber;
        public ushort Buttons;     // includes Guide (0x0400)
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    /// <summary>
    /// Creates a forwarding pair that reads XInput from a physical slot.
    /// </summary>
    public ForwardingPair(
        int physicalSlot,
        int targetSlot,
        string devicePath,
        IViGEmController vigem,
        Action<ForwardingErrorEventArgs>? onError = null)
    {
        _physicalSlot = physicalSlot;
        _targetSlot = targetSlot;
        _devicePath = devicePath;
        _vigem      = vigem;
        _onError    = onError;
    }

    /// <summary>Starts the forwarding thread.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _thread = new Thread(RunLoop)
        {
            Name = $"Forwarding-P{_targetSlot + 1}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Stops the forwarding thread.
    /// Does NOT disconnect the ViGEm controller — that lifecycle is owned by
    /// InputForwardingService's persistent pool.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _thread?.Join(millisecondsTimeout: 500);
    }

    /// <summary>
    /// Hot-swaps the ViGEm target without stopping the forwarding thread.
    /// The running RunLoop will pick up the new controller on its next iteration.
    /// </summary>
    public void SwapTarget(int newTargetSlot, IViGEmController newVigem)
    {
        _targetSlot = newTargetSlot;
        _vigem = newVigem;
        DebugLog.Log($"[Forward] physSlot={_physicalSlot} remapped → targetSlot={newTargetSlot}");
        InputTrace.Log($"[Forward] physSlot={_physicalSlot} remapped → targetSlot={newTargetSlot}");
    }

    private void RunLoop()
    {
        ushort prevButtons = 0;
        uint prevPacket = 0;

        DebugLog.Log($"[Forward] slot={_targetSlot} started — reading XInput physical slot {_physicalSlot}");
        InputTrace.Log($"[Forward] slot={_targetSlot} started — physSlot={_physicalSlot} path={_devicePath}");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int rc = XInputGetStateEx((uint)_physicalSlot, out XInputStateEx state);

                if (rc == 0) // ERROR_SUCCESS
                {
                    // Skip if no state change (same packet number).
                    if (state.PacketNumber == prevPacket)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    prevPacket = state.PacketNumber;

                    var report = new GamepadReport(
                        Buttons:      state.Buttons,
                        LeftTrigger:  state.LeftTrigger,
                        RightTrigger: state.RightTrigger,
                        ThumbLX:      state.ThumbLX,
                        ThumbLY:      state.ThumbLY,
                        ThumbRX:      state.ThumbRX,
                        ThumbRY:      state.ThumbRY);

                    // Log button changes for diagnostics.
                    if (report.Buttons != prevButtons)
                    {
                        ushort changed = (ushort)(report.Buttons ^ prevButtons);
                        DebugLog.Log($"[Forward] slot={_targetSlot} buttons=0x{report.Buttons:X4} (was 0x{prevButtons:X4})");
                        InputTrace.Log($"[Forward] slot={_targetSlot} physSlot={_physicalSlot} buttons=0x{report.Buttons:X4} was=0x{prevButtons:X4} changed=0x{changed:X4} [{InputTrace.ButtonNames(changed)}]");
                        prevButtons = report.Buttons;
                    }

                    _vigem.SubmitReport(report);
                }
                else if (rc == 1167) // ERROR_DEVICE_NOT_CONNECTED
                {
                    _onError?.Invoke(new ForwardingErrorEventArgs(
                        _targetSlot, _devicePath, "Physical controller disconnected"));
                    break;
                }

                Thread.Sleep(4); // ~250Hz
            }
            catch (Exception ex)
            {
                _onError?.Invoke(new ForwardingErrorEventArgs(
                    _targetSlot, _devicePath, "Forwarding error", ex));
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
        // ViGEm controller is NOT disposed here — it's owned by the persistent pool
        // in InputForwardingService and reused across stop/start cycles.
    }
}
