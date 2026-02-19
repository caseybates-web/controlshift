using System.Runtime.InteropServices;

namespace ControlShift.App.Tray;

/// <summary>
/// P/Invoke declarations for Shell_NotifyIcon (tray icon), window subclassing
/// (WM_DEVICECHANGE interception), and DPI helpers.
/// No logic lives here — only signatures, structs, and constants.
/// </summary>
internal static class NativeMethods
{
    // ── Shell_NotifyIcon commands ─────────────────────────────────────────────
    internal const uint NIM_ADD    = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;

    // ── NOTIFYICONDATA flags ──────────────────────────────────────────────────
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON    = 0x00000002;
    internal const uint NIF_TIP     = 0x00000004;
    internal const uint NIF_SHOWTIP = 0x00000080;

    // ── Tray notification events (arrive in lParam of the callback message) ───
    internal const uint NIN_SELECT    = 0x0400; // left-click / keyboard Enter
    internal const uint NIN_KEYSELECT = 0x0401; // keyboard-initiated selection

    // ── Window messages ───────────────────────────────────────────────────────
    internal const uint WM_DEVICECHANGE   = 0x0219;
    internal const uint WM_APP_TRAY       = 0x8001; // WM_APP + 1, our callback msg

    // ── WM_DEVICECHANGE event codes (arrive in wParam) ────────────────────────
    internal const int DBT_DEVNODES_CHANGED = 0x0007; // broadcast, no registration needed

    // ── LoadImage constants ───────────────────────────────────────────────────
    internal const uint IMAGE_ICON      = 1;
    internal const uint LR_DEFAULTSIZE  = 0x00000040;
    internal const uint LR_SHARED       = 0x00008000;
    internal const int  IDI_APPLICATION = 32512;

    // ── NOTIFYICONDATA (Windows Vista+ full struct) ───────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint    cbSize;
        public IntPtr  hWnd;
        public uint    uID;
        public uint    uFlags;
        public uint    uCallbackMessage;
        public IntPtr  hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string  szTip;
        public uint    dwState;
        public uint    dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string  szInfo;
        public uint    uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string  szInfoTitle;
        public uint    dwInfoFlags;
        public Guid    guidItem;
        public IntPtr  hBalloonIcon;
    }

    // ── SetWindowSubclass callback signature ──────────────────────────────────
    // IMPORTANT: the delegate instance must be rooted in a field for the lifetime
    // of the subclass. If it is GC'd, the next WndProc call will crash.
    internal delegate IntPtr SUBCLASSPROC(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        IntPtr uIdSubclass, IntPtr dwRefData);

    // ── P/Invoke ──────────────────────────────────────────────────────────────
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadImage(
        IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    // comctl32 v6 — active for WinUI 3 apps via the Windows App SDK manifest.
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(
        IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
