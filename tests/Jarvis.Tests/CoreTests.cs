using System.Text.Json;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Tests;

public class PolicyTests
{
    private static readonly AppSettings Settings = new();
    private static readonly PolicyEngine Policy = new(Settings);

    private static ToolDefinition Tool(string name, bool readOnly = false) => new()
    {
        Name = name,
        Description = "test",
        ParametersJson = "{}",
        ReadOnly = readOnly,
        Handler = (_, _) => Task.FromResult(ToolResult.Done("ok"))
    };

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("Get-ChildItem C:\\Users")]
    [InlineData("Get-Process | Sort-Object CPU -Descending")]
    [InlineData("echo hello")]
    [InlineData("git status")]
    public void Shell_commands_require_approval_even_when_the_risk_scan_misses(string command)
    {
        var decision = Policy.Evaluate(Tool("run_powershell"), Args($$"""{"command":{{JsonSerializer.Serialize(command)}}}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Theory]
    [InlineData("Remove-Item -Recurse .\\build")]
    [InlineData("rm -rf ./dist")]
    [InlineData("Stop-Process -Name chrome")]
    [InlineData("git push origin main")]
    [InlineData("Invoke-WebRequest -Method Post -Uri https://example.com -Body hi")]
    [InlineData("Set-ItemProperty HKLM:\\Software\\X -Name Y -Value Z")]
    [InlineData("shutdown /s /t 0")]
    public void Risky_shell_asks_first(string command)
    {
        var decision = Policy.Evaluate(Tool("run_powershell"), Args($$"""{"command":{{JsonSerializer.Serialize(command)}}}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Theory]
    [InlineData("format c:")]
    [InlineData("diskpart")]
    [InlineData("vssadmin delete shadows /all")]
    public void Catastrophic_shell_is_refused(string command)
    {
        var decision = Policy.Evaluate(Tool("run_powershell"), Args($$"""{"command":{{JsonSerializer.Serialize(command)}}}"""));
        Assert.Equal(RiskLevel.Deny, decision.Level);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void A_filename_that_contains_a_command_word_is_not_risky()
    {
        Assert.False(RiskScan.IsRisky("Get-ChildItem -Filter rm-old-notes.txt"));
        Assert.True(RiskScan.IsRisky("rm -rf old-notes"));
        Assert.True(RiskScan.IsRisky("Get-ChildItem | Remove-Item"));
    }

    [Fact]
    public void Pressing_enter_always_asks()
    {
        var decision = Policy.Evaluate(Tool("press_keys"), Args("""{"keys":"enter"}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Fact]
    public void Ordinary_keypresses_are_allowed()
    {
        Assert.Equal(RiskLevel.Allow, Policy.Evaluate(Tool("press_keys"), Args("""{"keys":"ctrl+l"}""")).Level);
    }

    [Fact]
    public void Writing_inside_the_profile_is_allowed()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "notes.txt");
        var decision = Policy.Evaluate(Tool("write_file"),
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}},"content":"hi"}"""));
        Assert.Equal(RiskLevel.Allow, decision.Level);
    }

    [Fact]
    public void Writing_outside_the_profile_asks()
    {
        var decision = Policy.Evaluate(Tool("write_file"), Args("""{"path":"C:\\Windows\\System32\\drivers\\etc\\hosts","content":"x"}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
        Assert.NotNull(decision.TargetPath);
        Assert.StartsWith("file.outside.", decision.RuleKey);
    }

    [Fact]
    public void Writing_to_ssh_asks_even_inside_the_profile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        var decision = Policy.Evaluate(Tool("write_file"), Args($$"""{"path":{{JsonSerializer.Serialize(path)}},"content":"x"}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Fact]
    public void Reading_credentials_requires_approval()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        var decision = Policy.Evaluate(Tool("read_file", readOnly: true),
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Fact]
    public void Sensitive_path_matching_respects_directory_boundaries()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh-notes.txt");
        var decision = Policy.Evaluate(Tool("read_file", readOnly: true),
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""));
        Assert.Equal(RiskLevel.Allow, decision.Level);
    }

    [Fact]
    public void Reading_dotenv_credentials_requires_approval_anywhere()
    {
        var path = Path.Combine(Path.GetTempPath(), "jarvis-project", ".env.production");
        var decision = Policy.Evaluate(Tool("read_file", readOnly: true),
            Args($$"""{"path":{{JsonSerializer.Serialize(path)}}}"""));
        Assert.Equal(RiskLevel.Confirm, decision.Level);
    }

    [Fact]
    public void Shell_always_allow_is_scoped_to_the_exact_command()
    {
        var first = Policy.Evaluate(Tool("run_powershell"), Args("""{"command":"echo one"}"""));
        var second = Policy.Evaluate(Tool("run_powershell"), Args("""{"command":"echo two"}"""));
        Assert.NotEqual(first.RuleKey, second.RuleKey);
    }

    [Fact]
    public void Opening_an_executable_asks()
    {
        Assert.Equal(RiskLevel.Confirm, Policy.Evaluate(Tool("open_path"), Args("""{"target":"C:\\Tools\\setup.exe"}""")).Level);
        Assert.Equal(RiskLevel.Allow, Policy.Evaluate(Tool("open_path"), Args("""{"target":"C:\\notes.txt"}""")).Level);
    }

    [Fact]
    public void Power_actions_ask()
    {
        Assert.Equal(RiskLevel.Confirm, Policy.Evaluate(Tool("system"), Args("""{"action":"shutdown"}""")).Level);
        Assert.Equal(RiskLevel.Confirm, Policy.Evaluate(Tool("system"), Args("""{"action":"lock"}""")).Level);
    }

    [Fact]
    public void Read_only_tools_never_ask()
    {
        var decision = Policy.Evaluate(Tool("list_windows", readOnly: true), Args("{}"));
        Assert.Equal(RiskLevel.Allow, decision.Level);
    }

    [Fact]
    public void Unknown_tools_ask()
    {
        Assert.Equal(RiskLevel.Confirm, Policy.Evaluate(Tool("something_new"), Args("{}")).Level);
    }
}

public class AnswerParserTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, please.")]
    [InlineData("yeah go ahead")]
    [InlineData("sure")]
    [InlineData("okay")]
    [InlineData("um, yes")]
    [InlineData("affirmative")]
    [InlineData("please do")]
    public void Yes_is_understood(string answer) => Assert.True(AnswerParser.IsYes(answer));

    [Theory]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("don't")]
    [InlineData("cancel that")]
    [InlineData("no thanks")]
    public void No_is_understood(string answer) => Assert.True(AnswerParser.IsNo(answer));

    [Theory]
    [InlineData("")]
    [InlineData("what time is it")]
    [InlineData("but yes, that is right")]
    public void A_yes_in_the_middle_does_not_count(string answer) => Assert.False(AnswerParser.IsYes(answer));

    [Fact]
    public void No_wins_over_yes_in_the_same_sentence()
    {
        Assert.False(AnswerParser.IsYes("yes, but actually no"));
    }
}

