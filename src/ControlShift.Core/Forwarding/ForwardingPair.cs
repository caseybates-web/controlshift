using HidSharp;
using ControlShift.Core.Devices;

namespace ControlShift.Core.Forwarding;

/// <summary>
/// One physical→virtual forwarding channel: reads HID reports from a physical device
/// and submits them to a ViGEm virtual controller at ~125Hz on a dedicated thread.
/// </summary>
internal sealed class ForwardingPair : IDisposable
{
    private readonly int _targetSlot;
    private readonly string _devicePath;
    private readonly IViGEmController _vigem;
    private readonly HidStream _hidStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<ForwardingErrorEventArgs>? _onError;
    private Thread? _thread;
    private bool _disposed;

    /// <summary>
    /// Creates a forwarding pair. The caller must have already opened the HidStream.
    /// </summary>
    public ForwardingPair(
        int targetSlot,
        string devicePath,
        IViGEmController vigem,
        HidStream hidStream,
        Action<ForwardingErrorEventArgs>? onError = null)
    {
        _targetSlot = targetSlot;
        _devicePath = devicePath;
        _vigem      = vigem;
        _hidStream  = hidStream;
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

    /// <summary>Stops the forwarding thread and closes the HID stream.
    /// Does NOT disconnect the ViGEm controller — that lifecycle is owned by
    /// InputForwardingService's persistent pool.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _thread?.Join(millisecondsTimeout: 500);

        try { _hidStream.Close(); }
        catch { /* best effort */ }
    }

    private void RunLoop()
    {
        // DECISION: HidStream.ReadTimeout = 8ms gives ~125Hz cadence.
        // If the device polls faster (e.g. 1000Hz), we read at device rate.
        // If slower (e.g. Bluetooth 100Hz), we read at device rate.
        // The 8ms timeout prevents blocking forever on a disconnected device.
        _hidStream.ReadTimeout = 8;

        byte[] buffer = new byte[64];

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int bytesRead = _hidStream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var report = HidReportParser.Parse(buffer.AsSpan(0, bytesRead));
                    _vigem.SubmitReport(report);
                }
            }
            catch (TimeoutException)
            {
                // Normal — device didn't have a report ready within 8ms.
                continue;
            }
            catch (IOException ex)
            {
                // Device disconnected.
                _onError?.Invoke(new ForwardingErrorEventArgs(
                    _targetSlot, _devicePath, "Device disconnected", ex));
                break;
            }
            catch (ObjectDisposedException)
            {
                // Stream was closed (shutdown in progress).
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
