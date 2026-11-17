using System.Runtime.InteropServices;

namespace DotLight;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint MOD_ALT = 0x1, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    // ---- cursor position (to open when it touches the top edge) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    // ---- paste into the app you came from ----
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public static void SendCtrlV()
    {
        const byte VK_CONTROL = 0x11, VK_V = 0x56;
        const uint KEYUP = 0x2;
        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        keybd_event(VK_V, 0, 0, IntPtr.Zero);
        keybd_event(VK_V, 0, KEYUP, IntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, IntPtr.Zero);
    }

    // ---- hardware glass window ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    public const int DWMWA_BORDER_COLOR = 34, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // ---- clickable area of the window ----
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    // ---- is a fullscreen app (game, video, presentation) in front? ----
    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;

    /// <summary>
    /// True when the window in front fills the whole main screen with no title bar (a game, a
    /// fullscreen video, a slideshow), or Windows itself reports a fullscreen game/presentation.
    /// A normally maximized window doesn't count: it leaves the taskbar visible and has a title bar.
    /// </summary>
    public static bool FullscreenAppInFront(IntPtr self)
    {
        if (SHQueryUserNotificationState(out var quns) == 0 && (quns == 3 || quns == 4))
            return true; // 3 = Direct3D fullscreen game, 4 = presentation mode

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // the desktop / taskbar

        if (!GetWindowRect(fg, out var r)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2 /* nearest */), ref info)) return false;
        if ((info.dwFlags & 1) == 0) return false; // fullscreen on another screen: the island isn't there

        var m = info.rcMonitor;
        bool coversScreen = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        bool hasTitleBar = (GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        return coversScreen && !hasTitleBar;
    }
}
