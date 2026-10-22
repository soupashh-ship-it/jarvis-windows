using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Jarvis.Core;

namespace Jarvis.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class TimelineItem : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Kind { get; init; } = "system";
    public string Header { get; init; } = "";
    public string Body { get; set; } = "";
    public string Meta { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public Brush Accent { get; init; } = Brushes.Gray;
    public string Glyph { get; init; } = "";
    public bool HasGlyph => Glyph.Length > 0;
    public string TimeText => At.ToLocalTime().ToString("HH:mm:ss");

    public void UpdateBody(string body)
    {
        Body = body;
        Raise(nameof(Body));
    }
}

public sealed class WorkerVm : ObservableObject
{
    public required string Name { get; init; }
    public string State { get; set; } = "running";
    public string Detail { get; set; } = "";
    public string Elapsed { get; set; } = "";
    public bool IsLive => State is "running" or "starting" or "waiting";
    public Brush StateBrush => State switch
    {
        "done" => Palette.Green,
        "waiting" => Palette.Amber,
        "failed" => Palette.Red,
        "stopped" => Palette.Muted,
        _ => Palette.Accent
    };
    public string StateLabel => State switch
    {
        "starting" => "Starting",
        "running" => "Running",
        "waiting" => "Needs you",
        "done" => "Finished",
        "failed" => "Failed",
        "stopped" => "Stopped",
        _ => State
    };

    public void Refresh()
    {
        Raise(nameof(State));
        Raise(nameof(Detail));
        Raise(nameof(Elapsed));
        Raise(nameof(IsLive));
        Raise(nameof(StateBrush));
        Raise(nameof(StateLabel));
    }
}

public static class Palette
{
    public static readonly Brush Accent = Make("#E8A57C");
    public static readonly Brush Cool = Make("#A9C7E8");
    public static readonly Brush Blue = Make("#93B8E8");
    public static readonly Brush Amber = Make("#EFC08A");
    public static readonly Brush Green = Make("#A8D8C0");
    public static readonly Brush Red = Make("#E79A9A");
    public static readonly Brush Muted = Make("#73808D");
    public static readonly Brush Dim = Make("#A9B4BE");
    public static readonly Brush Text = Make("#F1F4F7");
    public static readonly Brush White = Make("#FFFFFF");

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>All of the deck's state. The window stays dumb; this decides what is on screen.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Orchestrator _jarvis;
    private readonly AppSettings _settings;
    private readonly EventBus _events;
    private readonly MemoryStore _memory;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<IDisposable> _subscriptions = new();
    private TimelineItem? _liveAssistant;
    private DateTime _lastSystemSample = DateTime.MinValue;

    private string _inputText = "";
    private string _activity = "idle";
    private string _stateLabel = "Standing by";
    private string _stepText = "";
    private string _liveUser = "";
    private string _liveReply = "";
    private string _pendingQuestion = "";
    private string _systemLine = "";
    private string _memoryPreview = "";
    private string _voiceStatus = "";
    private double _micLevel;
    private double _outputLevel;
    private int _turns;
    private int _messages;
    private bool _hasPendingQuestion;
    private bool _isPaused;
    private bool _showSetup;
    private string _setupName = "";
    private string _setupHonorific = "sir";
    private string _setupCity = "London";
    private string _setupPreset = "deepseek";
    private string _setupApiKey = "";
    private string _uptime = "0m";
    private string _modelName = "";
    private string _ttsEngine = "";
    private int _toolCount;

