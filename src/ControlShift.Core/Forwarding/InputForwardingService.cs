using System.Diagnostics;
using Vortice.XInput;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// Runs a Stopwatch-based 125Hz loop that reads XInput state from physical slots 0–3
/// and forwards to virtual ViGEm controllers according to a configurable slot map.
/// </summary>
public sealed class InputForwardingService : IDisposable
{
    private readonly ViGEmControllerPool _pool;
    private readonly int[] _slotMap = { 0, 1, 2, 3 }; // slotMap[physical] = virtual
    private readonly object _slotMapLock = new();
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Target loop interval in milliseconds.</summary>
    private const double TargetIntervalMs = 8.0; // ~125Hz

    public InputForwardingService(ViGEmControllerPool pool)
    {
        _pool = pool;
    }

    /// <summary>
    /// Updates the slot mapping. slotMap[physicalSlot] = virtualSlot.
    /// Thread-safe — takes effect on the next forwarding cycle.
    /// </summary>
    public void SetSlotMap(int[] newMap)
    {
        if (newMap.Length != 4) throw new ArgumentException("Slot map must have exactly 4 entries.");
        lock (_slotMapLock)
        {
            Array.Copy(newMap, _slotMap, 4);
        }
        Debug.WriteLine($"[InputForwarding] SlotMap updated: " +
                        $"phys0→virt{newMap[0]}, phys1→virt{newMap[1]}, " +
                        $"phys2→virt{newMap[2]}, phys3→virt{newMap[3]}");
    }

    /// <summary>Gets a copy of the current slot map.</summary>
    public int[] GetSlotMap()
    {
        lock (_slotMapLock)
        {
            return (int[])_slotMap.Clone();
        }
    }

    /// <summary>Starts the forwarding loop on a background thread.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(ForwardingLoop)
        {
            Name = "ControlShift-Forwarding",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    /// <summary>Stops the forwarding loop and waits for the thread to exit.</summary>
    public void Stop()
    {
        _running = false;
        _thread?.Join(timeout: TimeSpan.FromSeconds(2));
        _thread = null;
    }

    /// <summary>True if the forwarding loop is running.</summary>
    public bool IsRunning => _running;

    private void ForwardingLoop()
    {
        var sw = Stopwatch.StartNew();

        while (_running)
        {
            long tickStart = sw.ElapsedTicks;

            // Snapshot the slot map for this cycle
            int v0, v1, v2, v3;
            lock (_slotMapLock)
            {
                v0 = _slotMap[0];
                v1 = _slotMap[1];
                v2 = _slotMap[2];
                v3 = _slotMap[3];
            }

            try
            {
                // Read all 4 physical slots
                XInput.GetState(0, out State s0);
                XInput.GetState(1, out State s1);
                XInput.GetState(2, out State s2);
                XInput.GetState(3, out State s3);

                // Forward to virtual controllers according to slot map
                ViGEmControllerPool.Forward(_pool[v0], s0.Gamepad);
                ViGEmControllerPool.Forward(_pool[v1], s1.Gamepad);
                ViGEmControllerPool.Forward(_pool[v2], s2.Gamepad);
                ViGEmControllerPool.Forward(_pool[v3], s3.Gamepad);
            }
            catch
            {
                // Forwarding errors must not kill the loop — log and continue
            }

            // Stopwatch-based sleep: spin-wait for precise timing instead of Thread.Sleep
            double elapsedMs = (sw.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency;
            double remainingMs = TargetIntervalMs - elapsedMs;

            if (remainingMs > 2.0)
            {
                // Sleep for the bulk of the remaining time, then spin for precision
                Thread.Sleep((int)(remainingMs - 1.5));
            }

            // Spin-wait for the final sub-millisecond precision
            while ((sw.ElapsedTicks - tickStart) * 1000.0 / Stopwatch.Frequency < TargetIntervalMs)
            {
                Thread.SpinWait(10);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
