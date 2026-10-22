using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Jarvis.Core;

namespace Jarvis.App;

public partial class SettingsWindow : Window
{
    private static readonly string[] EdgeVoices =
    {
        "en-GB-RyanNeural", "en-GB-SoniaNeural", "en-GB-LibbyNeural", "en-GB-MaisieNeural", "en-GB-ThomasNeural",
        "en-US-GuyNeural", "en-US-AriaNeural", "en-US-JennyNeural", "en-AU-WilliamNeural", "en-IE-ConnorNeural"
    };

    private static readonly string[] HudMonitors = { "primary", "secondary", "active", "mouse" };

    /// <summary>The Windows voices worth hearing.</summary>
    private static readonly string[] GoodVoices =
    {
        "George", "Catherine", "Harry", "Thomas", "Libby", "Maisie", "Sonia", "Ryan", "Hazel",
        "Aria", "Jenny", "Guy", "Eric", "Andrew", "Emma", "Brian", "Ava", "Michelle", "Nancy",
        "Ashley", "Amanda", "Roger", "Alex", "Tony", "Vicki", "Sara", "Marta", "Elsa",
        "Natasha", "William", "Neerja", "Prabhat", "Hemant", "Ananya", "Swara", "Madhur", "Gautam",
        "Hortense", "Klara", "Conchita", "Coral", "Luciana", "Nanami", "Hisako", "Yunhi", "Heera"
    };

    private readonly AppSettings _settings;
    private AppSettings _snapshot;
    private readonly DeviceList _micDevices = new();
    private readonly DeviceList _speakerDevices = new();
    private readonly MicrophoneCapture _meter = new();
    private readonly DispatcherTimer _meterTimer;
    private DateTime _meterSilentSince = DateTime.MinValue;
    private bool _meterHeard;
    private bool _meterHinted;
    private bool _loading = true;
    private string _hotkeyError = "";