public class TextTests
{
    [Fact]
    public void Sentence_splitter_emits_whole_sentences()
    {
        var splitter = new SentenceSplitter();
        var first = splitter.Feed("Good evening. All systems");
        Assert.Single(first);
        Assert.Equal("Good evening", first[0]);

        var rest = splitter.Feed(" are nominal.");
        Assert.Contains("All systems are nominal", rest[0]);
    }

    [Fact]
    public void Sentence_splitter_does_not_read_code()
    {
        var splitter = new SentenceSplitter();
        var output = new List<string>();
        output.AddRange(splitter.Feed("Before. ```Console.WriteLine(\"hi.\")``` After this."));
        output.AddRange(splitter.Flush());
        Assert.Contains(output, s => s.Contains("Before"));
        Assert.Contains(output, s => s.Contains("After this"));
        Assert.DoesNotContain(output, s => s.Contains("Console"));
    }

    [Fact]
    public void Abbreviations_do_not_split()
    {
        var splitter = new SentenceSplitter();
        var output = splitter.Feed("Mr. Smith is here. He is waiting.");
        output.AddRange(splitter.Flush());
        Assert.Contains(output, s => s.Contains("Mr. Smith is here"));
    }

    [Fact]
    public void Speech_text_strips_markdown_and_links()
    {
        var spoken = SpeechText.Speakable("**Done**, sir. See https://example.com/x for `details`.");
        Assert.DoesNotContain("**", spoken);
        Assert.DoesNotContain("https://", spoken);
        Assert.Contains("a link", spoken);
        Assert.Contains("details", spoken);
    }
}

