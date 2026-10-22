using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Jarvis.Core;

public enum LaunchKind { Shortcut, Executable, StoreApp, Uri }

public sealed record AppEntry(string Name, string Id, LaunchKind Kind, string Target, string Args = "")
{
    public string Key => Id.ToLowerInvariant();
}

/// <summary>
/// A real Windows app index: Start Menu shortcuts, App Paths, and Store apps enumerated through the
/// shell namespace. This is what makes "open spotify" work without hard-coded paths.
/// </summary>
public sealed class AppCatalog
{
    private static readonly Lazy<AppCatalog> Lazy = new(() => new AppCatalog(), LazyThreadSafetyMode.ExecutionAndPublication);
    public static AppCatalog Instance => Lazy.Value;

    private readonly object _gate = new();
    private List<AppEntry> _apps = new();
    private bool _storeLoaded;
    private DateTime _built = DateTime.MinValue;

    public IReadOnlyList<AppEntry> All
    {
        get
        {
            lock (_gate)
            {
                if (_apps.Count == 0) RebuildLocked();
                return _apps.ToArray();
            }
        }
    }

    public void Refresh()
    {
        lock (_gate) RebuildLocked();
    }

    private void RebuildLocked()
    {
        var apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in StartMenuDirs())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                        var id = "lnk:" + file.ToLowerInvariant();
                        apps.TryAdd(id, new AppEntry(Clean(name), id, LaunchKind.Shortcut, file));
                    }
                    catch { }
                }
            }
            catch { }
        }

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Registry64)
                 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;
                foreach (var sub in appPaths.GetSubKeyNames())
                {
                    try
                    {
                        using var key = appPaths.OpenSubKey(sub);
                        var target = key?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        target = target!.Trim('"');
                        if (!File.Exists(target)) continue;
                        var name = Path.GetFileNameWithoutExtension(sub);
                        var id = "exe:" + name.ToLowerInvariant();
                        apps.TryAdd(id, new AppEntry(Clean(name), id, LaunchKind.Executable, target));
                    }
                    catch { }
                }
            }
            catch { }
        }

        _apps = apps.Values.ToList();
        _built = DateTime.UtcNow;
    }

    private static IEnumerable<string> StartMenuDirs()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static string Clean(string name)
    {
        var cleaned = name.Replace(" - Shortcut", "").Trim();
        return cleaned.Length == 0 ? name : cleaned;
    }

    /// <summary>Store apps are only enumerated on demand: the shell namespace walk is comparatively slow.</summary>
    private void EnsureStoreApps()
    {
        lock (_gate)
        {
            if (_storeLoaded) return;
            _storeLoaded = true;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null) return;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell is null) return;
                dynamic? folder = shell.NameSpace("shell:AppsFolder");
                if (folder is null) return;
                dynamic items = folder.Items();
                var count = 0;
                foreach (dynamic item in items)
                {
                    if (++count > 500) break;
                    try
                    {
                        string name = item.Name;
                        string aumid = item.Path;
                        if (string.IsNullOrWhiteSpace(aumid) || aumid.Contains('\\')) continue;
                        var id = "uwp:" + aumid.ToLowerInvariant();
                        _apps.Add(new AppEntry(Clean(name), id, LaunchKind.StoreApp, aumid));
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("store app enumeration unavailable: " + ex.Message);
            }
        }
    }

    public IReadOnlyList<AppEntry> Find(string query, int limit = 12)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return Array.Empty<AppEntry>();

        lock (_gate)
        {
            if (_apps.Count == 0) RebuildLocked();
            if (!_storeLoaded) EnsureStoreApps();
            var snapshot = _apps.ToArray();

            return snapshot
                .Select(a => (a, score: Score(a, q)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.a.Name.Length)
                .Take(limit)
                .Select(x => x.a)
                .ToArray();
        }
    }

    private static int Score(AppEntry app, string q)
    {
        var n = app.Name.ToLowerInvariant();
        if (n == q) return 100;
        if (n.StartsWith(q)) return 88;
        if (n.Contains(" " + q)) return 76;
        if (n.Contains(q)) return 66;
        if (app.Kind == LaunchKind.Executable && app.Target.ToLowerInvariant().Contains(q)) return 40;
        return 0;
    }

    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["browser"] = "browser", ["the browser"] = "browser", ["chrome"] = "chrome", ["edge"] = "edge",
        ["files"] = "explorer", ["file manager"] = "explorer", ["explorer"] = "explorer",
        ["terminal"] = "terminal", ["powershell"] = "terminal", ["command prompt"] = "cmd",
        ["settings"] = "ms-settings:", ["windows settings"] = "ms-settings:",
        ["calculator"] = "calc", ["notepad"] = "notepad", ["paint"] = "mspaint",
        ["task manager"] = "taskmgr", ["control panel"] = "control",
        ["spotify"] = "spotify:", ["discord"] = "discord", ["steam"] = "steam",
        ["vscode"] = "code", ["vs code"] = "code", ["visual studio code"] = "code"
    };

    public static string ResolveAlias(string query) =>
        Aliases.TryGetValue(query.Trim(), out var alias) ? alias : query.Trim();
}

