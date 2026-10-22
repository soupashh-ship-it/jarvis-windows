using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Core;

public enum ProviderKind { None, OpenAiCompatible, Anthropic, GoogleAccount, Antigravity, AntigravityCli }
public enum TtsEngine { Sapi, Azure }

/// <summary>Everything the user can change. Plain JSON, no secrets in the clear.</summary>
public sealed class AppSettings
{
    // Identity
    public string UserName { get; set; } = "Tony";
    public string Honorific { get; set; } = "sir";
    public string City { get; set; } = "London";

    // Voice input
    public bool WakeEnabled { get; set; } = true;
    public string WakePhrase { get; set; } = "hey jarvis";
    public double WakeConfidence { get; set; } = 0.01;
    public double EndSilenceSeconds { get; set; } = 1.35;
    public double WaitForSpeechSeconds { get; set; } = 6.0;
    public double MaxUtteranceSeconds { get; set; } = 45;
    public double ConfirmTimeoutSeconds { get; set; } = 14;
    public bool BargeIn { get; set; } = false;
    public double FollowUpSeconds { get; set; } = 4.0;

    // Voice output
    public bool SpeakReplies { get; set; } = true;
    public TtsEngine Tts { get; set; } = TtsEngine.Sapi;
    public string AzureVoice { get; set; } = "en-GB-RyanNeural";
    public string AzureRegion { get; set; } = "uksouth";
    public string AzureKeyEncrypted { get; set; } = "";
    public double SpeechRate { get; set; } = 1.0;
    public double SpeechPitch { get; set; } = 1.0;
    public string SapiVoice { get; set; } = "";
    public int TtsVolume { get; set; } = 100;

    // Devices. Empty means "whatever Windows is using"; names survive renumbering, indices do not.
    public string MicDevice { get; set; } = "";
    public string SpeakerDevice { get; set; } = "";

    /// <summary>Lifts a quiet microphone before the recogniser hears it. 1.0 leaves it untouched.</summary>
    public double MicGain { get; set; } = 1.0;

    [JsonIgnore] public string AzureKey
    {
        get => Dpapi.Unprotect(AzureKeyEncrypted);
        set => AzureKeyEncrypted = Dpapi.Protect(value ?? "");
    }

    [JsonIgnore] public bool HasNeuralVoice => Tts == TtsEngine.Azure && !string.IsNullOrWhiteSpace(AzureKey);