public class SchemaAndToolTests
{
    [Fact]
    public void Every_tool_has_a_valid_object_schema_and_unique_name()
    {
        var tools = AppTools.Create()
            .Concat(DesktopTools.Create())
            .Concat(SystemTools.Create())
            .Concat(FileShellTools.Create())
            .Concat(MemoryTools.Create())
            .Concat(WorkerTools.Create())
            .ToList();

        Assert.True(tools.Count >= 30, $"expected a full toolbox, found {tools.Count}");
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            using var doc = JsonDocument.Parse(tool.ParametersJson);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.True(doc.RootElement.TryGetProperty("properties", out _));
        }
    }

    [Fact]
    public void Registry_rejects_duplicate_tools()
    {
        var registry = new ToolRegistry();
        var tool = new ToolDefinition
        {
            Name = "x", Description = "d", ParametersJson = "{\"type\":\"object\",\"properties\":{}}",
            Handler = (_, _) => Task.FromResult(ToolResult.Done(""))
        };
        registry.Add(tool);
        Assert.Throws<InvalidOperationException>(() => registry.Add(tool));
        Assert.Single(registry.All);
    }

    [Fact]
    public void Model_factory_returns_null_without_a_provider()
    {
        var settings = new AppSettings { Provider = ProviderKind.None };
        Assert.Null(ModelFactory.Create(settings));
    }
}