    /// <summary>A model in the picker: the id that is sent, and a label to read.</summary>
    private sealed record ModelChoice(string Id, string Label);
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _snapshot = settings.Clone();

        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(680, area.Width - 60));
        Height = Math.Min(Height, Math.Max(600, area.Height - 60));

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _meterTimer.Tick += MicMeter_Tick;

        Load();
        _loading = false;

        Loaded += (_, _) => StartMicMeter();

        PreviewKeyDown += Settings_PreviewKeyDown;
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            try { _meter.Dispose(); } catch { }
            // Cancel means cancel: this window edits the live object the orchestrator holds, so put
            // every value back to the last committed state rather than leaking half-typed settings.
            _settings.CopyFrom(_snapshot);
            if (Application.Current is App app)
            {
                try { app.ApplySettings(); }
                catch { }
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var round = 2;
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        }
        catch { }
    }

    private void Load()
    {
        NameBox.Text = _settings.UserName;
        HonorificBox.Text = _settings.Honorific;
        CityBox.Text = _settings.City;

        PresetCombo.ItemsSource = Presets.All;
        PresetCombo.SelectedItem = Presets.Find(_settings.Preset);
        BaseUrlBox.Text = _settings.BaseUrl;
        ModelBox.Text = _settings.Model;
        TempBox.Text = _settings.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
        TokensBox.Text = _settings.MaxTokens.ToString(CultureInfo.InvariantCulture);
        UpdateGeminiApiModels(_settings.Model);
        UpdateProviderHints();

        TtsCombo.ItemsSource = new[] { "Windows voices (offline)", "Azure neural voices" };
        TtsCombo.SelectedIndex = _settings.Tts == TtsEngine.Azure ? 1 : 0;
        RateBox.Text = _settings.SpeechRate.ToString("0.00", CultureInfo.InvariantCulture);
        VolumeBox.Text = _settings.TtsVolume.ToString(CultureInfo.InvariantCulture);
        SpeakRepliesCheck.IsChecked = _settings.SpeakReplies;

        EdgeVoiceCombo.ItemsSource = EdgeVoices;
        EdgeVoiceCombo.SelectedItem = EdgeVoices.Contains(_settings.AzureVoice) ? _settings.AzureVoice : EdgeVoices[0];
        AzureRegionBox.Text = _settings.AzureRegion;

        var sapi = new List<string> { "(system default)" };
        sapi.AddRange(GoodVoicesOnly(SapiTts.Voices()));
        SapiVoiceCombo.ItemsSource = sapi;
        SapiVoiceCombo.SelectedItem = sapi.Contains(_settings.SapiVoice) ? _settings.SapiVoice : "(system default)";
        UpdateVoicePanels();
        RefreshDeviceLists();

        WakeCheck.IsChecked = _settings.WakeEnabled;
        WakePhraseBox.Text = _settings.WakePhrase;
        WakeConfBox.Text = _settings.WakeConfidence.ToString("0.00", CultureInfo.InvariantCulture);
        FollowUpBox.Text = _settings.FollowUpSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        BargeInCheck.IsChecked = _settings.BargeIn;

        var gain = Math.Clamp(_settings.MicGain, 0.5, 4.0);
        MicGainSlider.Value = gain;
        MicGainText.Text = gain.ToString("0.0", CultureInfo.InvariantCulture) + "x";

        HudMonitorCombo.ItemsSource = HudMonitors;
        HudMonitorCombo.SelectedItem = HudMonitors.Contains(_settings.HudMonitor) ? _settings.HudMonitor : "primary";
        HudCheck.IsChecked = _settings.ShowHud;
        AllMonitorsCheck.IsChecked = _settings.ShowOnAllMonitors;
        GreetCheck.IsChecked = _settings.GreetOnStart;
        StartupCheck.IsChecked = _settings.StartWithWindows;
        HotkeyTalkBox.Text = _settings.HotkeyTalk;
        HotkeyStopBox.Text = _settings.HotkeyStop;
        HotkeyDeckBox.Text = _settings.HotkeyDeck;

        ConfirmCheck.IsChecked = _settings.RequireConfirmForRisky;
        VoiceConfirmCheck.IsChecked = _settings.ConfirmByVoice;
        PoliteCheck.IsChecked = _settings.PoliteMode;
        NetworkCheck.IsChecked = _settings.AllowNetwork;
        WriteDirsBox.Text = string.Join(Environment.NewLine, _settings.WriteOkDirs);

        DataPathText.Text = Paths.Root;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = $"JARVIS {version}  ·  settings.json lives here";
        FooterStatus.Text = _settings.HasModel ? $"Brain: {_settings.Model}" : "Local mode: no API key configured.";
    }

    private void RefreshDeviceLists()
    {
        // On first load the boxes have no selection yet, so fall back to the saved values.
        var mic = MicCombo.SelectedIndex >= 0 ? _micDevices.Selected(MicCombo) : _settings.MicDevice;
        var speaker = SpeakerCombo.SelectedIndex >= 0 ? _speakerDevices.Selected(SpeakerCombo) : _settings.SpeakerDevice;

        // Microphones are stored by Windows' endpoint id, which survives renumbering and re-plugging.
        var mics = AudioDevices.Inputs().Select(d => (d.Id, d.Name)).ToList();
        var speakers = AudioDevices.Outputs().Select(d => (d.Name, d.Name)).ToList();
        _micDevices.Fill(MicCombo, mics, mic, "(system default microphone)");
        _speakerDevices.Fill(SpeakerCombo, speakers, speaker, "(system default speaker)");
    }

    private void UpdateVoicePanels()
    {
        var azure = TtsCombo.SelectedIndex == 1;
        EdgeVoiceCombo.IsEnabled = azure;
        AzureKeyBox.IsEnabled = azure;
        AzureRegionBox.IsEnabled = azure;
        SapiVoiceCombo.IsEnabled = !azure;
    }

    private void Tts_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateVoicePanels();
        if (TtsCombo.SelectedIndex == 1 && AzureKeyBox.Password.Length == 0 &&
            string.IsNullOrWhiteSpace(_settings.AzureKey))
            VoiceStatus.Text = "Paste an Azure Speech key to use a neural voice; the Windows voice remains the fallback.";
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedItem is not ProviderPreset preset) return;
        BaseUrlBox.Text = preset.BaseUrl;
        ModelBox.Text = preset.Model;
        _settings.Model = preset.Model;
        UpdateGeminiApiModels(preset.Model);
        FooterStatus.Text = preset.KeyOptional
            ? "Local endpoint: no API key needed."
            : $"Paste a key from {preset.KeyUrl}";
        UpdateProviderHints();
    }

    /// <summary>Explains where the key comes from and which models the provider offers.</summary>
    private void UpdateProviderHints()
    {
        if (PresetCombo.SelectedItem is not ProviderPreset preset)
        {
            KeyHintText.Text = "";
            ModelHintText.Text = "";
            GetKeyButton.IsEnabled = false;
            ModelBox.Visibility = Visibility.Visible;
            GeminiApiModelCombo.Visibility = Visibility.Collapsed;
            return;
        }

        var geminiApi = preset.Id == "gemini";
        var antigravity = preset.Id == "antigravity-cli";
        ModelBox.Visibility = geminiApi ? Visibility.Collapsed : Visibility.Visible;
        GeminiApiModelCombo.Visibility = geminiApi ? Visibility.Visible : Visibility.Collapsed;
        KeyBlock.Visibility = antigravity ? Visibility.Collapsed : Visibility.Visible;

        GetKeyButton.IsEnabled = preset.KeyUrl.Length > 0;
        KeyHintText.Text = preset.Id switch
        {
            "antigravity-cli" => "Uses the Google account already signed in to the Antigravity CLI. No API key is required.",
            "gemini" => "Create a Gemini API key in Google AI Studio and paste it here. API quotas and billing are " +
                        "separate from AI Pro and Antigravity. Available models depend on your API project tier.",
            _ when preset.KeyOptional => "No API key needed for a local endpoint.",
            _ when preset.KeyHint.Length > 0 => $"Looks like {preset.KeyHint} — {preset.KeyUrl}",
            _ => preset.KeyUrl
        };
        ModelHintText.Text = preset.Id switch
        {
            "antigravity-cli" => "Uses Gemini 3.8 Flash Medium. Jarvis stays pinned to this model with no model fallback.",
            "gemini" => "Choose a current chat model below. Gemini 4 Argon needs a paid API tier; Gemini 3.8 Flash " +
                        "is the default and has a free API tier. API access is separate from AI Pro.",
            _ => ""
        };
    }

    private void UpdateGeminiApiModels(string model)
    {
        var models = GeminiModelCatalog.ApiKeyModels
            .Select(id => new ModelChoice(id, GeminiApiModelLabel(id)))
            .ToList();
        GeminiApiModelCombo.ItemsSource = models;
        var chosen = models.FirstOrDefault(m => m.Id == model);
        if (chosen is not null) GeminiApiModelCombo.SelectedItem = chosen;
        else GeminiApiModelCombo.Text = model;
    }

    private static string GeminiApiModelLabel(string id) => id switch
    {
        "gemini-4-argon" => "Gemini 4 Argon — flagship reasoning",
        "gemini-3.8-flash" => "Gemini 3.8 Flash — default",
        "gemini-3.6-flash" => "Gemini 3.6 Flash",
        "gemini-3.5-flash-lite" => "Gemini 3.5 Flash-Lite — efficient",
        "gemini-3.1-pro-preview" => "Gemini 3.1 Pro — preview",
        "gemini-3.1-flash-lite" => "Gemini 3.1 Flash-Lite",
        "gemini-3-flash-preview" => "Gemini 3 Flash — preview",
        _ => id
    };

    /// <summary>
    /// A model choice takes effect at once and survives Cancel, without committing unrelated edits.
    /// </summary>
    private void KeepModelChoice(string model)
    {
        _settings.Model = model;
        _snapshot.Model = model;
        var committed = _snapshot.Clone();
        committed.Model = model;
        if (PresetCombo.SelectedItem is ProviderPreset { Id: "gemini" } preset)
        {
            // A Gemini model ID belongs to the Gemini API provider. Commit only the provider
            // selection with it, leaving unrelated unsaved settings and secrets untouched.
            committed.Preset = preset.Id;
            committed.Provider = preset.Kind;
            committed.BaseUrl = preset.BaseUrl;
            _settings.Preset = preset.Id;
            _settings.Provider = preset.Kind;
            _settings.BaseUrl = preset.BaseUrl;
            _snapshot.Preset = preset.Id;
            _snapshot.Provider = preset.Kind;
            _snapshot.BaseUrl = preset.BaseUrl;
        }
        try { SettingsStore.Save(committed); }
        catch (Exception ex) { Log.Warn("could not save the model choice", ex); }
        FooterStatus.Text = "Brain: " + model;
    }

    private void GeminiApiModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GeminiApiModelCombo.SelectedItem is not ModelChoice choice) return;
        _settings.Model = choice.Id;
        ModelBox.Text = choice.Id;
        KeepModelChoice(choice.Id);
    }

    /// <summary>Keeps only the well-spoken Windows voices and hides the flat or novelty ones.</summary>
    private static IReadOnlyList<string> GoodVoicesOnly(IReadOnlyList<string> installed)
    {
        var good = installed
            .Where(v => GoodVoices.Contains(NormaliseVoice(v), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return good.Count > 0 ? good : installed;
    }

    private static string NormaliseVoice(string name)
    {
        var text = name.Trim();
        if (text.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)) text = text["Microsoft ".Length..];
        if (text.EndsWith(" Desktop", StringComparison.OrdinalIgnoreCase)) text = text[..^" Desktop".Length];
        return text.Trim();
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not ProviderPreset preset || preset.KeyUrl.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(preset.KeyUrl) { UseShellExecute = true });
            FooterStatus.Text = "Browser opened — sign in and copy the key back here.";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "Could not open a browser: " + ex.Message;
        }
    }

    private bool ApplyToSettings()
    {
        _settings.UserName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Tony" : NameBox.Text.Trim();
        _settings.Honorific = string.IsNullOrWhiteSpace(HonorificBox.Text) ? "sir" : HonorificBox.Text.Trim();
        _settings.City = string.IsNullOrWhiteSpace(CityBox.Text) ? "London" : CityBox.Text.Trim();

        if (PresetCombo.SelectedItem is ProviderPreset preset)
        {
            _settings.Preset = preset.Id;
            _settings.Provider = preset.Kind;
        }
        _settings.BaseUrl = string.IsNullOrWhiteSpace(BaseUrlBox.Text) ? _settings.BaseUrl : BaseUrlBox.Text.Trim();
        if ((_settings.Provider is ProviderKind.OpenAiCompatible or ProviderKind.Anthropic) &&
            Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var modelEndpoint) &&
            modelEndpoint.Scheme == Uri.UriSchemeHttp && !AppSettings.IsLocalEndpoint(_settings.BaseUrl))
        {
            FooterStatus.Text = "Use HTTPS for a remote model endpoint so the API key and conversation are encrypted in transit.";
            return false;
        }
        var modelText = GeminiApiModelCombo.Visibility == Visibility.Visible
            ? (GeminiApiModelCombo.SelectedItem as ModelChoice)?.Id ?? (GeminiApiModelCombo.Text ?? "").Trim()
            : ModelBox.Text.Trim();
        _settings.Model = string.IsNullOrWhiteSpace(modelText) ? _settings.Model : modelText;
        if (KeyBox.Password.Length > 0) _settings.ApiKey = KeyBox.Password;

        if (!Number("Temperature", TempBox.Text, 0, 1.5, v => _settings.Temperature = v)) return false;
        if (!Number("Max tokens", TokensBox.Text, 128, 16000, v => _settings.MaxTokens = (int)Math.Round(v))) return false;

        _settings.Tts = TtsCombo.SelectedIndex == 1 ? TtsEngine.Azure : TtsEngine.Sapi;
        if (EdgeVoiceCombo.SelectedItem is string azureVoice) _settings.AzureVoice = azureVoice;
        _settings.AzureRegion = string.IsNullOrWhiteSpace(AzureRegionBox.Text) ? "uksouth" : AzureRegionBox.Text.Trim();
        if (AzureKeyBox.Password.Length > 0) _settings.AzureKey = AzureKeyBox.Password;
        _settings.SapiVoice = SapiVoiceCombo.SelectedItem is string sapi && sapi != "(system default)" ? sapi : "";
        if (!Number("Speech rate", RateBox.Text, 0.6, 1.6, v => _settings.SpeechRate = v)) return false;
        if (!Number("Volume", VolumeBox.Text, 0, 100, v => _settings.TtsVolume = (int)Math.Round(v))) return false;
        _settings.SpeakReplies = SpeakRepliesCheck.IsChecked == true;
        _settings.MicDevice = _micDevices.Selected(MicCombo);
        _settings.MicGain = Math.Round(Math.Clamp(MicGainSlider.Value, 0.5, 4.0), 2);
        _settings.SpeakerDevice = _speakerDevices.Selected(SpeakerCombo);

        _settings.WakeEnabled = WakeCheck.IsChecked == true;
        _settings.WakePhrase = string.IsNullOrWhiteSpace(WakePhraseBox.Text)
            ? "hey jarvis"
            : WakePhraseBox.Text.Trim().ToLowerInvariant();
        if (!Number("Wake confidence", WakeConfBox.Text, 0.01, 0.95, v => _settings.WakeConfidence = v)) return false;
        if (!Number("Follow-up seconds", FollowUpBox.Text, 0, 30, v => _settings.FollowUpSeconds = v)) return false;
        _settings.BargeIn = BargeInCheck.IsChecked == true;

        _settings.HudMonitor = HudMonitorCombo.SelectedItem as string ?? "primary";
        _settings.ShowHud = HudCheck.IsChecked == true;
        _settings.ShowOnAllMonitors = AllMonitorsCheck.IsChecked == true;
        _settings.GreetOnStart = GreetCheck.IsChecked == true;
        _settings.StartWithWindows = StartupCheck.IsChecked == true;

        // Hotkey text is checked by the caller so that Test buttons still work while a field is being edited.
        _hotkeyError = "";
        if (TryHotkey(HotkeyTalkBox.Text, "Talk", "Ctrl+Alt+J", out var talk)) _settings.HotkeyTalk = talk;
        if (TryHotkey(HotkeyStopBox.Text, "Stop", "Ctrl+Alt+Escape", out var stop)) _settings.HotkeyStop = stop;
        if (TryHotkey(HotkeyDeckBox.Text, "Show deck", "Ctrl+Alt+Space", out var deck)) _settings.HotkeyDeck = deck;

        _settings.RequireConfirmForRisky = ConfirmCheck.IsChecked == true;
        _settings.ConfirmByVoice = VoiceConfirmCheck.IsChecked == true;
        _settings.PoliteMode = PoliteCheck.IsChecked == true;
        _settings.AllowNetwork = NetworkCheck.IsChecked == true;
        _settings.WriteOkDirs = WriteDirsBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    private AppSettings? BuildCandidateSettings()
    {
        var original = _settings.Clone();
        if (!ApplyToSettings())
        {
            _settings.CopyFrom(original);
            return null;
        }
        var candidate = _settings.Clone();
        _settings.CopyFrom(original);
        return candidate;
    }

    private bool TryHotkey(string text, string label, string example, out string value)
    {
        value = text.Trim();
        if (HotkeyParser.TryParse(value, out _, out _)) return true;
        if (_hotkeyError.Length == 0)
            _hotkeyError = $"{label} hotkey '{value}' is not valid. Try {example}.";
        return false;
    }

    private bool Number(string label, string text, double min, double max, Action<double> apply)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            FooterStatus.Text = $"{label} must be a number between {min:0.##} and {max:0.##}.";
            return false;
        }
        apply(Math.Clamp(value, min, max));
        return true;
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceLists();
        StartMicMeter();
        FooterStatus.Text = $"{_micDevices.Count} microphone(s) and {_speakerDevices.Count} playback device(s) found.";
    }

    /// <summary>Auditions the picked microphone straight away, before the settings are saved.</summary>
    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StartMicMeter();
    }

    private void StartMicMeter()
    {
        var device = _micDevices.Selected(MicCombo);
        _meterSilentSince = DateTime.MinValue;
        _meterHeard = false;
        _meterHinted = false;
        try
        {
            _meter.Stop();
            _meter.Gain = Math.Clamp(MicGainSlider.Value, 0.5, 4.0);
            _meter.Start(device);
            _meterTimer.Start();
            MicStatus.Text = "Listening now - speak, and the bar above should move.";
        }
        catch (Exception ex)
        {
            _meterTimer.Stop();
            MicMeter.Value = 0;
            MicLevelText.Text = "not open";
            MicStatus.Text = "This microphone could not be opened: " + Trim(ex.Message, 140);
        }
    }

    private void MicMeter_Tick(object? sender, EventArgs e)
    {
        var level = _meter.Level;
        MicMeter.Value = Math.Min(1.0, level * 1.6);
        var heard = level > 0.008;
        MicLevelText.Text = heard ? "hearing you" : "silent";
        MicLevelText.Foreground = (System.Windows.Media.Brush)FindResource(heard ? "GreenBrush" : "TextMutedBrush");

        if (heard)
        {
            _meterHeard = true;
            _meterSilentSince = DateTime.MinValue;
            return;
        }
        if (_meterSilentSince == DateTime.MinValue)
        {
            _meterSilentSince = DateTime.UtcNow;
            return;
        }
        if (_meterHinted || _meterHeard) return;
        if (DateTime.UtcNow - _meterSilentSince < TimeSpan.FromSeconds(5)) return;
        _meterHinted = true;
        MicStatus.Text = "No sound is reaching this microphone. If it is a virtual device (SteelSeries Sonar, " +
                         "VoiceMeeter, a phone bridge), its app must be running with the microphone turned on.";
    }

    private void MicGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = sender is Slider slider ? slider.Value : e.NewValue;
        var gain = Math.Round(value, 2);
        if (MicGainText is not null)
            MicGainText.Text = gain.ToString("0.0", CultureInfo.InvariantCulture) + "x";
        _meter.Gain = gain;
        if (_loading) return;
        _settings.MicGain = gain;
    }

    private async void TestMic_Click(object sender, RoutedEventArgs e)
    {
        var device = _micDevices.Selected(MicCombo);
        TestMicButton.IsEnabled = false;
        MicStatus.Text = "Listening for four seconds — say anything.";
        try
        {
            var peak = await AudioDevices.MeasureInputAsync(device, TimeSpan.FromSeconds(4), CancellationToken.None);
            MicStatus.Text = peak switch
            {
                < 0.02 => "I heard nothing at all. Check the device, the Windows input level, or a hardware mute switch.",
                < 0.12 => $"I heard only a faint signal ({peak * 100:0}%). It should work, but speak up or raise the gain.",
                _ => $"I can hear you clearly ({peak * 100:0}%). This microphone is a good choice."
            };
        }
        catch (Exception ex)
        {
            MicStatus.Text = "The microphone could not be opened: " + Trim(ex.Message, 140);
        }
        finally
        {
            TestMicButton.IsEnabled = true;
        }
    }

    private async void TestBrain_Click(object sender, RoutedEventArgs e)
    {
        var candidate = BuildCandidateSettings();
        if (candidate is null) return;
        BrainStatus.Text = "Testing…";
        var model = ModelFactory.Create(candidate);
        if (model is null)
        {
            BrainStatus.Text = "No model configured. Add a key, or choose a local preset and start its server.";
            return;
        }
        using var modelLifetime = model as IDisposable;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var reply = await model.StreamAsync(new ModelRequest(
                "Reply with exactly the single word: online",
                new[] { ChatMessage.User("ping") }, new ToolRegistry(), 0, 12), null, cts.Token);
            BrainStatus.Text = $"Connected to {model.DisplayName}. It said: \"{Trim(reply.Text, 70)}\"";
        }
        catch (Exception ex)
        {
            BrainStatus.Text = "Failed: " + Trim(ex.Message, 180);
        }

    }

    private void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyToSettings()) return;
        if (_settings.Tts == TtsEngine.Azure && string.IsNullOrWhiteSpace(_settings.AzureKey))
        {
            VoiceStatus.Text = "Azure needs a key. Add one, or choose the Windows voice.";
            return;
        }
        if (Application.Current is not App app) return;
        // Speak through the newly chosen device right now, not after a restart.
        app.Jarvis.Tts.ReloadOutput();
        app.Jarvis.Tts.Enabled = true;
        app.Jarvis.Tts.Say("Good evening. All systems are operating within normal parameters.");
        var device = _speakerDevices.Selected(SpeakerCombo);
        VoiceStatus.Text = device.Length == 0
            ? "Playing on the Windows default device — save to keep these settings."
            : $"Playing on {device} — save to keep these settings.";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        try
        {
            if (!ApplyToSettings()) return;
            if (_hotkeyError.Length > 0)
            {
                FooterStatus.Text = _hotkeyError;
                return;
            }

            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _settings.CopyFrom(_snapshot);
            Log.Warn("could not save settings", ex);
            FooterStatus.Text = "Could not save settings: " + ex.Message;
            return;
        }

        // From here on, closing the window must keep what was just saved, even if a hotkey still
        // needs fixing. The baseline moves with it, so later edits are still cancellable.
        _snapshot = _settings.Clone();
        if (Application.Current is App app)
        {
            app.ApplySettings();
            var hotkeyError = app.Hotkeys?.LastError;
            if (!string.IsNullOrWhiteSpace(hotkeyError))
            {
                FooterStatus.Text = "Saved, but " + hotkeyError + " Choose another combination.";
                return;
            }
        }

        FooterStatus.Text = "Saved.";
        Close();
    }

    private void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return;
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            SaveSettings();
        }
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    /// <summary>Turns a key press into a combination the parser understands, in the usual Ctrl+Alt+X order.</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = HotkeyKeyName(key);
        if (name is null) return;

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

        // With no modifier, only capture function keys: Backspace, arrows, digits and letters must
        // stay usable for editing or typing a combination by hand.
        var functionKey = name.Length > 1 && name[0] == 'f' && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24;
        if (parts.Count == 0 && !functionKey) return;

        parts.Add(Pretty(name));
        var combo = string.Join("+", parts);
        box.Text = combo;
        box.CaretIndex = combo.Length;
        e.Handled = true;
    }

    private static string Pretty(string name) =>
        name.Length == 1 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..];

    private static string? HotkeyKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "f" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Escape => "escape",
            Key.Space => "space",
            Key.Tab => "tab",
            Key.Enter => "enter",
            Key.Back => "backspace",
            Key.Delete => "delete",
            Key.Insert => "insert",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Up => "up",
            Key.Down => "down",
            Key.Left => "left",
            Key.Right => "right",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.OemQuestion => "slash",
            Key.OemMinus => "minus",
            Key.OemPlus => "equal",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "quote",
            Key.OemOpenBrackets => "leftbracket",
            Key.OemCloseBrackets => "rightbracket",
            Key.OemPipe => "backslash",
            Key.OemTilde => "grave",
            Key.Apps => "apps",
            Key.PrintScreen => "printscreen",
            Key.Pause => "pause",
            Key.CapsLock => "capslock",
            Key.NumLock => "numlock",
            Key.Scroll => "scrolllock",
            Key.MediaPlayPause => "playpause",
            Key.MediaNextTrack => "next",
            Key.MediaPreviousTrack => "previous",
            Key.VolumeMute => "mute",
            Key.VolumeUp => "volumeup",
            Key.VolumeDown => "volumedown",
            _ => null
        };
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Paths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) { FooterStatus.Text = ex.Message; }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Paths.Ensure();
            Process.Start(new ProcessStartInfo(Paths.LogsDir) { UseShellExecute = true });
        }
        catch (Exception ex) { FooterStatus.Text = ex.Message; }
    }

    private void ResetSetup_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetupComplete = false;
        _snapshot.SetupComplete = false;
        SettingsStore.Save(_settings);
        FooterStatus.Text = "Setup will run again next launch.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private static string Trim(string text, int max)
    {
        var clean = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    /// <summary>Maps a combo row to the device name stored in settings; the first row is Windows' default.</summary>
    private sealed class DeviceList
    {
        private readonly List<(string Value, string Label)> _items = new();

        public int Count => Math.Max(0, _items.Count - 1);

        public void Fill(ComboBox box, IReadOnlyList<(string Value, string Label)> devices, string current, string defaultLabel)
        {
            _items.Clear();
            _items.Add((AudioDevices.SystemDefault, defaultLabel));
            foreach (var device in devices) _items.Add((device.Value, device.Label));
            if (!string.IsNullOrWhiteSpace(current) &&
                !_items.Any(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase)))
                _items.Add((current, current + "   (not connected)"));

            box.ItemsSource = _items.Select(i => i.Label).ToList();
            var index = _items.FindIndex(i => string.Equals(i.Value, current, StringComparison.OrdinalIgnoreCase));
            box.SelectedIndex = index >= 0 ? index : 0;
        }

        public string Selected(ComboBox box) =>
            box.SelectedIndex >= 0 && box.SelectedIndex < _items.Count ? _items[box.SelectedIndex].Value : "";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
