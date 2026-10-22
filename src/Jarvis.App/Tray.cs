using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>A native notification-area icon with a dark WPF menu and balloon notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000 + 0x51;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const int NiifInfo = 0x1, NiifWarning = 0x2, NiifError = 0x3;
    private const int NotifyVersion4 = 4;

    private HwndSource? _source;
    private readonly int _id = 0x4A525649;
    private Icon? _icon;
    private IntPtr _iconHandle;
    private ContextMenu? _menu;
    private TextBlock? _pauseText;
    private bool _added;

    public event Action? OpenRequested;
    public event Action? TalkRequested;
    public event Action? StopRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action? PauseToggled;

    public bool Paused { get; set; }

    /// <summary>Keeps the menu honest: the same row pauses and resumes.</summary>
    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (_pauseText is not null) _pauseText.Text = paused ? "Resume listening" : "Pause listening";
    }

    public TrayIcon()
    {
        var parameters = new HwndSourceParameters("JarvisTraySink")
        {
            Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = 0, ParentWindow = (IntPtr)(-3)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "jarvis.ico");
            _icon = File.Exists(path) ? new Icon(path, new System.Drawing.Size(32, 32)) : SystemIcons.Application;
            _iconHandle = _icon.Handle;
        }
        catch
        {
            _icon = SystemIcons.Application;
            _iconHandle = _icon.Handle;
        }

        Add();
        BuildMenu();
    }

    private void Add()
    {
        if (_source is null) return;
        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.szTip = "JARVIS — online";
        data.uCallbackMessage = WmApp;
        data.hIcon = _iconHandle;
        if (!Shell_NotifyIcon(NimAdd, ref data)) return;
        _added = true;
        data.uTimeoutOrVersion = (uint)NotifyVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source?.Handle ?? IntPtr.Zero,
        uID = _id,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    public void SetTooltip(string text)
    {
        if (_source is null || !_added) return;
        var data = NewData();
        data.uFlags = NifTip | NifShowTip;
        data.szTip = text.Length > 120 ? text[..120] : text;
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Notify(string title, string body, bool warning = false)
    {
        if (_source is null || !_added) return;
        var data = NewData();
        data.uFlags = NifInfo;
        data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        data.szInfo = body.Length > 250 ? body[..250] : body;
        data.dwInfoFlags = (uint)(warning ? NiifWarning : NiifInfo);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void BuildMenu()
    {
        _menu = new ContextMenu();
        var open = Item("Open command deck", "\uE8A7");
        open.Click += (_, _) => OpenRequested?.Invoke();
        var talk = Item("Talk to JARVIS", "\uE720");
        talk.Click += (_, _) => TalkRequested?.Invoke();
        var stop = Item("Stop", "\uE71A");
        stop.Click += (_, _) => StopRequested?.Invoke();
        var pause = Item("Pause listening", "\uE769");
        _pauseText = ((StackPanel)pause.Header).Children.OfType<TextBlock>().LastOrDefault();
        pause.Click += (_, _) => PauseToggled?.Invoke();
        var settings = Item("Settings", "\uE713");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var quit = Item("Quit JARVIS", "\uE7E8");
        quit.Click += (_, _) => QuitRequested?.Invoke();

        _menu.Items.Add(open);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(talk);
        _menu.Items.Add(stop);
        _menu.Items.Add(pause);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(settings);
        _menu.Items.Add(quit);
    }

    private static MenuItem Item(string header, string glyph)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9F, 0xB4, 0xC6))
        });
        panel.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });
        return new MenuItem { Header = panel };
    }

    private void ShowMenu()
    {
        if (_menu is null) return;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmApp) return IntPtr.Zero;
        handled = true;
        var notification = (int)(lParam.ToInt64() & 0xFFFF);
        switch (notification)
        {
            case 0x0202: // WM_LBUTTONUP
            case 0x0203: // WM_LBUTTONDBLCLK
            case 0x0400: // NIN_SELECT (NOTIFYICON_VERSION_4)
            case 0x0401: // NIN_KEYSELECT
                OpenRequested?.Invoke();
                break;
            case 0x0205: // WM_RBUTTONUP
            case 0x007B: // WM_CONTEXTMENU
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null && _added)
        {
            var data = NewData();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
        _icon?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
}