public class SettingsTests
{
    [Fact]
    public void Api_key_round_trips_through_dpapi_and_never_appears_in_json()
    {
        var settings = new AppSettings();
        settings.ApiKey = "sk-super-secret-value";
        Assert.Equal("sk-super-secret-value", settings.ApiKey);
        Assert.StartsWith("dpapi:", settings.ApiKeyEncrypted);

        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("sk-super-secret-value", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("sk-super-secret-value", restored.ApiKey);
    }

    [Fact]
    public void Blank_and_legacy_keys_are_handled()
    {
        Assert.Equal("", Dpapi.Unprotect(""));
        Assert.Equal("plain", Dpapi.Unprotect("plain"));
        Assert.Equal("", Dpapi.Protect(""));
    }

    [Fact]
    public void Local_endpoints_need_no_key()
    {
        Assert.True(AppSettings.IsLocalEndpoint("http://localhost:11434/v1"));
        Assert.True(AppSettings.IsLocalEndpoint("http://127.0.0.1:1234/v1"));
        Assert.False(AppSettings.IsLocalEndpoint("https://api.deepseek.com"));
        Assert.False(AppSettings.IsLocalEndpoint("https://example.com/path/localhost"));
        Assert.False(AppSettings.IsLocalEndpoint("not-a-url"));
    }

    [Fact]
    public void Model_factory_rejects_invalid_endpoints_even_when_a_key_is_present()
    {
        var settings = new AppSettings { Provider = ProviderKind.OpenAiCompatible, BaseUrl = "not-a-url" };
        settings.ApiKey = "test-key";
        Assert.Null(ModelFactory.Create(settings));
    }

    [Fact]
    public void Presets_are_consistent()
    {
        foreach (var preset in Presets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Label));
            Assert.False(string.IsNullOrWhiteSpace(preset.BaseUrl));
            Assert.False(string.IsNullOrWhiteSpace(preset.Model));
            Assert.NotNull(Presets.Find(preset.Id));
        }
    }

    [Fact]
    public void Gemini_preset_points_at_the_openai_compatible_endpoint()
    {
        var gemini = Presets.All.Single(p => p.Id == "gemini");
        Assert.Equal(ProviderKind.OpenAiCompatible, gemini.Kind);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", gemini.BaseUrl);
        Assert.False(string.IsNullOrWhiteSpace(gemini.Model));
        Assert.False(string.IsNullOrWhiteSpace(gemini.KeyUrl));
    }

    [Fact]
    public void Consumer_Google_sign_in_presets_are_not_offered()
    {
        Assert.DoesNotContain(Presets.All, p => p.Id is "googleaccount" or "antigravity");
    }

    [Fact]
    public void Legacy_google_sign_in_credentials_are_dropped_during_migration()
    {
        const string legacyJson = """
            {
              "Provider": "GoogleAccount",
              "Preset": "googleaccount",
              "Model": "gemini-3.8-flash",
              "GoogleClientId": "123456-abc.apps.googleusercontent.com",
              "GoogleClientSecretEncrypted": "legacy-client-secret",
              "GoogleRefreshTokenEncrypted": "legacy-refresh-token",
              "GoogleEmail": "someone@gmail.com"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.True(GeminiModelCatalog.MigrateLegacySelection(settings));
        Assert.Equal(ProviderKind.AntigravityCli, settings.Provider);
        Assert.Equal("antigravity-cli", settings.Preset);

        var migratedJson = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("legacy-client-secret", migratedJson);
        Assert.DoesNotContain("legacy-refresh-token", migratedJson);
        Assert.DoesNotContain("GoogleClientId", migratedJson);
        Assert.DoesNotContain("GoogleEmail", migratedJson);
    }

    [Fact]
    public void A_legacy_google_account_provider_is_disabled_until_migrated()
    {
        var settings = new AppSettings { Provider = ProviderKind.GoogleAccount, Preset = "googleaccount" };
        Assert.Null(ModelFactory.Create(settings));
        Assert.True(GeminiModelCatalog.MigrateLegacySelection(settings));
        Assert.Equal(ProviderKind.AntigravityCli, settings.Provider);
        Assert.Equal("antigravity-cli", settings.Preset);
        Assert.Equal("gemini-3.8-flash-medium", settings.Model);
    }

    [Fact]
    public void Legacy_consumer_sign_in_is_migrated_to_the_Antigravity_CLI_route()
    {
        var settings = new AppSettings
        {
            Provider = ProviderKind.Antigravity,
            Preset = "antigravity",
            Model = "gemini-3.8-flash-medium"
        };
        Assert.True(GeminiModelCatalog.MigrateLegacySelection(settings));
        Assert.Equal(ProviderKind.AntigravityCli, settings.Provider);
        Assert.Equal("antigravity-cli", settings.Preset);
        Assert.Equal("agy://local", settings.BaseUrl);
        Assert.Equal("gemini-3.8-flash-medium", settings.Model);
        Assert.NotNull(ModelFactory.Create(settings));
    }

    [Fact]
    public void Gemini_API_picker_contains_current_text_chat_models_only_and_no_Gemini_2()
    {
        Assert.Contains("gemini-4-argon", GeminiModelCatalog.ApiKeyModels);
        Assert.Contains("gemini-3.8-flash", GeminiModelCatalog.ApiKeyModels);
        Assert.All(GeminiModelCatalog.ApiKeyModels, model =>
            Assert.False(GeminiModelCatalog.IsGemini2Family(model)));
    }

    [Fact]
    public void A_tool_call_keeps_the_signature_the_model_gave_it()
    {
        var call = new ToolCall("id1", "get_weather", "{\"location\":\"Paris\"}", "sig123");
        Assert.Equal("sig123", call.ThoughtSignature);
        // The old three-part shape still works.
        var plain = new ToolCall("id2", "get_weather", "{}");
        Assert.Equal("", plain.ThoughtSignature);
    }

    [Fact]
    public void Device_choices_default_to_windows_and_survive_json()
    {
        var settings = new AppSettings();
        Assert.Equal("", settings.MicDevice);
        Assert.Equal("", settings.SpeakerDevice);

        settings.MicDevice = "Studio Microphone (USB)";
        settings.SpeakerDevice = "Speakers (Realtek)";
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(settings.MicDevice, restored.MicDevice);
        Assert.Equal(settings.SpeakerDevice, restored.SpeakerDevice);
    }

    [Fact]
    public void Unknown_audio_devices_fall_back_to_the_windows_default()
    {
        // A stale id or name must never stop voice input: it falls back instead, and never throws.
        var defaultInput = AudioDevices.FindInput(null);
        try { defaultInput?.Dispose(); } catch { }
        var stale = AudioDevices.FindInput("No such microphone " + Guid.NewGuid().ToString("n"));
        try { stale?.Dispose(); } catch { }

        Assert.Equal(-1, AudioDevices.ResolveOutput("No such speaker " + Guid.NewGuid().ToString("n")));
    }

    [Fact]
    public void Microphones_are_listed_by_windows_own_names()
    {
        var mics = AudioDevices.Inputs();
        Assert.NotEmpty(mics);
        foreach (var mic in mics)
        {
            // Endpoint ids look like {0.0.1.00000000}.{guid}; they are what settings stores.
            Assert.False(string.IsNullOrWhiteSpace(mic.Id));
            Assert.StartsWith("{", mic.Id);
            Assert.False(string.IsNullOrWhiteSpace(mic.Name));
            // The old legacy-mixer junk must never reach the picker again.
            Assert.DoesNotContain("No such", mic.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_microphone_is_found_by_id_and_by_name()
    {
        var first = AudioDevices.Inputs().FirstOrDefault();
        if (first is null) return; // a machine with no microphone has nothing to resolve

        using var byId = AudioDevices.FindInput(first.Id);
        Assert.NotNull(byId);
        Assert.Equal(first.Id, byId!.ID);

        using var byName = AudioDevices.FindInput(first.Name);
        Assert.NotNull(byName);
        Assert.Equal(first.Name, byName!.FriendlyName);
    }

    [Fact]
    public void A_quiet_microphone_can_be_lifted()
    {
        Assert.Equal(1.0, new AppSettings().MicGain);
        var settings = new AppSettings { MicGain = 2.5 };
        var copy = settings.Clone();
        Assert.Equal(2.5, copy.MicGain);
    }

    [Fact]
    public void CopyFrom_undoes_an_abandoned_settings_edit()
    {
        var settings = new AppSettings
        {
            UserName = "Tony",
            MicDevice = "Desk microphone",
            SpeakerDevice = "Studio monitors"
        };
        settings.WriteOkDirs.Add("C:\\Projects");
        var snapshot = settings.Clone();

        settings.UserName = "Someone else";
        settings.MicDevice = "Laptop microphone";
        settings.SpeakerDevice = "";
        settings.Tts = TtsEngine.Azure;
        settings.WakeEnabled = false;
        settings.WriteOkDirs.Add("D:\\Temp");

        settings.CopyFrom(snapshot);

        Assert.Equal("Tony", settings.UserName);
        Assert.Equal("Desk microphone", settings.MicDevice);
        Assert.Equal("Studio monitors", settings.SpeakerDevice);
        Assert.Equal(TtsEngine.Sapi, settings.Tts);
        Assert.True(settings.WakeEnabled);
        Assert.Equal(new[] { "C:\\Projects" }, settings.WriteOkDirs);

        // The restored list must be a copy, not an alias of the snapshot.
        settings.WriteOkDirs.Add("E:\\Other");
        Assert.Single(snapshot.WriteOkDirs);
    }
}

public class MemoryAndPresenceTests
{
    [Fact]
    public void Presence_marks_the_first_message_of_the_day()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var presence = new PresenceTracker(dir);
            Assert.Contains("first today", presence.TimeTag());
            Assert.DoesNotContain("first today", presence.TimeTag());
            Assert.Contains("[typed]", presence.TimeTag(typed: true));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Facts_are_remembered_searched_and_forgotten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var memory = new MemoryStore(dir, "Tony");
            memory.Remember("Prefers tea over coffee");
            memory.Remember("Works with .NET on Windows");
            Assert.Single(memory.Search("tea"));
            Assert.Equal(2, memory.Facts.Count);
            Assert.True(memory.Forget("tea"));
            Assert.Empty(memory.Search("tea"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Notes_appear_in_the_prompt_context()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var memory = new MemoryStore(dir, "Tony");
            memory.SeedNotesIfMissing();
            memory.AppendRecent("Asked for the weather");
            var context = memory.ContextBlock();
            Assert.Contains("Asked for the weather", context);
            Assert.Contains("Notes left by earlier sessions", context);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ScreenshotMappingTests
{
    [Fact]
    public void Image_coordinates_map_back_to_physical_desktop_pixels()
    {
        using var bitmap = new System.Drawing.Bitmap(960, 540);
        var image = new CapturedImage(bitmap, 1920, 0, 2.0, "second display", IntPtr.Zero);
        Assert.Equal((1920, 0), image.ToDesktop(0, 0));
        Assert.Equal((2220, 220), image.ToDesktop(150, 110));
    }

    [Fact]
    public void Screenshot_state_is_stale_until_updated()
    {
        var state = new ScreenshotState();
        Assert.False(state.HasShot);
        using var bitmap = new System.Drawing.Bitmap(10, 10);
        state.Update(new CapturedImage(bitmap, 0, 0, 1.0, "primary", IntPtr.Zero));
        Assert.True(state.HasShot);
        Assert.Equal((5, 7), state.ToDesktop(5, 7));
    }
}

public class OfflineBrainTests
{
    private static (IntentEngine Intent, JarvisServices Services, string Dir) Build()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var settings = new AppSettings { UserName = "Tony", Honorific = "sir" };
        var events = new EventBus(dir);
        var memory = new MemoryStore(dir, settings.UserName);
        var services = new JarvisServices
        {
            Settings = settings,
            Events = events,
            Memory = memory,
            Screenshot = new ScreenshotState(),
            Approvals = new ApprovalService(),
            Timers = new TimerService(),
            Policy = new PolicyEngine(settings),
            Tools = new ToolRegistry(),
            DataDirectory = dir
        };
        return (new IntentEngine(services), services, dir);
    }

    [Fact]
    public async Task Time_and_greetings_work_without_a_model()
    {
        var (intent, _, dir) = Build();
        try
        {
            var time = await intent.TryHandleAsync("what time is it", CancellationToken.None);
            Assert.Contains("It is", time);

            var hello = await intent.TryHandleAsync("hello", CancellationToken.None);
            Assert.Contains("At your service", hello);

            Assert.Null(await intent.TryHandleAsync("compose a symphony in the style of Brahms", CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Memory_intents_round_trip()
    {
        var (intent, services, dir) = Build();
        try
        {
            await intent.TryHandleAsync("remember that I take my coffee black", CancellationToken.None);
            Assert.Contains(services.Memory.Facts, f => f.Text.Contains("coffee black"));

            var recalled = await intent.TryHandleAsync("what do you remember about coffee", CancellationToken.None);
            Assert.Contains("coffee black", recalled);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task System_report_is_available_offline()
    {
        var (intent, _, dir) = Build();
        try
        {
            var report = await intent.TryHandleAsync("system status", CancellationToken.None);
            Assert.Contains("CPU", report);
            Assert.Contains("memory", report);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class UtteranceRecorderTests
{
    private const int FrameSamples = 1_280;

    [Fact]
    public async Task Quiet_speech_is_detected_and_returns_audio_for_transcription()
    {
        var recorder = new UtteranceRecorder(endSilenceSeconds: 0.25, maxUtteranceSeconds: 10);
        var capture = recorder.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        recorder.Push(MakeFrame(0.011f), FrameSamples * 2);
        recorder.Push(MakeFrame(0.011f), FrameSamples * 2);
        for (var i = 0; i < 4; i++) recorder.Push(new byte[FrameSamples * 2], FrameSamples * 2);

        var samples = await capture.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(samples);
        Assert.True(samples.Length >= FrameSamples * 2);
        Assert.Contains(samples, sample => Math.Abs(sample) > 0.005f);
    }

    private static byte[] MakeFrame(float peak)
    {
        var bytes = new byte[FrameSamples * 2];
        for (var i = 0; i < FrameSamples; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 220 * i / 16_000) * peak * short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xff);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        return bytes;
    }
}