public static class AppLauncher
{
    public sealed record LaunchResult(bool Launched, string Message, string? ProcessName);

    public static LaunchResult Launch(string query)
    {
        var original = query.Trim();
        var resolved = AppCatalog.ResolveAlias(original);

        // 1. A window that already matches: bring it forward instead of starting a second copy.
        var existing = WindowApi.Find(resolved);
        if (existing.Count > 0 && WindowApi.Focus(existing[0]))
            return new LaunchResult(true, $"{existing[0].Title} was already open; brought it to the front.",
                existing[0].ProcessName);

        // 2. Built-in targets that are not ordinary applications.
        switch (resolved.ToLowerInvariant())
        {
            case "browser":
                OpenUri("https://www.google.com");
                return new LaunchResult(true, "Opened your default browser.", null);
            case "explorer":
                Start("explorer.exe", null);
                return new LaunchResult(true, "Opened File Explorer.", "Explorer");
            case "terminal":
                return StartTerminal();
            case "cmd":
                Start("cmd.exe", "/K cd /d %USERPROFILE%");
                return new LaunchResult(true, "Opened Command Prompt.", "cmd");
            case "taskmgr":
                Start("taskmgr.exe", null);
                return new LaunchResult(true, "Opened Task Manager.", "Taskmgr");
            case "calc":
                Start("calc.exe", null);
                return new LaunchResult(true, "Opened Calculator.", "Calculator");
            case "notepad":
                Start("notepad.exe", null);
                return new LaunchResult(true, "Opened Notepad.", "Notepad");
            case "mspaint":
                Start("mspaint.exe", null);
                return new LaunchResult(true, "Opened Paint.", "mspaint");
            case "control":
                Start("control.exe", null);
                return new LaunchResult(true, "Opened Control Panel.", "control");
        }

        if (resolved.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            OpenUri(resolved);
            return new LaunchResult(true, "Opened Windows Settings.", "SystemSettings");
        }

        if (resolved.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            OpenUri(resolved);
            return new LaunchResult(true, "Opening Spotify.", "Spotify");
        }

        // 3. The app index.
        var matches = AppCatalog.Instance.Find(resolved);
        if (matches.Count == 0)
            return new LaunchResult(false,
                $"No installed app matches '{original}'. Try list_apps, or give me the full path.", null);

        var app = matches[0];
        try
        {
            switch (app.Kind)
            {
                case LaunchKind.Shortcut:
                    Start(app.Target, null);
                    break;
                case LaunchKind.Executable:
                    Start(app.Target, string.IsNullOrWhiteSpace(app.Args) ? null : app.Args);
                    break;
                case LaunchKind.StoreApp:
                    Start("explorer.exe", $"shell:AppsFolder\\{app.Target}");
                    break;
                case LaunchKind.Uri:
                    OpenUri(app.Target);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"launch failed for {app.Name}", ex);
            return new LaunchResult(false, $"I could not start {app.Name}: {ex.Message}", null);
        }

        // Wait briefly so the window exists and can be focused before we report success.
        var procName = app.Kind == LaunchKind.Executable ? Path.GetFileNameWithoutExtension(app.Target) : app.Name;
        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(150);
            var windows = WindowApi.Find(app.Name);
            if (windows.Count == 0 && app.Kind == LaunchKind.Executable) windows = WindowApi.Find(procName);
            if (windows.Count > 0)
            {
                WindowApi.Focus(windows[0]);
                return new LaunchResult(true, $"Launched {app.Name}; '{windows[0].Title}' is in front.", procName);
            }
        }
        return new LaunchResult(true, $"Launched {app.Name}. It is still starting up.", procName);
    }

    private static LaunchResult StartTerminal()
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\wt.exe");
        if (File.Exists(wt)) { Start(wt, null); return new LaunchResult(true, "Opened Windows Terminal.", "WindowsTerminal"); }
        Start("powershell.exe", null);
        return new LaunchResult(true, "Opened PowerShell.", "PowerShell");
    }

    public static void Start(string file, string? args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
        Process.Start(psi);
    }

    public static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
}