    // Brain
    public ProviderKind Provider { get; set; } = ProviderKind.None;
    public string Preset { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-chat";
    public string ApiKeyEncrypted { get; set; } = "";
    public double Temperature { get; set; } = 0.65;
    public int MaxTokens { get; set; } = 1600;
    public int MaxToolSteps { get; set; } = 24;
    public int SessionIdleResetMinutes { get; set; } = 30;
    public bool AllowNetwork { get; set; } = true;

    // Behaviour
    public bool PoliteMode { get; set; } = true;
    public bool AnnounceToolSteps { get; set; } = true;
    public bool GreetOnStart { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool AutoUpdateNotes { get; set; } = true;

    // Interface
    public string HotkeyTalk { get; set; } = "Ctrl+Alt+J";
    public string HotkeyStop { get; set; } = "Ctrl+Alt+Escape";
    public string HotkeyDeck { get; set; } = "Ctrl+Alt+Space";
    public bool ShowHud { get; set; } = true;
    public string HudMonitor { get; set; } = "primary";
    public double HudLineWidth { get; set; } = 240;
    public double HudBottomMargin { get; set; } = 74;
    public bool ShowOnAllMonitors { get; set; } = false;

    // Safety
    public bool RequireConfirmForRisky { get; set; } = true;
    public bool ConfirmByVoice { get; set; } = true;
    public List<string> WriteOkDirs { get; set; } = new();
    public List<string> TrustedCommands { get; set; } = new();

    // Setup
    public bool SetupComplete { get; set; } = false;
    public string LastRunVersion { get; set; } = "";

    [JsonIgnore] public string ApiKey
    {
        get => Dpapi.Unprotect(ApiKeyEncrypted);
        set => ApiKeyEncrypted = Dpapi.Protect(value ?? "");
    }

    [JsonIgnore] public bool HasModel => Provider switch
    {
        ProviderKind.None => false,
        ProviderKind.Antigravity or ProviderKind.GoogleAccount => false,
        ProviderKind.AntigravityCli => true,
        _ => !string.IsNullOrWhiteSpace(ApiKey) || IsLocalEndpoint(BaseUrl)
    };

    public static bool IsLocalEndpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>
    /// Copies every plain setting from another instance, in place. The orchestrator and the windows
    /// all hold this one object, so Settings uses this to undo an edit the user cancelled or
    /// abandoned rather than swap the reference out from under them.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        var source = other.Clone();
        foreach (var property in typeof(AppSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            try { property.SetValue(this, property.GetValue(source)); } catch { }
        }
    }
}

public sealed record ProviderPreset(
    string Id, string Label, ProviderKind Kind, string BaseUrl, string Model,
    string KeyHint, string KeyUrl, bool KeyOptional = false)
{
    public override string ToString() => Label;
}

public static class Presets
{
    public static readonly IReadOnlyList<ProviderPreset> All = new[]
    {
        new ProviderPreset("antigravity-cli", "Google Antigravity (Google login)", ProviderKind.AntigravityCli,
            "agy://local", "gemini-3.8-flash-medium", "Google login", "https://antigravity.google/docs/cli/headless", KeyOptional: true),
        new ProviderPreset("deepseek", "DeepSeek", ProviderKind.OpenAiCompatible,
            "https://api.deepseek.com", "deepseek-chat",
            "sk-…", "https://platform.deepseek.com/api_keys"),
        new ProviderPreset("openai", "OpenAI", ProviderKind.OpenAiCompatible,
            "https://api.openai.com/v1", "gpt-4o-mini",
            "sk-…", "https://platform.openai.com/api-keys"),
        // Google AI speaks the OpenAI dialect on this endpoint. The key comes from AI Studio
        // (sign in with your Gmail). A Google AI Pro/Ultra subscription does not cover API use.
        new ProviderPreset("gemini", "Google Gemini (API key)", ProviderKind.OpenAiCompatible,
            "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.8-flash",
            "AIza…", "https://aistudio.google.com/apikey"),
        new ProviderPreset("openrouter", "OpenRouter", ProviderKind.OpenAiCompatible,
            "https://openrouter.ai/api/v1", "deepseek/deepseek-chat",
            "sk-or-…", "https://openrouter.ai/keys"),
        new ProviderPreset("groq", "Groq", ProviderKind.OpenAiCompatible,
            "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile",
            "gsk_…", "https://console.groq.com/keys"),
        new ProviderPreset("anthropic", "Anthropic", ProviderKind.Anthropic,
            "https://api.anthropic.com", "claude-sonnet-4-20250514",
            "sk-ant-…", "https://console.anthropic.com/settings/keys"),
        new ProviderPreset("ollama", "Ollama (local)", ProviderKind.OpenAiCompatible,
            "http://localhost:11434/v1", "llama3.1",
            "not required", "https://ollama.com", KeyOptional: true),
        new ProviderPreset("lmstudio", "LM Studio (local)", ProviderKind.OpenAiCompatible,
            "http://localhost:1234/v1", "local-model",
            "not required", "https://lmstudio.ai", KeyOptional: true),
        new ProviderPreset("custom", "Custom endpoint", ProviderKind.OpenAiCompatible,
            "http://localhost:8000/v1", "default",
            "if required", "", KeyOptional: true),
    };

    public static ProviderPreset Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(AppSettings s, ProviderPreset p)
    {
        s.Preset = p.Id;
        s.Provider = p.Kind;
        s.BaseUrl = p.BaseUrl;
        s.Model = p.Model;
    }
}

public static class Paths
{
    /// <summary>The directory the app was launched from, retained for commands that need a workspace.</summary>
    public static string LaunchDirectory { get; private set; } = Environment.CurrentDirectory;
    public static string Root { get; } = ResolveRoot();

    /// <summary>
    /// NanoWakeWord resolves its bundled ONNX models relative to the process working directory.
    /// Desktop shortcuts and `dotnet run` can start from arbitrary directories, so use the app
    /// output directory while keeping the original launch directory for the Antigravity workspace.
    /// </summary>
    public static void InitializeApplicationDirectory()
    {
        LaunchDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = AppContext.BaseDirectory;
    }

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable("JARVIS_HOME");
        var root = !string.IsNullOrWhiteSpace(custom)
            ? custom!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        return fullRoot;
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string NotesFile => Path.Combine(Root, "notes.md");
    public static string FactsFile => Path.Combine(Root, "memory.json");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ShotsDir => Path.Combine(Root, "shots");
    public static string ProfilesDir => Path.Combine(Root, "profiles");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ShotsDir);
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly object Gate = new();
    private static AppSettings? _cached;
    private static string? _loadWarning;
    private static string? _corruptBackupPath;
    private static bool _preserveUnreadableSettings;

    public static string? LastLoadWarning
    {
        get { lock (Gate) return _loadWarning; }
    }

    public static AppSettings Load()
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;
            Paths.Ensure();
            _loadWarning = null;
            _corruptBackupPath = null;
            _preserveUnreadableSettings = false;
            AppSettings s;
            try
            {
                s = File.Exists(Paths.SettingsFile)
                    ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Options) ?? new()
                    : new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Warn("settings unreadable; preserving the original and using defaults", ex);
                s = new AppSettings();
                _preserveUnreadableSettings = File.Exists(Paths.SettingsFile);
                if (_preserveUnreadableSettings)
                {
                    var backup = Paths.SettingsFile + ".corrupt-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Copy(Paths.SettingsFile, backup, overwrite: false);
                        _corruptBackupPath = backup;
                        _loadWarning = $"Saved settings could not be read. Jarvis is using defaults; the original file was preserved at '{backup}'.";
                    }
                    catch (Exception backupError)
                    {
                        Log.Warn("could not back up the unreadable settings file", backupError);
                        _loadWarning = $"Saved settings could not be read. Jarvis is using defaults, but the original file could not be backed up. It will not be replaced until a backup can be created.";
                    }
                }
                else
                {
                    _loadWarning = "Saved settings could not be read. Jarvis is using defaults; no settings file was replaced.";
                }
            }
            var needsSave = GeminiModelCatalog.MigrateLegacySelection(s);
            try { needsSave |= EncryptLegacySecrets(s); }
            catch (Exception ex) { Log.Warn("could not migrate a legacy plaintext secret", ex); }
            if (needsSave)
            {
                try { Persist(s); }
                catch (Exception ex) { Log.Warn("could not save migrated settings", ex); }
            }
            _cached = s;
            return s;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            if (_preserveUnreadableSettings && File.Exists(Paths.SettingsFile) &&
                (_corruptBackupPath is null || !File.Exists(_corruptBackupPath)))
            {
                var backup = Paths.SettingsFile + ".corrupt-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(Paths.SettingsFile, backup, overwrite: false);
                    _corruptBackupPath = backup;
                }
                catch (Exception ex)
                {
                    throw new IOException("Settings were not saved because the unreadable settings file could not be backed up.", ex);
                }
            }
            Persist(settings);
            _cached = settings;
            _preserveUnreadableSettings = false;
            _loadWarning = null;
        }
    }

    private static void Persist(AppSettings settings)
    {
        Paths.Ensure();
        EncryptLegacySecrets(settings);
        var tmp = Paths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, Paths.SettingsFile, overwrite: true);
    }

    private static bool EncryptLegacySecrets(AppSettings settings)
    {
        var changed = false;
        settings.ApiKeyEncrypted = Encrypt(settings.ApiKeyEncrypted, ref changed);
        settings.AzureKeyEncrypted = Encrypt(settings.AzureKeyEncrypted, ref changed);
        return changed;

        static string Encrypt(string stored, ref bool changed)
        {
            if (string.IsNullOrWhiteSpace(stored) || stored.StartsWith("dpapi:", StringComparison.Ordinal))
                return stored;
            var encrypted = Dpapi.Protect(stored);
            if (string.IsNullOrWhiteSpace(encrypted))
                throw new InvalidOperationException("Windows could not encrypt a saved credential. Settings were not written.");
            changed = true;
            return encrypted;
        }
    }

    public static void ResetCache() { lock (Gate) _cached = null; }
}