    public MainViewModel(Orchestrator jarvis, AppSettings settings, EventBus events, MemoryStore memory, Dispatcher dispatcher)
    {
        _jarvis = jarvis;
        _settings = settings;
        _events = events;
        _memory = memory;
        _dispatcher = dispatcher;

        SendCommand = new RelayCommand(Send, () => !string.IsNullOrWhiteSpace(InputText));
        TalkCommand = new RelayCommand(() => _ = Safe(_jarvis.TalkAsync()));
        StopCommand = new RelayCommand(() => _ = Safe(_jarvis.StopAsync()));
        NewSessionCommand = new RelayCommand(() => _ = Safe(_jarvis.NewSessionAsync()));
        PauseCommand = new RelayCommand(() =>
        {
            IsPaused = !IsPaused;
            _jarvis.SetPaused(IsPaused);
        });
        AllowCommand = new RelayCommand(() => Answer(ApprovalOutcome.Allowed));
        DenyCommand = new RelayCommand(() => Answer(ApprovalOutcome.Denied));
        ClearCommand = new RelayCommand(() => Timeline.Clear());
        RefreshMemoryCommand = new RelayCommand(RefreshMemory);
        OpenNotesCommand = new RelayCommand(OpenNotes);
        SaveSetupCommand = new RelayCommand(() => _ = SaveSetupAsync());
        SkipSetupCommand = new RelayCommand(() => _ = SkipSetupAsync());

        _setupName = settings.UserName;
        _setupHonorific = settings.Honorific;
        _setupCity = settings.City;
        _setupPreset = settings.Preset;
        _modelName = jarvis.ModelName;
        _ttsEngine = jarvis.TtsEngine;
        _toolCount = jarvis.Tools.Count;
        _voiceStatus = jarvis.VoiceStatus;
        _showSetup = !settings.SetupComplete;
        RefreshMemory();

        // Replay only events from this process, never last night's errors.
        var since = DateTimeOffset.Now - _clock.Elapsed;
        foreach (var ev in _events.History.Where(e => e.At >= since).TakeLast(40)) Handle(ev, replay: true);
        _subscriptions.Add(_events.Subscribe(ev => _dispatcher.BeginInvoke(() => Handle(ev, replay: false))));
        _jarvis.StateChanged += OnAssistantStateChanged;

        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _ticker.Start();
        RefreshLive();
    }

    private void OnAssistantStateChanged(AssistantState state) => _dispatcher.BeginInvoke(RefreshLive);

    public ObservableCollection<TimelineItem> Timeline { get; } = new();
    public ObservableCollection<WorkerVm> Workers { get; } = new();

    public ICommand SendCommand { get; }
    public ICommand TalkCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NewSessionCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand AllowCommand { get; }
    public ICommand DenyCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshMemoryCommand { get; }
    public ICommand OpenNotesCommand { get; }
    public ICommand SaveSetupCommand { get; }
    public ICommand SkipSetupCommand { get; }

    public event Action? SettingsRequested;
    public event Action? TimelineAppended;
    public event Action? SetupSettingsRequested;