public static class AppTools
{
    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "launch_app",
            Category = "desktop",
            ReadOnly = false,
            Description = "Open an installed Windows application by name (for example 'spotify', 'discord', 'steam', " +
                          "'visual studio code', 'calculator'). If it is already running, its window is brought to the front.",
            ParametersJson = T.Schema(T.Str("name", "Application name, as a person would say it.", true)),
            Describe = a => $"open {Json.Str(a, "name")}",
            Handler = (ctx, args) =>
            {
                var name = Json.Str(args, "name");
                var result = AppLauncher.Launch(name);
                ctx.Services.Events.Emit("app_launch", result.Message, true, ("query", name), ("ok", result.Launched));
                return Task.FromResult(result.Launched ? ToolResult.Done(result.Message) : ToolResult.Fail(result.Message));
            }
        };

        yield return new ToolDefinition
        {
            Name = "list_apps",
            Category = "desktop",
            ReadOnly = true,
            Description = "List installed applications whose name contains the query (empty query lists the first 80).",
            ParametersJson = T.Schema(T.Str("query", "Text to match, or empty for a general list.")),
            Handler = (ctx, args) =>
            {
                var q = Json.Str(args, "query");
                var apps = string.IsNullOrWhiteSpace(q)
                    ? AppCatalog.Instance.All.OrderBy(a => a.Name).Take(80).ToList()
                    : AppCatalog.Instance.Find(q, 40).ToList();
                return Task.FromResult(ToolResult.Done(apps.Count == 0
                    ? "Nothing matched."
                    : string.Join(", ", apps.Select(a => a.Name).Distinct())));
            }
        };

        yield return new ToolDefinition
        {
            Name = "open_path",
            Category = "desktop",
            ReadOnly = false,
            Description = "Open a file, folder or URL with its default application.",
            ParametersJson = T.Schema(T.Str("target", "A file path, folder path, or URL.", true)),
            Describe = a => $"open {Json.Str(a, "target")}",
            Handler = (ctx, args) =>
            {
                var target = Environment.ExpandEnvironmentVariables(Json.Str(args, "target"));
                try
                {
                    if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) AppLauncher.OpenUri(target);
                    else if (File.Exists(target) || Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    else return Task.FromResult(ToolResult.Fail($"There is nothing at {target}."));
                    return Task.FromResult(ToolResult.Done($"Opened {target}."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail($"Could not open it: {ex.Message}")); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "close_app",
            Category = "desktop",
            ReadOnly = false,
            Description = "Ask an application's main window to close (gracefully, like clicking its X). " +
                          "Prefer this over killing processes.",
            ParametersJson = T.Schema(T.Str("name", "Application or window name.", true)),
            Describe = a => $"close {Json.Str(a, "name")}",
            Handler = (ctx, args) =>
            {
                var name = Json.Str(args, "name");
                var windows = WindowApi.Find(name);
                if (windows.Count == 0) return Task.FromResult(ToolResult.Fail($"No open window matches '{name}'."));
                var w = windows[0];
                Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return Task.FromResult(ToolResult.Done($"Asked {w.Title} to close."));
            }
        };
    }
}

internal static class Json
{
    public static string Str(JsonElement args, string name, string fallback = "") =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    public static int Int(JsonElement args, string name, int fallback = 0)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => fallback
        };
    }

    public static double Num(JsonElement args, string name, double fallback = 0)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => fallback
        };
    }

    public static bool Bool(JsonElement args, string name, bool fallback = false)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => fallback
        };
    }
}