/// <summary>Windows DPAPI: the API key is encrypted for this user on this machine only.</summary>
public static class Dpapi
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        byte[]? bytes = null;
        Blob input = default;
        try
        {
            bytes = Encoding.UTF8.GetBytes(plain);
            input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (!CryptProtectData(ref input, "JARVIS API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try { return "dpapi:" + Convert.ToBase64String(PtrToBytes(output)); }
            finally { LocalFree(output.Data); }
        }
        catch (CryptographicException ex)
        {
            Log.Warn("DPAPI protect failed", ex);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI protect failed", ex);
            throw new CryptographicException("Windows could not protect the saved credential.", ex);
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                if (input.Size > 0) Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size);
                Marshal.FreeHGlobal(input.Data);
            }
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        if (!cipher.StartsWith("dpapi:", StringComparison.Ordinal)) return cipher!; // legacy/plain
        try
        {
            var bytes = Convert.FromBase64String(cipher["dpapi:".Length..]);
            var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                    return "";
                try { return Encoding.UTF8.GetString(PtrToBytes(output)); }
                finally { LocalFree(output.Data); }
            }
            finally { Marshal.FreeHGlobal(input.Data); }
        }
        catch (Exception ex)
        {
            Log.Warn("DPAPI unprotect failed", ex);
            return "";
        }
    }

    private static byte[] PtrToBytes(Blob b)
    {
        var arr = new byte[b.Size];
        if (b.Size > 0) Marshal.Copy(b.Data, arr, 0, b.Size);
        return arr;
    }
}
