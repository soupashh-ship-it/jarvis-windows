using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jarvis.App.Controls;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>A click-through, never-focused glass pill that floats above the desktop.</summary>
public sealed class HudWindow : Window
{
    private readonly HudView _view = new();
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x80;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _view;
        SourceInitialized += OnSourceInitialized;
    }

    public DisplayInfo Monitor { get; private set; } =
        new(0, "DISPLAY", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1, true);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        }
        catch (Exception ex) { Log.Debug("hud styles: " + ex.Message); }
    }

    public void PlaceOn(DisplayInfo monitor, double bottomMargin = 60)
    {
        Monitor = monitor;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var scale = monitor.Scale <= 0.1 ? 1.0 : monitor.Scale;
        const double widthDip = 700;
        const double heightDip = 170;
        Width = widthDip;
        Height = heightDip;

        var widthPx = (int)Math.Round(widthDip * scale);
        var heightPx = (int)Math.Round(heightDip * scale);
        var margin = (int)Math.Round(bottomMargin * scale);
        var x = monitor.X + (monitor.Width - widthPx) / 2;
        var y = monitor.Y + monitor.Height - heightPx - margin;

        SetWindowPos(hwnd, HwndTopmost, x, y, widthPx, heightPx, SwpNoActivate | SwpShowWindow);
    }

    public void Update(string activity, double mic, double output, string title, string detail) =>
        _view.Update(activity, mic, output, title, detail);

    public void SetHidden(bool hidden) => Opacity = hidden ? 0 : 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Owns one HUD per selected display and feeds it the current state.</summary>
public sealed class HudManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Orchestrator _jarvis;
    private readonly List<HudWindow> _windows = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private bool _hidden;
    private string _lastNonIdle = "idle";
    private DateTime _doneUntil = DateTime.MinValue;
    private string _doneDetail = "";

    public HudManager(AppSettings settings, Orchestrator jarvis)
    {
        _settings = settings;
        _jarvis = jarvis;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Render, (_, _) => Tick(),
            _dispatcher);
        Rebuild();
        _timer.Start();
    }

    public void Rebuild()
    {
        _timer.Stop();
        foreach (var window in _windows)
        {
            try { window.Close(); } catch { }
        }
        _windows.Clear();

        if (!_settings.ShowHud)
        {
            _timer.Start();
            return;
        }

        var monitors = _settings.ShowOnAllMonitors
            ? DisplayApi.All()
            : new List<DisplayInfo> { ResolveMonitor() };

        foreach (var monitor in monitors)
        {
            var window = new HudWindow();
            window.Show();
            window.PlaceOn(monitor, _settings.HudBottomMargin);
            _windows.Add(window);
        }
        _timer.Start();
    }

    private DisplayInfo ResolveMonitor() => DisplayApi.Resolve(_settings.HudMonitor);

    private void Tick()
    {
        if (_windows.Count == 0) return;

        if (!_settings.ShowOnAllMonitors && _settings.HudMonitor is "active" or "mouse")
        {
            var wanted = ResolveMonitor();
            foreach (var window in _windows)
                if (window.Monitor.Index != wanted.Index || window.Monitor.X != wanted.X)
                    window.PlaceOn(wanted, _settings.HudBottomMargin);
        }

        var actual = _jarvis.Activity;
        var honorific = _settings.Honorific;

        if (actual is not ("idle" or "paused"))
        {
            _lastNonIdle = actual;
            _doneUntil = DateTime.MinValue;
            if (actual == "speaking" && _jarvis.ReplyText.Length > 0) _doneDetail = _jarvis.ReplyText;
        }
        else if (actual == "idle" && _lastNonIdle is "thinking" or "speaking" && _doneUntil == DateTime.MinValue)
        {
            _doneUntil = DateTime.UtcNow.AddSeconds(2.8);
            if (_jarvis.ReplyText.Length > 0) _doneDetail = _jarvis.ReplyText;
        }

        var display = actual;
        string title;
        string detail;
        if (actual == "idle" && _doneUntil != DateTime.MinValue && DateTime.UtcNow < _doneUntil)
        {
            display = "done";
            title = "Done";
            detail = _doneDetail.Length > 0 ? _doneDetail : "All wrapped up.";
        }
        else
        {
            if (actual == "idle" && _doneUntil != DateTime.MinValue && DateTime.UtcNow >= _doneUntil)
            {
                _doneUntil = DateTime.MinValue;
                _lastNonIdle = "idle";
            }
            (title, detail) = actual switch
            {
                "listening" => ("Listening…",
                    _jarvis.LiveTranscript.Length > 0 ? _jarvis.LiveTranscript : $"Go ahead, {honorific}."),
                "thinking" => (_jarvis.StepText.Length > 0 ? "Working…" : "Thinking…",
                    _jarvis.StepText.Length > 0 ? _jarvis.StepText : "Analysing your request"),
                "speaking" => ("Speaking…", _jarvis.ReplyText),
                "waiting" => ("Permission", _jarvis.PendingQuestion),
                "background" => ("Working…", "A background task is running"),
                "paused" => ("Paused", "Listening is paused"),
                _ => ("Standing by", _settings.HasModel ? "Ready when you are." : "Local mode. Ready when you are.")
            };
        }

        foreach (var window in _windows)
            window.Update(display, _jarvis.MicLevel, _jarvis.OutputLevel, title, detail);
    }

    public async Task HideForCaptureAsync()
    {
        if (_hidden) return;
        _hidden = true;
        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var window in _windows) window.SetHidden(true);
        });
        await Task.Delay(90);
    }

    public void RestoreAfterCapture()
    {
        if (!_hidden) return;
        _hidden = false;
        _dispatcher.Invoke(() =>
        {
            foreach (var window in _windows) window.SetHidden(false);
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var window in _windows)
        {
            try { window.Close(); } catch { }
        }
        _windows.Clear();
    }
}
