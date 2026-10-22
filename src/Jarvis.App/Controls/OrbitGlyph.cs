using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Jarvis.App.Controls;

/// <summary>A circular status glyph with orbital motion, used in the status cards and the HUD.</summary>
public sealed class OrbitGlyph : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(OrbitGlyph),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(OrbitGlyph),
        new FrameworkPropertyMetadata("\uE946", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(OrbitGlyph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(OrbitGlyph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly DateTime _start = DateTime.UtcNow;
    private bool _running;
    private DateTime _last = DateTime.UtcNow;

    public OrbitGlyph()
    {
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsHitTestVisible = false;
    }

    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

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
        if (Mode is "thinking" or "working" or "listening" or "speaking" or "waiting")
            InvalidateVisual();
    }

    internal static Color AccentFor(string mode) => mode switch
    {
        "listening" => Color.FromRgb(0xF6, 0xF8, 0xF9),
        "thinking" => Color.FromRgb(0xA9, 0xC7, 0xE8),
        "speaking" => Color.FromRgb(0x93, 0xB8, 0xE8),
        "working" or "background" => Color.FromRgb(0xE8, 0xA5, 0x7C),
        "waiting" => Color.FromRgb(0xEF, 0xC0, 0x8A),
        "done" => Color.FromRgb(0xF0, 0xD6, 0xB8),
        "paused" => Color.FromRgb(0x73, 0x80, 0x8D),
        _ => Color.FromRgb(0xCF, 0xD8, 0xDF)
    };

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var center = new Point(w / 2, h / 2);
        var radius = Math.Min(w, h) / 2 - 2;
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        var accent = AccentFor(Mode);

        // halo
        var halo = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5)
        };
        halo.GradientStops.Add(new GradientStop(WithAlpha(accent, Mode == "idle" ? 0.10 : 0.22), 0));
        halo.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        halo.Freeze();
        dc.DrawEllipse(halo, null, center, radius * 1.15, radius * 1.15);

        // disc
        var disc = new RadialGradientBrush
        {
            Center = new Point(0.42, 0.36),
            GradientOrigin = new Point(0.42, 0.36)
        };
        disc.GradientStops.Add(new GradientStop(Color.FromArgb(26, 255, 255, 255), 0));
        disc.GradientStops.Add(new GradientStop(Color.FromArgb(10, 255, 255, 255), 1));
        disc.Freeze();
        dc.DrawEllipse(disc, null, center, radius, radius);
        dc.DrawEllipse(null, Pen(Color.FromArgb(56, 255, 255, 255), 1), center, radius, radius);

        // progress arc
        if (Progress > 0.001)
        {
            dc.DrawGeometry(null, Pen(WithAlpha(accent, 0.85), 2.2, round: true),
                Arc(center, radius - 1, -90, Math.Clamp(Progress, 0, 1) * 360));
        }

        // glyph
        var glyph = string.IsNullOrEmpty(Glyph) ? "\uE946" : Glyph;
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Math.Max(10, w * 0.34), new SolidColorBrush(accent), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));

        // orbital motion
        switch (Mode)
        {
            case "thinking":
            case "working":
                for (var i = 0; i < 3; i++)
                {
                    var angle = t * 55 + i * 120;
                    var dot = Polar(center, radius * 0.98, angle);
                    dc.DrawEllipse(new SolidColorBrush(WithAlpha(accent, 0.95)), null, dot, 1.9, 1.9);
                }
                break;
            case "listening":
            {
                var level = Math.Clamp(Level, 0, 1);
                var pulse = radius * (0.62 + 0.10 * Math.Sin(t * 5));
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.25 + level * 0.4), 1.2), center, pulse, pulse);
                break;
            }
            case "speaking":
            {
                var level = Math.Clamp(Level, 0, 1);
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.3 + level * 0.4), 1.4), center,
                    radius * (0.86 + level * 0.08), radius * (0.86 + level * 0.08));
                break;
            }
            case "waiting":
            {
                var breathe = 0.5 + 0.5 * Math.Sin(t * 2.2);
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.1 + breathe * 0.25), 1), center,
                    radius * (1.04 + breathe * 0.04), radius * (1.04 + breathe * 0.04));
                break;
            }
            case "done":
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.35), 1.2), center, radius * 0.9, radius * 0.9);
                break;
        }
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);

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

    private static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Polar(center, radius, startDegrees), false, false);
            ctx.ArcTo(Polar(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0,
                sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>The thin activity line: waveform while talking, a sweep while thinking, a breath while waiting.</summary>
public sealed class ActivityLine : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(ActivityLine),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(ActivityLine),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DateTime _start = DateTime.UtcNow;
    private bool _running;
    private DateTime _last = DateTime.UtcNow;

    public ActivityLine()
    {
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsHitTestVisible = false;
    }

    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

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
        if (Mode is not ("idle" or "paused")) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 0) return;
        var mid = h / 2;
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        var accent = OrbitGlyph.AccentFor(Mode);
        var level = Math.Clamp(Level, 0, 1);

        switch (Mode)
        {
            case "idle":
            case "paused":
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), null,
                    new Rect(0, mid - 1, w, 2), 1, 1);
                break;
            case "listening":
            case "speaking":
            {
                const int bars = 22;
                var step = w / bars;
                for (var i = 0; i < bars; i++)
                {
                    var envelope = Math.Sin(Math.PI * i / (bars - 1));
                    var wave = 0.35 + 0.65 * Math.Abs(Math.Sin(i * 0.62 + t * 7.2));
                    var height = 1.5 + level * 14 * envelope * wave;
                    var brush = new SolidColorBrush(WithAlpha(accent, 0.22 + 0.65 * envelope));
                    brush.Freeze();
                    dc.DrawRoundedRectangle(brush, null,
                        new Rect(i * step + step * 0.28, mid - height / 2, Math.Max(1.2, step * 0.38), height), 1, 1);
                }
                break;
            }
            case "thinking":
            case "working":
            case "background":
            {
                // A dim track with a moving highlight: honest indeterminate progress, matching the
                // reference's progress bar without pretending to know a percentage.
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null,
                    new Rect(0, mid - 1.2, w, 2.4), 1.2, 1.2);
                var sweep = 0.55 + 0.45 * Math.Sin(t * 1.9);
                var position = sweep * (w - 60);
                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0)
                };
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                brush.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.9), 0.5));
                brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                brush.Freeze();
                dc.DrawRoundedRectangle(brush, null, new Rect(position, mid - 1.4, 60, 2.8), 1.4, 1.4);
                break;
            }
            case "waiting":
            {
                var breathe = 0.35 + 0.45 * (0.5 + 0.5 * Math.Sin(t * 2.3));
                dc.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(accent, breathe)), null,
                    new Rect(0, mid - 1.2, w, 2.4), 1.2, 1.2);
                break;
            }
            case "done":
                dc.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(accent, 0.75)), null,
                    new Rect(0, mid - 1.2, w, 2.4), 1.2, 1.2);
                break;
            default:
                dc.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(accent, 0.4)), null,
                    new Rect(0, mid - 1, w, 2), 1, 1);
                break;
        }
    }

    private static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
}
