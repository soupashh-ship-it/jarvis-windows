using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jarvis.Core;

namespace Jarvis.App;

/// <summary>Click-through windows, one per display, that draw the "let me show you" annotations.</summary>
public sealed class AnnotationOverlay : IAnnotationSink, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly List<OverlayWindow> _windows = new();
    private DispatcherTimer? _timer;
    private DateTime _expires;

    public AnnotationOverlay(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Task ShowAsync(AnnotationRequest request)
    {
        _dispatcher.Invoke(() => ShowCore(request));
        return Task.CompletedTask;
    }

    private void ShowCore(AnnotationRequest request)
    {
        ClearCore();
        var started = DateTime.UtcNow;
        foreach (var display in DisplayApi.All())
        {
            var window = new OverlayWindow(display, request.Shapes, started, request.Duration);
            window.Show();
            window.PlaceOn(display);
            _windows.Add(window);
        }

        _expires = started + request.Duration;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Render,
            (_, _) =>
            {
                if (DateTime.UtcNow < _expires) return;
                ClearCore();
            }, _dispatcher);
        _timer.Start();
    }

    public void Clear()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(ClearCore);
            return;
        }
        ClearCore();
    }

    private void ClearCore()
    {
        _timer?.Stop();
        _timer = null;
        foreach (var window in _windows)
        {
            try { window.Close(); } catch { }
        }
        _windows.Clear();
    }

    public void Dispose() => Clear();
}

