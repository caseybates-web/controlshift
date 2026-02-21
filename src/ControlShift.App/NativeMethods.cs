using System.Runtime.InteropServices;

namespace ControlShift.App;

/// <summary>
/// Shared P/Invoke declarations used by multiple windows.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);
}
