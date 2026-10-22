using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Jarvis.App.Controls;

/// <summary>The floating glass orb: a soft sphere with a bright core and one accent per state.</summary>
public sealed class StatusOrb : FrameworkElement
{
    public static readonly DependencyProperty ActivityProperty = DependencyProperty.Register(
        nameof(Activity), typeof(string), typeof(StatusOrb),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(StatusOrb),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DateTime _start = DateTime.UtcNow;
    private bool _rendering;
    private DateTime _lastFrame = DateTime.UtcNow;
    private double _smoothed;

    public StatusOrb()
    {
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsHitTestVisible = false;
    }

    public string Activity
    {
        get => (string)GetValue(ActivityProperty);
        set => SetValue(ActivityProperty, value);
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private void Start()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        if (dt < 1.0 / 45) return;
        _lastFrame = now;
        _smoothed += (Math.Clamp(Level, 0, 1) - _smoothed) * Math.Min(1, dt * 10);
        InvalidateVisual();
    }

    private static Color AccentFor(string activity) => activity switch
    {
        "listening" => Color.FromRgb(0xF6, 0xF8, 0xF9),
        "thinking" => Color.FromRgb(0xA9, 0xC7, 0xE8),
        "speaking" => Color.FromRgb(0x93, 0xB8, 0xE8),
        "waiting" => Color.FromRgb(0xEF, 0xC0, 0x8A),
        "background" => Color.FromRgb(0xA8, 0xD8, 0xC0),
        "paused" => Color.FromRgb(0x73, 0x80, 0x8D),
        "done" => Color.FromRgb(0xF3, 0xD3, 0xBE),
        _ => Color.FromRgb(0xE4, 0xD6, 0xC8)
    };

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var center = new Point(w / 2, h / 2);
        var radius = Math.Min(w, h) / 2 - 8;
        var t = (DateTime.UtcNow - _start).TotalSeconds;
        var accent = AccentFor(Activity);
        var level = _smoothed;
        var breathe = 0.5 + 0.5 * Math.Sin(t * 1.15);

        // ambient light behind the sphere
        var ambient = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        ambient.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.16 + level * 0.12), 0));
        ambient.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.045), 0.62));
        ambient.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        ambient.Freeze();
        dc.DrawEllipse(ambient, null, center, radius * 1.26, radius * 1.26);

        // glass body: off-centre highlight makes it read as a sphere
        var body = new RadialGradientBrush
        {
            Center = new Point(0.38, 0.32),
            GradientOrigin = new Point(0.38, 0.32),
            RadiusX = 0.78,
            RadiusY = 0.78
        };
        body.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0));
        body.GradientStops.Add(new GradientStop(Color.FromArgb(17, 255, 255, 255), 0.42));
        body.GradientStops.Add(new GradientStop(Color.FromArgb(7, 255, 255, 255), 0.75));
        body.GradientStops.Add(new GradientStop(Color.FromArgb(2, 255, 255, 255), 1));
        body.Freeze();
        dc.DrawEllipse(body, null, center, radius, radius);

        // rim and inner rim
        dc.DrawEllipse(null, Pen(Color.FromArgb(104, 255, 255, 255), 1), center, radius, radius);
        dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.13), 1), center, radius * 0.83, radius * 0.83);

        // a bright catch on the upper-left rim, then the sunset on the lower rim
        dc.DrawGeometry(null, Pen(Color.FromArgb(96, 255, 255, 255), 1.2, round: true),
            Arc(center, radius * 0.992, 196, 54));
        dc.DrawGeometry(null, Pen(Color.FromArgb(52, 232, 165, 124), 1.3, round: true),
            Arc(center, radius * 0.995, 18, 144));

        // state motion
        switch (Activity)
        {
            case "listening":
                for (var i = 0; i < 3; i++)
                {
                    var phase = (t * 0.72 + i / 3.0) % 1.0;
                    var ringRadius = radius * (0.34 + phase * 0.66);
                    var alpha = (1 - phase) * (0.18 + level * 0.32);
                    dc.DrawEllipse(null, Pen(WithAlpha(accent, alpha), 1), center, ringRadius, ringRadius);
                }
                break;
            case "thinking":
            {
                for (var i = 0; i < 3; i++)
                {
                    var angle = t * 72 + i * 120;
                    var dot = Polar(center, radius * 0.60, angle);
                    dc.DrawEllipse(new SolidColorBrush(WithAlpha(accent, 0.9)), null, dot, 2.6, 2.6);
                    var halo = new RadialGradientBrush
                    {
                        Center = new Point(0.5, 0.5),
                        GradientOrigin = new Point(0.5, 0.5)
                    };
                    halo.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.35), 0));
                    halo.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                    halo.Freeze();
                    dc.DrawEllipse(halo, null, dot, 12, 12);
                }
                dc.DrawGeometry(null, Pen(WithAlpha(accent, 0.30), 1.4, round: true),
                    Arc(center, radius * 0.93, t * 78, 58));
                break;
            }
            case "speaking":
            {
                var pulse = radius * (0.50 + level * 0.06);
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.30 + level * 0.25), 1.2), center, pulse, pulse);
                for (var i = 0; i < 2; i++)
                {
                    var phase = (t * 0.9 + i / 2.0) % 1.0;
                    var ringRadius = radius * (0.45 + phase * 0.55);
                    dc.DrawEllipse(null, Pen(WithAlpha(accent, (1 - phase) * 0.20 * (0.4 + level)), 1),
                        center, ringRadius, ringRadius);
                }
                break;
            }
            case "waiting":
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.14 + breathe * 0.22), 1), center,
                    radius * (0.99 + breathe * 0.03), radius * (0.99 + breathe * 0.03));
                break;
            case "background":
            {
                var spark = center.X + Math.Sin(t * 0.7) * radius * 0.45;
                dc.DrawEllipse(new SolidColorBrush(WithAlpha(accent, 0.55)), null,
                    new Point(spark, center.Y + radius * 0.62), 1.8, 1.8);
                break;
            }
            case "done":
                dc.DrawEllipse(null, Pen(WithAlpha(accent, 0.35), 1.4), center, radius * 0.93, radius * 0.93);
                break;
        }

        // the core
        var core = radius * (0.030 + level * 0.016 + breathe * 0.006);
        var glow = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.95), 0));
        glow.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.28), 0.28));
        glow.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        glow.Freeze();
        var glowRadius = radius * (0.22 + level * 0.08);
        dc.DrawEllipse(glow, null, center, glowRadius, glowRadius);

        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(250, 255, 252, 248)), null, center, core, core);
        var coreHalo = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5)
        };
        coreHalo.GradientStops.Add(new GradientStop(WithAlpha(accent, 0.85), 0));
        coreHalo.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        coreHalo.Freeze();
        dc.DrawEllipse(coreHalo, null, center, core * 2.6, core * 2.6);
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
