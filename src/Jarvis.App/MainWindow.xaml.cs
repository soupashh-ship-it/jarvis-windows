using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Jarvis.App.ViewModels;
using Jarvis.Core;

namespace Jarvis.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _view;
    private bool _allowClose;

    public MainWindow(MainViewModel view)
    {
        InitializeComponent();
        _view = view;
        DataContext = view;

        // Small laptops must still get a usable window.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(1000, area.Width - 60));
        Height = Math.Min(Height, Math.Max(660, area.Height - 60));

        view.TimelineAppended += OnTimelineAppended;
        view.SettingsRequested += ShowSettings;
        view.SetupSettingsRequested += ShowSettings;
        Loaded += (_, _) => Composer.Focus();
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowDeck()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        Composer.Focus();
    }

    public void PrepareExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // immersive dark mode
            var round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // rounded corners
            var caption = 0x00110F0C;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // caption colour
            var border = 0x002A1C16;
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)); // border colour
        }
        catch (Exception ex) { Log.Debug("dwm attributes unavailable: " + ex.Message); }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxHeight = SystemParameters.WorkArea.Height + 8;
            MaxWidth = SystemParameters.WorkArea.Width + 8;
            MaximizeButton.Content = "\uE923";
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
            MaximizeButton.Content = "\uE922";
        }
    }

    private void OnTimelineAppended()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { TimelineScroll.ScrollToEnd(); } catch { }
        }));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _view.StopCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
    }

    private void Composer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _view.Send();
        }
    }

    private void Composer_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ComposerHint is null) return;
        ComposerHint.Visibility = string.IsNullOrEmpty(Composer.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _view.Send();
    private void Talk_Click(object sender, RoutedEventArgs e) => _view.TalkCommand.Execute(null);
    private void Stop_Click(object sender, RoutedEventArgs e) => _view.StopCommand.Execute(null);
    private void NewSession_Click(object sender, RoutedEventArgs e) => _view.NewSessionCommand.Execute(null);
    private void Pause_Click(object sender, RoutedEventArgs e) => _view.PauseCommand.Execute(null);
    private void Clear_Click(object sender, RoutedEventArgs e) => _view.ClearCommand.Execute(null);
    private void Allow_Click(object sender, RoutedEventArgs e) => _view.AllowCommand.Execute(null);
    private void Deny_Click(object sender, RoutedEventArgs e) => _view.DenyCommand.Execute(null);
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void RefreshMemory_Click(object sender, RoutedEventArgs e) => _view.RefreshMemoryCommand.Execute(null);

    private void OpenNotes_Click(object sender, RoutedEventArgs e) => _view.OpenNotesCommand.Execute(null);

    private void SaveSetup_Click(object sender, RoutedEventArgs e) => _view.SaveSetupCommand.Execute(null);
    private void SkipSetup_Click(object sender, RoutedEventArgs e) => _view.SkipSetupCommand.Execute(null);

    private void SetupKey_Changed(object sender, RoutedEventArgs e) =>
        _view.SetupApiKey = SetupKeyBox.Password;

    private void ShowSettings()
    {
        if (Application.Current is App app) app.ShowSettings();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            if (Application.Current is App app) app.NotifyHidden();
        }
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