    public string InputText
    {
        get => _inputText;
        set { if (Set(ref _inputText, value)) Raise(nameof(CanSend)); }
    }
    public bool CanSend => !string.IsNullOrWhiteSpace(InputText);
    public string Activity { get => _activity; private set => Set(ref _activity, value); }
    public string StateLabel { get => _stateLabel; private set => Set(ref _stateLabel, value); }
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    public bool HasStep => !string.IsNullOrWhiteSpace(StepText);
    public string LiveUser { get => _liveUser; private set => Set(ref _liveUser, value); }
    public string LiveReply { get => _liveReply; private set => Set(ref _liveReply, value); }
    public string PendingQuestion { get => _pendingQuestion; private set => Set(ref _pendingQuestion, value); }
    public bool HasPendingQuestion { get => _hasPendingQuestion; private set => Set(ref _hasPendingQuestion, value); }
    public string SystemLine { get => _systemLine; private set => Set(ref _systemLine, value); }
    public string MemoryPreview { get => _memoryPreview; private set => Set(ref _memoryPreview, value); }
    public string VoiceStatus { get => _voiceStatus; private set => Set(ref _voiceStatus, value); }
    public double MicLevel { get => _micLevel; private set => Set(ref _micLevel, value); }
    public double OutputLevel { get => _outputLevel; private set => Set(ref _outputLevel, value); }
    public int Turns { get => _turns; private set => Set(ref _turns, value); }
    public int Messages { get => _messages; private set => Set(ref _messages, value); }
    public bool IsPaused { get => _isPaused; private set => Set(ref _isPaused, value); }
    public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
    public string TtsEngine { get => _ttsEngine; private set => Set(ref _ttsEngine, value); }
    public int ToolCount { get => _toolCount; private set => Set(ref _toolCount, value); }
    public string SessionCount => $"{Turns} turn{(Turns == 1 ? "" : "s")}";
    public string HotkeyTalk => _settings.HotkeyTalk;
    public string HotkeyStop => _settings.HotkeyStop;
    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "BURNING THE MIDNIGHT OIL",
        < 12 => "GOOD MORNING",
        < 18 => "GOOD AFTERNOON",
        _ => "GOOD EVENING"
    };

    public string StateWord => Activity switch
    {
        "listening" => "Listening",
        "thinking" => "Thinking",
        "speaking" => "Speaking",
        "waiting" => "Waiting",
        "background" => "Working",
        "paused" => "Paused",
        _ => "Idle"
    };

    public string CardTitle => Activity switch
    {
        "listening" => "Listening…",
        "thinking" => _jarvis.StepText.Length > 0 ? "Working…" : "Thinking…",
        "speaking" => "Speaking…",
        "waiting" => "Permission needed",
        "background" => "Working in the background",
        "paused" => "Paused",
        _ => "Standing by"
    };

    public string CardSubtitle => Activity switch
    {
        "listening" => _jarvis.LiveTranscript.Length > 0 ? _jarvis.LiveTranscript : "Go ahead, " + _settings.Honorific + ".",
        "thinking" => _jarvis.StepText.Length > 0 ? _jarvis.StepText : "Analysing your request",
        "speaking" => _jarvis.ReplyText,
        "waiting" => _jarvis.PendingQuestion,
        "background" => "A background task is running",
        "paused" => "Listening is paused",
        _ => _settings.HasModel ? "Connected to " + _settings.Model : $"Local mode · {_toolCount} tools ready"
    };

    public string CardGlyph => Activity switch
    {
        "listening" => "\uE720",
        "thinking" => "\uE945",
        "speaking" => "\uE767",
        "waiting" => "\uE72E",
        "background" => "\uE7C3",
        "paused" => "\uE769",
        _ => "\uE946"
    };

    public string HeroSubtitle => Activity == "idle"
        ? "Say “Hey Jarvis”, or press " + _settings.HotkeyTalk
        : CardSubtitle;

    public bool HasWorkers => Workers.Count > 0;

    public Brush ActivityBrush => Activity switch
    {
        "listening" => Palette.White,
        "thinking" => Palette.Cool,
        "speaking" => Palette.Blue,
        "waiting" => Palette.Amber,
        "background" => Palette.Green,
        "paused" => Palette.Muted,
        _ => Palette.Muted
    };

    /// <summary>Mic while listening, speaker output otherwise: the level the status visual should follow.</summary>
    public double ActivityLevel => Activity == "listening" ? MicLevel : OutputLevel;

    public bool ShowSetup { get => _showSetup; private set => Set(ref _showSetup, value); }
    public string SetupName { get => _setupName; set => Set(ref _setupName, value); }
    public string SetupHonorific { get => _setupHonorific; set => Set(ref _setupHonorific, value); }
    public string SetupCity { get => _setupCity; set => Set(ref _setupCity, value); }
    public string SetupPreset { get => _setupPreset; set => Set(ref _setupPreset, value); }
    public string SetupApiKey { get => _setupApiKey; set => Set(ref _setupApiKey, value); }
    public IReadOnlyList<ProviderPreset> PresetList => Presets.All;
    public string ProviderHint => Presets.Find(SetupPreset).KeyHint;
    public string ProviderKeyUrl => Presets.Find(SetupPreset).KeyUrl;

    private void Tick()
    {
        try
        {
            var activity = _jarvis.Activity;
            if (activity != Activity)
            {
                Activity = activity;
                StateLabel = activity switch
                {
                    "listening" => "Listening",
                    "thinking" => "Working",
                    "speaking" => "Speaking",
                    "waiting" => "Awaiting your answer",
                    "background" => "Background work",
                    "paused" => "Paused",
                    _ => "Standing by"
                };
                Raise(nameof(ActivityBrush));
                Raise(nameof(StateLabel));
            }

            MicLevel = _jarvis.MicLevel;
            OutputLevel = _jarvis.OutputLevel;
            StepText = _jarvis.StepText;
            LiveUser = _jarvis.UserText;
            LiveReply = _jarvis.ReplyText;
            Turns = _jarvis.SessionTurns;
            Messages = _jarvis.SessionMessages;
            ModelName = _jarvis.ModelName;
            TtsEngine = _jarvis.TtsEngine;
            VoiceStatus = _jarvis.VoiceStatus;
            Raise(nameof(StateWord));
            Raise(nameof(CardTitle));
            Raise(nameof(CardSubtitle));
            Raise(nameof(CardGlyph));
            Raise(nameof(HeroSubtitle));
            Raise(nameof(ActivityLevel));

            var question = _jarvis.PendingQuestion;
            if (question != PendingQuestion) { PendingQuestion = question; HasPendingQuestion = question.Length > 0; }

            var elapsed = _clock.Elapsed;
            Uptime = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m"
                : $"{elapsed.Minutes}m {elapsed.Seconds:00}s";

            if ((DateTime.UtcNow - _lastSystemSample).TotalSeconds > 3)
            {
                _lastSystemSample = DateTime.UtcNow;
                var mem = SystemProbe.Memory();
                var battery = SystemProbe.Battery();
                SystemLine = $"CPU {SystemProbe.CpuPercent():0}%  ·  RAM {mem.Percent:0}%"
                             + (battery.HasBattery ? $"  ·  {battery.Percent:0}%" : "");
                Raise(nameof(SystemLine));
            }

            RefreshWorkers();
        }
        catch (Exception ex) { Log.Debug("ui tick: " + ex.Message); }
    }

    private void RefreshWorkers()
    {
        var live = _jarvis.Workers.All;
        foreach (var worker in live)
        {
            var vm = Workers.FirstOrDefault(w => w.Name == worker.Name);
            if (vm is null)
            {
                vm = new WorkerVm { Name = worker.Name };
                Workers.Insert(0, vm);
            }
            vm.State = worker.State;
            vm.Detail = worker.LastMessage.Length > 160 ? worker.LastMessage[..160] + "…" : worker.LastMessage;
            var minutes = (DateTimeOffset.Now - worker.Started).TotalMinutes;
            vm.Elapsed = minutes < 1 ? "<1m" : $"{minutes:0}m";
            vm.Refresh();
        }

        // Keep a short history of finished jobs; the live ones are always shown.
        while (Workers.Count > 12)
        {
            var stale = Workers.LastOrDefault(w => !w.IsLive);
            if (stale is null) break;
            Workers.Remove(stale);
        }
        Raise(nameof(HasWorkers));
    }

    private void RefreshLive()
    {
        try
        {
            Activity = _jarvis.Activity;
            StateLabel = Activity switch
            {
                "listening" => "Listening", "thinking" => "Working", "speaking" => "Speaking",
                "waiting" => "Awaiting your answer", "background" => "Background work",
                "paused" => "Paused", _ => "Standing by"
            };
            Raise(nameof(ActivityBrush));
            Raise(nameof(StateWord));
            Raise(nameof(CardTitle));
            Raise(nameof(CardSubtitle));
            Raise(nameof(CardGlyph));
            Raise(nameof(HeroSubtitle));
            Raise(nameof(ActivityLevel));
        }
        catch { }
    }

    public void Send()
    {
        var text = InputText.Trim();
        if (text.Length == 0) return;
        InputText = "";
        _ = Safe(_jarvis.SubmitAsync(text, "typed"));
    }

    private void Answer(ApprovalOutcome outcome)
    {
        _jarvis.AnswerApproval(outcome);
        HasPendingQuestion = false;
    }

    public void RefreshMemory()
    {
        var notes = _memory.ReadNotes().Trim();
        MemoryPreview = notes.Length == 0 ? "No notes yet. I will keep a running summary here." :
            notes.Length > 1400 ? notes[..1400] + "…" : notes;
    }

    /// <summary>Refreshes the deck after Settings saves, including values bound only once.</summary>
    public void NotifySettingsChanged()
    {
        RefreshMemory();
        Raise(nameof(HotkeyTalk));
        Raise(nameof(HotkeyStop));
        Raise(nameof(Greeting));
        Raise(nameof(HeroSubtitle));
        Raise(nameof(CardSubtitle));
    }

    private void OpenNotes()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_memory.NotesPath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open notes failed", ex); }
    }

    public void RequestSettings() => SettingsRequested?.Invoke();

    private async Task SaveSetupAsync()
    {
        try
        {
            var preset = Presets.Find(SetupPreset);
            _settings.UserName = string.IsNullOrWhiteSpace(SetupName) ? "Tony" : SetupName.Trim();
            _settings.Honorific = string.IsNullOrWhiteSpace(SetupHonorific) ? "sir" : SetupHonorific.Trim();
            _settings.City = string.IsNullOrWhiteSpace(SetupCity) ? "London" : SetupCity.Trim();

            if (SetupApiKey.Trim().Length > 0 || preset.KeyOptional)
            {
                Presets.Apply(_settings, preset);
                if (SetupApiKey.Trim().Length > 0) _settings.ApiKey = SetupApiKey.Trim();
            }
            _settings.SetupComplete = true;
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.SetupComplete = false;
            Log.Warn("setup settings could not be saved", ex);
            AddTimeline(new TimelineItem
            {
                Kind = "error",
                Header = "SETUP NOT SAVED",
                Body = "Windows could not protect or save the settings. " + ex.Message,
                Accent = Palette.Red
            });
            return;
        }
        ShowSetup = false;
        _events.Emit("setup_complete", null, true, ("provider", _settings.Preset));
        AddTimeline(new TimelineItem
        {
            Kind = "system",
            Header = "SETUP COMPLETE",
            Body = _settings.HasModel
                ? $"Brain connected: {_settings.Model}. Voice, desktop tools and memory are ready."
                : "Local mode is active. Add an API key in Settings whenever you want the full brain.",
            Accent = Palette.Green
        });
        await Safe(_jarvis.NewSessionAsync(silent: true));
        await Safe(_jarvis.TalkAsync()).ConfigureAwait(false);
    }

    private Task SkipSetupAsync()
    {
        _settings.SetupComplete = true;
        SettingsStore.Save(_settings);
        ShowSetup = false;
        _events.Emit("setup_complete", null, true, ("mode", "local"));
        AddTimeline(new TimelineItem
        {
            Kind = "system",
            Header = "LOCAL MODE",
            Body = "No API key, so I am running the offline brain: apps, windows, volume, media, timers, memory and system reports. " +
                   "Add a key in Settings for full reasoning.",
            Accent = Palette.Amber
        });
        return Task.CompletedTask;
    }

    public void RequestOpenSettingsFromSetup() => SetupSettingsRequested?.Invoke();

    private void Handle(JarvisEvent ev, bool replay)
    {
        try
        {
            switch (ev.Kind)
            {
                case "started":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "system", Header = "ONLINE",
                        Body = $"JARVIS is running. {ev.Str("tools")} tools, model {ev.Str("model")}, voice {ev.Str("voice")}.",
                        Accent = Palette.Green
                    });
                    break;

                case "turn_start":
                    _liveAssistant = null;
                    AddTimeline(new TimelineItem
                    {
                        Kind = "user",
                        Header = ev.Str("source") == "worker" ? "WORKER" : "YOU",
                        Body = ev.Text ?? "",
                        Accent = Palette.Dim,
                        Glyph = "\uE77B"
                    });
                    break;

                case "say":
                {
                    var sentence = ev.Text ?? "";
                    if (sentence.Length == 0) break;
                    if (_liveAssistant is null)
                    {
                        _liveAssistant = new TimelineItem
                        {
                            Kind = "assistant", Header = "JARVIS", Body = sentence,
                            Accent = Palette.Blue, Glyph = "\uE9D9"
                        };
                        AddTimeline(_liveAssistant);
                    }
                    else
                    {
                        _liveAssistant.UpdateBody((_liveAssistant.Body + " " + sentence).Trim());
                    }
                    break;
                }

                case "turn_end":
                {
                    var text = ev.Text ?? "";
                    if (_liveAssistant is null && text.Length > 0)
                    {
                        AddTimeline(new TimelineItem
                        {
                            Kind = "assistant", Header = "JARVIS", Body = text,
                            Accent = Palette.Blue, Glyph = "\uE9D9"
                        });
                    }
                    else if (_liveAssistant is not null && text.Length > 0)
                    {
                        _liveAssistant.UpdateBody(text);
                    }
                    _liveAssistant = null;
                    break;
                }

                case "tool_call":
                {
                    var name = ev.Str("name") ?? "tool";
                    AddTimeline(new TimelineItem
                    {
                        Kind = "tool",
                        Header = Humanize(name),
                        Body = CompactInput(ev.Str("input") ?? ""),
                        Meta = name,
                        Accent = Palette.Accent,
                        Glyph = "\uE943"
                    });
                    break;
                }

                case "tool_result":
                {
                    var ok = ev.Str("ok") == "True" || ev.Str("ok") == "true";
                    var preview = ev.Str("preview") ?? "";
                    AddTimeline(new TimelineItem
                    {
                        Kind = ok ? "tool-ok" : "tool-err",
                        Header = ok ? "RESULT" : "FAILED",
                        Body = preview.Length > 420 ? preview[..420] + "…" : preview,
                        Meta = ev.Str("ms") ?? "",
                        Accent = ok ? Palette.Muted : Palette.Red,
                        Glyph = ok ? "\uE73E" : "\uE7BA"
                    });
                    break;
                }

                case "approval_ask":
                    HasPendingQuestion = true;
                    PendingQuestion = ev.Text ?? "";
                    AddTimeline(new TimelineItem
                    {
                        Kind = "approval", Header = "PERMISSION", Body = ev.Text ?? "",
                        Accent = Palette.Amber, Glyph = "\uE72E"
                    });
                    break;

                case "approval_heard":
                case "approval_answered":
                    HasPendingQuestion = false;
                    break;

                case "worker":
                {
                    var name = ev.Str("name") ?? "worker";
                    var state = ev.Str("state") ?? "running";
                    var vm = Workers.FirstOrDefault(w => w.Name == name);
                    if (vm is null) { vm = new WorkerVm { Name = name }; Workers.Insert(0, vm); }
                    vm.State = state;
                    vm.Detail = ev.Text ?? "";
                    vm.Refresh();
                    break;
                }

                case "worker_finished":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "system", Header = "WORKER", Body = ev.Text ?? "",
                        Accent = Palette.Green, Glyph = "\uE7C3"
                    });
                    break;

                case "polite_hold":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "system", Header = "HELD",
                        Body = $"Holding an update until your call with {ev.Str("app")} is quiet.",
                        Accent = Palette.Green
                    });
                    break;

                case "session_reset":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "system", Header = "NEW SESSION",
                        Body = $"Fresh session. Previous session had {ev.Str("turns")} turn(s); notes filed.",
                        Accent = Palette.Muted
                    });
                    break;

                case "error":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "error", Header = "ERROR", Body = ev.Text ?? "",
                        Accent = Palette.Red, Glyph = "\uEA39"
                    });
                    break;

                case "voice_status":
                    VoiceStatus = ev.Text ?? "";
                    break;

                case "screenshot":
                    if (!replay)
                    {
                        // The image itself lives in the shots folder; one quiet line is enough.
                        AddTimeline(new TimelineItem
                        {
                            Kind = "system", Header = "SCREEN",
                            Body = $"Captured {ev.Str("width")}×{ev.Str("height")} ({ev.Text}).",
                            Accent = Palette.Muted
                        });
                    }
                    break;

                case "barge_in":
                    AddTimeline(new TimelineItem
                    {
                        Kind = "system", Header = "INTERRUPTED",
                        Body = "You spoke; I stopped.",
                        Accent = Palette.Muted
                    });
                    break;
            }
        }
        catch (Exception ex) { Log.Warn("timeline event failed", ex); }
    }

    private void AddTimeline(TimelineItem item)
    {
        Timeline.Add(item);
        while (Timeline.Count > 400) Timeline.RemoveAt(0);
        TimelineAppended?.Invoke();
    }

    private static string Humanize(string tool) => tool.Replace('_', ' ').ToUpperInvariant();

    private static string CompactInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var parts = new List<string>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => property.Value.GetString() ?? "",
                    System.Text.Json.JsonValueKind.Number => property.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.True => "true",
                    System.Text.Json.JsonValueKind.False => "false",
                    _ => property.Value.GetRawText()
                };
                if (value.Length > 140) value = value[..140] + "…";
                parts.Add($"{property.Name}: {value}");
                if (parts.Count >= 3) break;
            }
            return string.Join("   ", parts);
        }
        catch { return json.Length > 200 ? json[..200] : json; }
    }

    private async Task Safe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Warn("command failed", ex);
            _ = _dispatcher.BeginInvoke(() => AddTimeline(new TimelineItem
            {
                Kind = "error", Header = "ERROR", Body = ex.Message, Accent = Palette.Red
            }));
        }
    }
}