public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x80;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    public OverlayWindow(DisplayInfo monitor, IReadOnlyList<AnnotationShape> shapes, DateTime started, TimeSpan duration)
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
        Content = new AnnotationView
        {
            Monitor = monitor,
            Shapes = shapes,
            Started = started,
            Duration = duration
        };
        SourceInitialized += (_, _) =>
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            }
            catch { }
        };
    }

    public void PlaceOn(DisplayInfo monitor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var scale = monitor.Scale <= 0.1 ? 1.0 : monitor.Scale;
        Width = monitor.Width / scale;
        Height = monitor.Height / scale;
        SetWindowPos(handle, HwndTopmost, monitor.X, monitor.Y, monitor.Width, monitor.Height,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

public sealed class AnnotationView : FrameworkElement
{
    private static readonly Color Gold = Color.FromRgb(0xFF, 0xC5, 0x3D);
    private static readonly Color Ink = Color.FromRgb(0x10, 0x12, 0x14);
    private static readonly FontFamily Sans = new("Segoe UI Variable Display, Segoe UI");

    public DisplayInfo Monitor { get; init; } = new(0, "D", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1, true);
    public IReadOnlyList<AnnotationShape> Shapes { get; init; } = Array.Empty<AnnotationShape>();
    public DateTime Started { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(9);

    private bool _running;
    private DateTime _last = DateTime.UtcNow;

    public AnnotationView()
    {
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsHitTestVisible = false;
    }

    private void Start()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _last).TotalSeconds < 1.0 / 30) return;
        _last = now;
        InvalidateVisual();
    }

    private double ToLocalX(double desktopX) => (desktopX - Monitor.X) / Monitor.Scale;
    private double ToLocalY(double desktopY) => (desktopY - Monitor.Y) / Monitor.Scale;
    private double ToLocal(double size) => size / Monitor.Scale;

    protected override void OnRender(DrawingContext dc)
    {
        var t = (DateTime.UtcNow - Started).TotalSeconds;
        var total = Duration.TotalSeconds;
        if (t > total + 0.5) return;

        var fadeIn = Math.Clamp(t / 0.30, 0, 1);
        var fadeOut = Math.Clamp((total - t) / 0.75, 0, 1);
        var alpha = Math.Min(fadeIn, fadeOut);
        if (alpha <= 0.01) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pulse = 0.82 + 0.18 * Math.Sin(t * 3.2);

        foreach (var shape in Shapes)
        {
            var x = ToLocalX(shape.X);
            var y = ToLocalY(shape.Y);
            if (x < -400 || y < -400 || x > ActualWidth + 400 || y > ActualHeight + 400) continue;

            switch (shape.Type)
            {
                case "ring":
                {
                    var radius = ToLocal(shape.Radius ?? 60) * (0.75 + 0.25 * fadeIn);
                    DrawCircle(dc, new Point(x, y), radius, alpha * pulse);
                    DrawCross(dc, new Point(x, y), radius, alpha);
                    DrawLabel(dc, shape, x, y + radius + 16, alpha, pixelsPerDip);
                    break;
                }
                case "arrow":
                {
                    var from = new Point(ToLocalX(shape.FromX ?? shape.X - 120), ToLocalY(shape.FromY ?? shape.Y - 120));
                    var to = new Point(x, y);
                    DrawArrow(dc, from, to, alpha);
                    DrawLabel(dc, shape, x, y + 20, alpha, pixelsPerDip);
                    break;
                }
                case "rect":
                {
                    var w = ToLocal(shape.Width ?? 200);
                    var h = ToLocal(shape.Height ?? 80);
                    DrawRect(dc, new Rect(x, y, w, h), alpha * pulse);
                    DrawLabel(dc, shape, x + w / 2, y + h + 16, alpha, pixelsPerDip);
                    break;
                }
            }

            if (shape.Step is { } step)
                DrawStep(dc, new Point(x, y), step, alpha, pixelsPerDip);
        }
    }

    private static void DrawCircle(DrawingContext dc, Point center, double radius, double alpha)
    {
        dc.DrawEllipse(null, Pen(Color.FromArgb((byte)(60 * alpha), 0, 0, 0), 9), new Point(center.X + 2, center.Y + 2), radius, radius);
        dc.DrawEllipse(null, Pen(Color.FromArgb((byte)(70 * alpha), Gold.R, Gold.G, Gold.B), 12), center, radius, radius);
        dc.DrawEllipse(null, Pen(Color.FromArgb((byte)(255 * alpha), Gold.R, Gold.G, Gold.B), 2.6), center, radius, radius);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(22 * alpha), Gold.R, Gold.G, Gold.B)), null, center, radius, radius);
    }

    private static void DrawCross(DrawingContext dc, Point center, double radius, double alpha)
    {
        var pen = Pen(Color.FromArgb((byte)(200 * alpha), Gold.R, Gold.G, Gold.B), 1.6);
        var tick = Math.Max(4, radius * 0.18);
        dc.DrawLine(pen, new Point(center.X - tick, center.Y), new Point(center.X + tick, center.Y));
        dc.DrawLine(pen, new Point(center.X, center.Y - tick), new Point(center.X, center.Y + tick));
    }

    private static void DrawRect(DrawingContext dc, Rect rect, double alpha)
    {
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb((byte)(26 * alpha), Gold.R, Gold.G, Gold.B)),
            Pen(Color.FromArgb((byte)(235 * alpha), Gold.R, Gold.G, Gold.B), 2.2), rect, 10, 10);
        var inner = Rect.Inflate(rect, 4, 4);
        dc.DrawRoundedRectangle(null, Pen(Color.FromArgb((byte)(55 * alpha), Gold.R, Gold.G, Gold.B), 6), inner, 12, 12);
    }

    private static void DrawArrow(DrawingContext dc, Point from, Point to, double alpha)
    {
        var pen = Pen(Color.FromArgb((byte)(255 * alpha), Gold.R, Gold.G, Gold.B), 3.2, round: true);
        var shadow = Pen(Color.FromArgb((byte)(90 * alpha), 0, 0, 0), 7, round: true);

        dc.DrawLine(shadow, new Point(from.X + 2, from.Y + 2), new Point(to.X + 2, to.Y + 2));
        dc.DrawLine(pen, from, to);

        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double headLength = 18;
        const double spread = 0.42;
        var left = new Point(to.X - headLength * Math.Cos(angle - spread), to.Y - headLength * Math.Sin(angle - spread));
        var right = new Point(to.X - headLength * Math.Cos(angle + spread), to.Y - headLength * Math.Sin(angle + spread));
        dc.DrawLine(shadow, to, left);
        dc.DrawLine(shadow, to, right);
        dc.DrawLine(pen, to, left);
        dc.DrawLine(pen, to, right);

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), Gold.R, Gold.G, Gold.B)), null, from, 3, 3);
    }

    private static void DrawStep(DrawingContext dc, Point target, int step, double alpha, double pixelsPerDip)
    {
        var center = new Point(target.X - 16, target.Y - 16);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(235 * alpha), Gold.R, Gold.G, Gold.B)), null, center, 13, 13);
        var text = new FormattedText(step.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(Sans, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            12, new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), Ink.R, Ink.G, Ink.B)), pixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2 - 1));
    }

    private static void DrawLabel(DrawingContext dc, AnnotationShape shape, double centerX, double top, double alpha,
        double pixelsPerDip)
    {
        if (string.IsNullOrWhiteSpace(shape.Label)) return;
        var lines = shape.Label!.Split('\n');
        var texts = new List<FormattedText>();
        foreach (var line in lines)
        {
            texts.Add(new FormattedText(line.Trim(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Sans, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                13.5, new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255)), pixelsPerDip)
            {
                MaxTextWidth = 340,
                TextAlignment = TextAlignment.Center
            });
        }

        var width = texts.Max(t => t.Width);
        var height = texts.Sum(t => t.Height) + (texts.Count - 1) * 2;
        const double padX = 12, padY = 7;
        var rect = new Rect(centerX - width / 2 - padX, top, width + padX * 2, height + padY * 2);

        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb((byte)(220 * alpha), 8, 12, 16)),
            Pen(Color.FromArgb((byte)(120 * alpha), Gold.R, Gold.G, Gold.B), 1), rect, 8, 8);

        var y = top + padY;
        foreach (var text in texts)
        {
            dc.DrawText(text, new Point(centerX - text.Width / 2, y));
            y += text.Height + 2;
        }
    }

    private static Pen Pen(Color color, double thickness, bool round = false)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat
        };
        pen.Freeze();
        return pen;
    }
}
