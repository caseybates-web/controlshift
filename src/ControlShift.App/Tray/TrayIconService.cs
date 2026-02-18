using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using static ControlShift.App.Tray.NativeMethods;

namespace ControlShift.App.Tray;

/// <summary>
/// Owns the Shell_NotifyIcon tray icon and intercepts tray + WM_DEVICECHANGE
/// messages by installing a comctl32 window subclass on the popup window's HWND.
/// All events are marshalled back to the UI thread via <see cref="DispatcherQueue"/>.
/// </summary>
/// <remarks>
/// DECISION: We subclass the popup window's HWND (rather than creating a separate
/// Win32 message-only window) because WinUI 3 already owns that HWND and its
/// message loop. SetWindowSubclass lets us chain cleanly without replacing the
/// existing WndProc.
///
/// DECISION: The placeholder tray icon uses IDI_APPLICATION (the Windows default
/// app icon). A custom .ico asset should be added and loaded via LoadImage before
/// the v1 release.
/// </remarks>
internal sealed class TrayIconService : IDisposable
{
    private readonly IntPtr         _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly SUBCLASSPROC   _subclassProc; // must be a field — prevents GC while subclass is active
    private bool _disposed;

    /// <summary>Raised on the UI thread when the device tree changes (controller plugged/unplugged).</summary>
    public event Action? DeviceChanged;

    /// <summary>Raised on the UI thread when the user left-clicks or keyboard-activates the tray icon.</summary>
    public event Action? TrayIconActivated;

    public TrayIconService(IntPtr hwnd, DispatcherQueue dispatcher)
    {
        _hwnd       = hwnd;
        _dispatcher = dispatcher;

        // Store the delegate in a field BEFORE passing it to SetWindowSubclass.
        // The native side holds only a raw function pointer; the GC has no knowledge
        // of this reference. Without the field, the delegate may be collected on the
        // next GC cycle, causing an access violation on the next WndProc call.
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(_hwnd, _subclassProc, uIdSubclass: (IntPtr)1, dwRefData: IntPtr.Zero);

        AddTrayIcon();
    }

    // ── Shell_NotifyIcon ──────────────────────────────────────────────────────

    private void AddTrayIcon()
    {
        // TODO: replace IDI_APPLICATION with a custom ControlShift .ico asset.
        var hIcon = LoadImage(
            IntPtr.Zero, (IntPtr)IDI_APPLICATION,
            IMAGE_ICON, 0, 0, LR_DEFAULTSIZE | LR_SHARED);

        var nid = new NOTIFYICONDATA
        {
            hWnd            = _hwnd,
            uID             = 1,
            uFlags          = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon           = hIcon,
            szTip           = "ControlShift",
            szInfo          = string.Empty,
            szInfoTitle     = string.Empty,
        };
        nid.cbSize = (uint)Marshal.SizeOf(nid);

        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    // ── Window subclass WndProc ───────────────────────────────────────────────

    private IntPtr SubclassWndProc(
        IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        IntPtr uIdSubclass, IntPtr dwRefData)
    {
        switch (uMsg)
        {
            case WM_APP_TRAY:
            {
                // lParam low-word = notification event (version-0 Shell_NotifyIcon format).
                uint notification = (uint)(lParam.ToInt64() & 0xFFFF);
                if (notification is NIN_SELECT or NIN_KEYSELECT)
                    _dispatcher.TryEnqueue(() => TrayIconActivated?.Invoke());
                return IntPtr.Zero;
            }

            case WM_DEVICECHANGE when (int)wParam == DBT_DEVNODES_CHANGED:
                // DBT_DEVNODES_CHANGED is broadcast to all top-level windows
                // without needing RegisterDeviceNotification. This fires whenever
                // the device tree changes — sufficient for controller plug/unplug.
                _dispatcher.TryEnqueue(() => DeviceChanged?.Invoke());
                return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Remove the tray icon first so no more callbacks arrive.
        var nid = new NOTIFYICONDATA
        {
            hWnd  = _hwnd,
            uID   = 1,
            szTip = string.Empty, szInfo = string.Empty, szInfoTitle = string.Empty,
        };
        nid.cbSize = (uint)Marshal.SizeOf(nid);
        Shell_NotifyIcon(NIM_DELETE, ref nid);

        RemoveWindowSubclass(_hwnd, _subclassProc, uIdSubclass: (IntPtr)1);
    }
}
