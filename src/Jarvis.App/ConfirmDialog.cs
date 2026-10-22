using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jarvis.App;

/// <summary>The on-screen half of a spoken permission question: a calm, dark confirmation card.</summary>
public sealed class ConfirmDialog : Window
{
    private readonly TaskCompletionSource<bool?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _countdown;
    private readonly DispatcherTimer _timer;
    private int _seconds = 60;

    private ConfirmDialog(string question, Window? owner)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var root = new Border
        {
            Background = Brush("#F210171F"),
            BorderBrush = Brush("#33FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(28, 26, 28, 24)
        };
        root.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };

        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7, Height = 7, Fill = Brush("#EFC08A"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };
        var title = new TextBlock
        {
            Text = "P E R M I S S I O N   R E Q U I R E D",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 9,
            Foreground = Brush("#EFC08A"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11, Width = 26, Height = 24, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brush("#73808D"), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Finish(null);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(dot);
        header.Children.Add(title);
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 16,
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#F1F4F7")
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Say yes or no, or choose below.",
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 11.5,
            Foreground = Brush("#73808D")
        });

        var buttons = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var deny = MakeButton("Deny", "#14FFFFFF", "#A9B4BE", "#22FFFFFF");
        deny.Click += (_, _) => Finish(false);
        var allow = MakeButton("Allow", "#E8A57C", "#20140D", "#E8A57C");
        allow.Click += (_, _) => Finish(true);
        Grid.SetColumn(deny, 2);
        Grid.SetColumn(allow, 4);
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        stack.Children.Add(buttons);

        _countdown = new TextBlock
        {
            Text = "Closes in 60s",
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 10.5,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            Foreground = Brush("#56636F")
        };
        stack.Children.Add(_countdown);

        root.Child = stack;
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(null); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                // Preserve the focused choice: Enter on Deny must never approve the request.
                if (Keyboard.FocusedElement is Button focused)
                    focused.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else
                    Finish(true);
                e.Handled = true;
            }
        };

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            _seconds--;
            _countdown.Text = $"Closes in {_seconds}s";
            if (_seconds <= 0) Finish(null);
        }, Dispatcher.CurrentDispatcher);
        Loaded += (_, _) => { _timer.Start(); Activate(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static Button MakeButton(string text, string background, string foreground, string border) => new()
    {
        Content = text,
        Padding = new Thickness(22, 10, 22, 10),
        Background = Brush(background),
        Foreground = Brush(foreground),
        BorderBrush = Brush(border),
        BorderThickness = new Thickness(1),
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Cursor = Cursors.Hand,
        Template = ButtonTemplate(background, border)
    };

    private static ControlTemplate ButtonTemplate(string background, string border)
    {
        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.BackgroundProperty, Brush(background));
        factory.SetValue(Border.BorderBrushProperty, Brush(border));
        factory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(presenter);
        template.VisualTree = factory;
        return template;
    }

    private void Finish(bool? answer)
    {
        _result.TrySetResult(answer);
        try { Close(); } catch { }
    }

    public static Task<bool?> AskAsync(string question, Window? owner)
    {
        var dialog = new ConfirmDialog(question, owner);
        dialog.ShowDialog();
        return Task.FromResult(dialog._result.Task.IsCompletedSuccessfully ? dialog._result.Task.Result : (bool?)null);
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
