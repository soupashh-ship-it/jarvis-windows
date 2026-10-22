using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Jarvis.App.Controls;

/// <summary>The floating glass pill: what JARVIS is doing, in one calm card.</summary>
public partial class HudView : UserControl
{
    private double _target;

    public HudView()
    {
        InitializeComponent();
        Opacity = 0;
    }

    public void Update(string activity, double mic, double output, string title, string detail)
    {
        var mode = string.IsNullOrWhiteSpace(activity) ? "idle" : activity;
        var accent = OrbitGlyph.AccentFor(mode);
        var accentBrush = Frozen(accent);
        var softAccent = Frozen(Color.FromArgb(190, accent.R, accent.G, accent.B));

        Glyph.Mode = mode;
        Glyph.Glyph = GlyphFor(mode);
        Glyph.Level = mode == "listening" ? mic : output;
        Line.Mode = mode;
        Line.Level = mode == "listening" ? mic : output;
        // As in the reference, the pill only carries an activity/progress line while there is work to show.
        Line.Visibility = mode is "idle" or "paused" or "done" ? Visibility.Collapsed : Visibility.Visible;

        TitleText.Text = title;
        DetailText.Text = detail;
        StateText.Text = Spaced(mode.ToUpperInvariant());
        StateText.Foreground = softAccent;
        Glyph.InvalidateVisual();

        Pill.BorderBrush = mode is "waiting" or "done"
            ? Frozen(Color.FromArgb(86, accent.R, accent.G, accent.B))
            : Frozen(Color.FromArgb(46, 255, 255, 255));

        var idle = mode is "idle" or "paused";
        var target = idle ? 0.0 : 1.0;
        if (Math.Abs(target - _target) < 0.01) return;
        _target = target;
        BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static string GlyphFor(string mode) => mode switch
    {
        "listening" => "\uE720",
        "thinking" => "\uE945",
        "speaking" => "\uE767",
        "waiting" => "\uE72E",
        "background" => "\uE7C3",
        "done" => "\uE73E",
        "paused" => "\uE769",
        _ => "\uE946"
    };

    private static string Spaced(string text)
    {
        if (text.Length < 2) return text;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\u2002', words.Select(w => string.Join('\u2009', w.ToCharArray())));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
