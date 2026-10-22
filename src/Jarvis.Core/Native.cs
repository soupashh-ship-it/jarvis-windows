using System.Runtime.InteropServices;
using System.Text;

namespace Jarvis.Core;

public static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3,
        SW_RESTORE = 9, SW_SHOW = 5;

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_GETICON = 0x007F;

    public const int DWMWA_CLOAKED = 14;

    public const int MDT_EFFECTIVE_DPI = 0;

    public const uint MONITORINFOF_PRIMARY = 0x1;

    public const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecute(IntPtr hwnd, string? verb, string file, string? parameters, string? directory, int show);

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int max);

    [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
}

/// <summary>Synthetic keyboard and mouse input that works in any Windows app.</summary>
public static class InputApi
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_RIGHTDOWN = 0x0008,
        MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x01000, MOUSEEVENTF_MOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint msg; public ushort wParamL; public ushort wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private static uint Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    public static void MoveTo(int x, int y) => Native.SetCursorPos(x, y);

    public static (int X, int Y) Cursor()
    {
        Native.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    public static void Click(string button = "left", int clicks = 1, int delayMs = 60)
    {
        for (var i = 0; i < Math.Clamp(clicks, 1, 3); i++)
        {
            Send(new[] { Mouse(MouseFlags(button, true)), Mouse(MouseFlags(button, false)) });
            if (i < clicks - 1) Thread.Sleep(delayMs);
        }
    }

    private static uint MouseFlags(string button, bool down) => button.ToLowerInvariant() switch
    {
        "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
    };

    private static INPUT Mouse(uint flags, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new INPUTUNION { mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = (uint)data, dwFlags = flags } }
    };

    public static void Scroll(int notches, bool horizontal = false)
    {
        var delta = Math.Clamp(notches, -40, 40) * 120;
        Send(new[] { Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, delta) });
    }

    /// <summary>Types literal text via Unicode key events, so any layout and any character works.</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var scan = ch;
            Send(new[]
            {
                new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { scan = scan, flags = KEYEVENTF_UNICODE } } },
                new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { scan = scan, flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } }
            });
            Thread.Sleep(1);
        }
    }

    public static bool KeyDown(ushort vk) => Send(new[]
        { new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { vk = vk, scan = 0, flags = Extended(vk) } } } }) == 1;

    public static bool KeyUp(ushort vk) => Send(new[]
        { new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { vk = vk, scan = 0, flags = KEYEVENTF_KEYUP | Extended(vk) } } } }) == 1;

    private static uint Extended(ushort vk) =>
        vk is 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x90 or 0x91 or 0xA3 or 0xA5 or 0x5B or 0x5C
            ? KEYEVENTF_EXTENDEDKEY : 0;

    /// <summary>Press a combination like "ctrl+shift+esc". Returns the names it did not recognise.</summary>
    public static (bool Ok, List<string> Unknown) PressCombo(string combo)
    {
        var names = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant()).ToList();
        var unknown = new List<string>();
        var vks = new List<ushort>();
        foreach (var name in names)
        {
            if (KeyMap.TryGetValue(name, out var vk)) vks.Add(vk);
            else unknown.Add(name);
        }
        if (unknown.Count > 0) return (false, unknown);

        // Hold modifiers, tap the final key, release in reverse order.
        foreach (var vk in vks.Take(vks.Count - 1)) KeyDown(vk);
        KeyDown(vks[^1]);
        Thread.Sleep(18);
        KeyUp(vks[^1]);
        for (var i = vks.Count - 2; i >= 0; i--) KeyUp(vks[i]);
        return (true, unknown);
    }

    public static readonly Dictionary<string, ushort> KeyMap = BuildKeyMap();

    private static Dictionary<string, ushort> BuildKeyMap()
    {
        var m = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
            ["windows"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B,
            ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D,
            ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["pgup"] = 0x21,
            ["pgdn"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["printscreen"] = 0x2C,
            ["pause"] = 0x13, ["apps"] = 0x5D, ["sleep"] = 0x5F,
            ["comma"] = 0xBC, ["period"] = 0xBE, ["dot"] = 0xBE, ["slash"] = 0xBF, ["backslash"] = 0xDC,
            ["semicolon"] = 0xBA, ["quote"] = 0xDE, ["apostrophe"] = 0xDE, ["minus"] = 0xBD, ["equal"] = 0xBB,
            ["grave"] = 0xC0, ["backtick"] = 0xC0, ["leftbracket"] = 0xDB, ["rightbracket"] = 0xDD,
            ["playpause"] = 0xB3, ["next"] = 0xB0, ["previous"] = 0xB1, ["prev"] = 0xB1, ["stopmedia"] = 0xB2,
            ["mute"] = 0xAD, ["volumeup"] = 0xAF, ["volumedown"] = 0xAE,
            ["browserback"] = 0xA6, ["browserforward"] = 0xA7, ["refresh"] = 0xA8,
            ["media"] = 0xB3
        };
        for (var c = 'a'; c <= 'z'; c++) m[c.ToString()] = (ushort)(0x41 + (c - 'a'));
        for (var c = '0'; c <= '9'; c++) m[c.ToString()] = (ushort)(0x30 + (c - '0'));
        for (var i = 1; i <= 24; i++) m[$"f{i}"] = i <= 12 ? (ushort)(0x6F + i) : (ushort)(0x87 + (i - 13));
        return m;
    }
}
