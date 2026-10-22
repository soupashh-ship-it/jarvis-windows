using System.Runtime.InteropServices;
using System.Windows;
using Jarvis.App.ViewModels;
using Jarvis.Core;
using Microsoft.Win32;

namespace Jarvis.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _exiting;
    private bool _hiddenNotified;
    private SettingsWindow? _settingsWindow;
    private string _appliedMic = "";
    private string _appliedSpeaker = "";
    private string _appliedWakePhrase = "";

    public AppSettings Settings { get; private set; } = null!;
    public EventBus Events { get; private set; } = null!;
    public MemoryStore Memory { get; private set; } = null!;
    public Orchestrator Jarvis { get; private set; } = null!;
    public MainViewModel ViewModel { get; private set; } = null!;
    public MainWindow? Deck { get; private set; }
    public HudManager? Hud { get; private set; }
    public TrayIcon? Tray { get; private set; }
    public HotkeyManager? Hotkeys { get; private set; }
    public AnnotationOverlay? Annotations { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "JarvisForWindows.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("JARVIS is already running. Look for its icon beside the clock.",
                "JARVIS", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            Paths.InitializeApplicationDirectory();
            Bootstrap();
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show("JARVIS could not start:\n\n" + ex.Message, "JARVIS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void Bootstrap()
    {
        Paths.Ensure();
        Log.Init(Paths.LogsDir);
        Log.Info("=== JARVIS starting ===");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            // Continuing after an unhandled UI failure can leave settings, permissions, or
            // device state partially updated. Let WPF terminate through its normal fatal path.
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Settings = SettingsStore.Load();
        if (SettingsStore.LastLoadWarning is { } settingsWarning)
        {
            MessageBox.Show(settingsWarning, "JARVIS settings recovery", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        _appliedMic = Settings.MicDevice;
        _appliedSpeaker = Settings.SpeakerDevice;
        _appliedWakePhrase = Settings.WakePhrase;
        Events = new EventBus(Paths.Root);
        Memory = new MemoryStore(Paths.Root, Settings.UserName);
        Memory.SeedNotesIfMissing();

        Jarvis = new Orchestrator(Settings, Events, Memory);
        Annotations = new AnnotationOverlay(Dispatcher);
        Jarvis.Services.Annotations = Annotations;
        Jarvis.Services.Ui = new ShellUi(this);

        ScreenCapture.BeforeCapture = async () =>
        {
            if (Hud is not null) await Hud.HideForCaptureAsync();
            if (Annotations is not null) Annotations.Clear();
        };
        ScreenCapture.AfterCapture = () =>
        {
            Hud?.RestoreAfterCapture();
            return Task.CompletedTask;
        };

        ViewModel = new MainViewModel(Jarvis, Settings, Events, Memory, Dispatcher);
        Deck = new MainWindow(ViewModel);
        Deck.Show();

        Hud = new HudManager(Settings, Jarvis);
        Tray = new TrayIcon();
        WireTray();

        Hotkeys = new HotkeyManager();
        Hotkeys.TalkPressed += () => Fire(Jarvis.TalkAsync);
        Hotkeys.StopPressed += () => Fire(Jarvis.StopAsync);
        Hotkeys.DeckPressed += ShowDeck;

        ApplySettings();
        _ = StartJarvisAsync();
    }

    private void WireTray()
    {
        if (Tray is null) return;
        Tray.OpenRequested += ShowDeck;
        Tray.TalkRequested += () => Fire(Jarvis.TalkAsync);
        Tray.StopRequested += () => Fire(Jarvis.StopAsync);
        Tray.SettingsRequested += ShowSettings;
        Tray.QuitRequested += () => Dispatcher.BeginInvoke(new Action(() => _ = ExitAsync()));
        Tray.PauseToggled += () =>
        {
            ViewModel.PauseCommand.Execute(null);
            if (Tray is not null)
            {
                Tray.SetPaused(Jarvis.Paused);
                Tray.SetTooltip(Jarvis.Paused ? "JARVIS — paused" : "JARVIS — online");
            }
        };
    }

    private async Task StartJarvisAsync()
    {
        try
        {
            await Jarvis.StartAsync().ConfigureAwait(true);
            if (_exiting) return;
            Tray?.SetTooltip("JARVIS — online");
        }
        catch (Exception ex)
        {
            Log.Error("JARVIS failed to start", ex);
            Tray?.Notify("JARVIS could not start", ex.Message, warning: true);
        }
    }

    public void ApplySettings()
    {
        try
        {
            // Only restart voice hardware when the choice actually changed: rebuilding the microphone
            // on every save would make the assistant go deaf for a second for no reason.
            var micChanged = !string.Equals(_appliedMic, Settings.MicDevice, StringComparison.Ordinal);
            var phraseChanged = !string.Equals(_appliedWakePhrase, Settings.WakePhrase.Trim(),
                StringComparison.OrdinalIgnoreCase);
            if (micChanged || phraseChanged)
            {
                _appliedMic = Settings.MicDevice;
                _appliedWakePhrase = Settings.WakePhrase;
                Jarvis.Ears.Reload();
            }
            if (!string.Equals(_appliedSpeaker, Settings.SpeakerDevice, StringComparison.Ordinal))
            {
                _appliedSpeaker = Settings.SpeakerDevice;
                Jarvis.Tts.ReloadOutput();
            }
            Jarvis.Tts.SetVolume(Settings.TtsVolume);
            Jarvis.Tts.Enabled = Settings.SpeakReplies;
            Jarvis.Ears.Muted = !Settings.WakeEnabled;
            Hud?.Rebuild();
            Hotkeys?.Register(Settings);
            Tray?.SetTooltip(Jarvis.Paused ? "JARVIS — paused" : "JARVIS — online");
            Tray?.SetPaused(Jarvis.Paused);
            SetAutoStart(Settings.StartWithWindows);
            ViewModel.NotifySettingsChanged();
        }
        catch (Exception ex)
        {
            Log.Warn("apply settings failed", ex);
        }
    }

    public void ShowDeck()
    {
        if (Deck is null) return;
        if (!Deck.IsVisible) Deck.Show();
        if (Deck.WindowState == WindowState.Minimized) Deck.WindowState = WindowState.Normal;
        Deck.Activate();
        Deck.Topmost = true;
        Deck.Topmost = false;
        Deck.Focus();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings);
        if (Deck is { IsVisible: true })
        {
            _settingsWindow.Owner = Deck;
            _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ViewModel.RefreshMemory();
        };
        _settingsWindow.Show();
    }

    public void NotifyHidden()
    {
        if (_hiddenNotified) return;
        _hiddenNotified = true;
        Tray?.Notify("JARVIS is still running",
            "I am in the notification area. Double-click the icon to bring the deck back.");
    }

    private async Task ExitAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(ExitAsync).Task.Unwrap();
            return;
        }
        if (_exiting) return;
        _exiting = true;
        try { Hud?.Dispose(); } catch { }
        try { Hotkeys?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { Annotations?.Dispose(); } catch { }
        try { await Jarvis.ShutdownAsync().ConfigureAwait(true); } catch { }
        try { await Jarvis.DisposeAsync().ConfigureAwait(true); } catch { }
        try { Deck?.PrepareExit(); } catch { }
        Shutdown();
    }

    private void Fire(Func<Task> action)
    {
        _ = Task.Run(async () =>
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("background command failed", ex); }
        });
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path)) key.SetValue("JARVIS", $"\"{path}\"");
            }
            else
            {
                key.DeleteValue("JARVIS", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("autostart update failed: " + ex.Message);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try { Jarvis?.ShutdownAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMutex && _mutex is not null) _mutex.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        Log.Info("=== JARVIS stopped ===");
        base.OnExit(e);
    }
}
