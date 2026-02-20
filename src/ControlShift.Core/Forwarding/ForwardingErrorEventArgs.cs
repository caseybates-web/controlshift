namespace ControlShift.Core.Forwarding;

/// <summary>
/// Event args raised when a forwarding channel encounters an error (e.g. device disconnect).
/// </summary>
public sealed class ForwardingErrorEventArgs : EventArgs
{
    public int TargetSlot { get; }
    public string DevicePath { get; }
    public string ErrorMessage { get; }
    public Exception? InnerException { get; }

    public ForwardingErrorEventArgs(
        int targetSlot, string devicePath, string errorMessage, Exception? innerException = null)
    {
        TargetSlot     = targetSlot;
        DevicePath     = devicePath;
        ErrorMessage   = errorMessage;
        InnerException = innerException;
    }
}
