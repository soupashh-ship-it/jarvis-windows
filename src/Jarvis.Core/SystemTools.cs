using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace Jarvis.Core;

public static class SystemTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(9) };

    static SystemTools()
    {
        try { Http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.5.0"); } catch { }
    }

    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "volume",
            Category = "system",
            ReadOnly = false,
            Description = "System output volume. action: get, set (with level 0-100), up, down, mute, unmute.",
            ParametersJson = T.Schema(
                T.OneOf("action", "What to do.", new[] { "get", "set", "up", "down", "mute", "unmute" }, true),
                T.Int("level", "Volume 0-100, for action=set.")),
            Handler = (_, args) => Task.FromResult(Volume(Json.Str(args, "action", "get"), Json.Int(args, "level", 50)))
        };

        yield return new ToolDefinition
        {
            Name = "media",
            Category = "system",
            ReadOnly = false,
            Description = "Control whatever is playing music or video. action: play_pause, play, pause, next, previous, stop.",
            ParametersJson = T.Schema(T.OneOf("action", "Playback action.",
                new[] { "play_pause", "play", "pause", "next", "previous", "stop" }, true)),
            Handler = (_, args) =>
            {
                var action = Json.Str(args, "action", "play_pause").ToLowerInvariant();
                var key = action switch
                {
                    "next" => "next",
                    "previous" => "previous",
                    "stop" => "stopmedia",
                    _ => "playpause"
                };
                var (ok, unknown) = InputApi.PressCombo(key);
                return Task.FromResult(ok
                    ? ToolResult.Done($"Sent {action.Replace('_', ' ')}.")
                    : ToolResult.Fail($"Unknown media key {string.Join(",", unknown)}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "system",
            Category = "system",
            ReadOnly = false,
            Description = "Power and session actions: lock, sleep, signout, shutdown, restart. Destructive ones ask first.",
            ParametersJson = T.Schema(T.OneOf("action", "What to do.",
                new[] { "lock", "sleep", "signout", "shutdown", "restart" }, true)),
            Describe = a => $"{Json.Str(a, "action")} the computer",
            Handler = (ctx, args) =>
            {
                var action = Json.Str(args, "action").ToLowerInvariant();
                try
                {
                    switch (action)
                    {
                        case "lock":
                            return Task.FromResult(Native.LockWorkStation()
                                ? ToolResult.Done("Locked.")
                                : ToolResult.Fail($"Windows could not lock the workstation (Win32 error {Marshal.GetLastWin32Error()})."));
                        case "sleep":
                            return Task.FromResult(Native.SetSuspendState(false, false, false)
                                ? ToolResult.Done("Going to sleep.")
                                : ToolResult.Fail($"Windows could not suspend the computer (Win32 error {Marshal.GetLastWin32Error()})."));
                        case "signout":
                            return Task.FromResult(Run("shutdown.exe", "/l")
                                ? ToolResult.Done("Signing out.")
                                : ToolResult.Fail("Windows did not accept the sign-out request."));
                        case "shutdown":
                            return Task.FromResult(Run("shutdown.exe", "/s /t 5 /c \"JARVIS: shutting down\"")
                                ? ToolResult.Done("Shutting down in five seconds.")
                                : ToolResult.Fail("Windows did not accept the shutdown request."));
                        case "restart":
                            return Task.FromResult(Run("shutdown.exe", "/r /t 5 /c \"JARVIS: restarting\"")
                                ? ToolResult.Done("Restarting in five seconds.")
                                : ToolResult.Fail("Windows did not accept the restart request."));
                        default:
                            return Task.FromResult(ToolResult.Fail($"I do not know the system action '{action}'."));
                    }
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail($"That failed: {ex.Message}")); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "clipboard",
            Category = "system",
            ReadOnly = false,
            Description = "Read or write the clipboard. action: get or set (with text).",
            ParametersJson = T.Schema(
                T.OneOf("action", "What to do.", new[] { "get", "set" }, true),
                T.Str("text", "Text to put on the clipboard, for action=set.")),
            Handler = (_, args) =>
            {
                var action = Json.Str(args, "action", "get").ToLowerInvariant();
                if (action == "set")
                {
                    var text = Json.Str(args, "text");
                    return Task.FromResult(ClipboardApi.SetText(text)
                        ? ToolResult.Done("Put it on the clipboard.")
                        : ToolResult.Fail("The clipboard would not accept it."));
                }
                var content = ClipboardApi.GetText();
                return Task.FromResult(ToolResult.Done(content.Length == 0
                    ? "The clipboard is empty."
                    : content.Length > 4000 ? content[..4000] + "…" : content));
            }
        };

        yield return new ToolDefinition
        {
            Name = "notify",
            Category = "system",
            ReadOnly = true,
            Description = "Show a desktop notification, for things worth seeing like a link or a number to copy.",
            ParametersJson = T.Schema(T.Str("title", "Short title.", true), T.Str("body", "Notification body.", true)),
            Handler = async (ctx, args) =>
            {
                await ctx.Services.Ui.NotifyAsync(Json.Str(args, "title", "JARVIS"), Json.Str(args, "body")).ConfigureAwait(false);
                return ToolResult.Done("Shown.");
            }
        };

        yield return new ToolDefinition
        {
            Name = "timer",
            Category = "system",
            ReadOnly = true,
            Description = "Set a timer. When it finishes, JARVIS says so out loud. seconds or minutes; label is optional.",
            ParametersJson = T.Schema(
                T.Int("seconds", "Delay in seconds (or use minutes)."),
                T.Num("minutes", "Delay in minutes (alternative to seconds)."),
                T.Str("label", "What the timer is for, e.g. 'the pasta'.")),
            Handler = (ctx, args) =>
            {
                var seconds = Json.Num(args, "seconds", 0);
                var minutes = Json.Num(args, "minutes", 0);
                var total = Math.Max(seconds, minutes * 60);
                if (total <= 0) return Task.FromResult(ToolResult.Fail("Tell me how long the timer should be."));
                if (total > 24 * 3600) return Task.FromResult(ToolResult.Fail("That is longer than a day; I would forget."));
                var label = Json.Str(args, "label");
                var item = ctx.Services.Timers.Add(TimeSpan.FromSeconds(total), label);
                var friendly = total < 90 ? $"{total:0} seconds" : total < 5400 ? $"{total / 60:0} minutes" : $"{total / 3600:0.#} hours";
                return Task.FromResult(ToolResult.Done($"Timer set for {friendly}{(label.Length > 0 ? $" ({label})" : "")}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "cancel_timer",
            Category = "system",
            ReadOnly = true,
            Description = "Cancel a running timer by its label or id, or 'all'.",
            ParametersJson = T.Schema(T.Str("target", "Label, id, or 'all'.", true)),
            Handler = (ctx, args) =>
            {
                var target = Json.Str(args, "target");
                if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var all = ctx.Services.Timers.Active.ToList();
                    foreach (var t in all) ctx.Services.Timers.Cancel(t.Id);
                    return Task.FromResult(ToolResult.Done($"Cancelled {all.Count} timer(s)."));
                }
                return Task.FromResult(ctx.Services.Timers.Cancel(target)
                    ? ToolResult.Done("Timer cancelled.")
                    : ToolResult.Fail("No timer matches that."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "system_info",
            Category = "system",
            ReadOnly = true,
            Description = "Report CPU, memory, disks, battery, uptime and idle time for this PC.",
            ParametersJson = T.Schema(),
            Handler = (_, _) => Task.FromResult(ToolResult.Done(SystemProbe.Summary() +
                $" Idle for {(int)SystemProbe.IdleTime().TotalMinutes} minutes."))
        };

        yield return new ToolDefinition
        {
            Name = "weather",
            Category = "system",
            ReadOnly = true,
            Description = "Current weather for a city (defaults to the configured city).",
            ParametersJson = T.Schema(T.Str("city", "City name; empty uses the configured city.")),
            Handler = async (ctx, args) =>
            {
                if (!ctx.Services.Settings.AllowNetwork)
                    return ToolResult.Fail("Network access is switched off in settings.");
                var city = Json.Str(args, "city");
                if (string.IsNullOrWhiteSpace(city)) city = ctx.Services.Settings.City;
                var slug = Uri.EscapeDataString(city.Trim()).Replace("%20", "+");
                try
                {
                    var text = await Http.GetStringAsync($"https://wttr.in/{slug}?format=%C,+%t,+feels+like+%f")
                        .ConfigureAwait(false);
                    return ToolResult.Done($"{city}: {text.Trim()}");
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail($"I could not reach the weather service: {ex.Message}");
                }
            }
        };
    }

    public static ToolResult Volume(string action, int level)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;

            switch (action.ToLowerInvariant())
            {
                case "set":
                    volume.MasterVolumeLevelScalar = Math.Clamp(level, 0, 100) / 100f;
                    break;
                case "up":
                    volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + 0.08f, 0f, 1f);
                    break;
                case "down":
                    volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar - 0.08f, 0f, 1f);
                    break;
                case "mute":
                    volume.Mute = true;
                    break;
                case "unmute":
                    volume.Mute = false;
                    break;
            }

            var percent = (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
            return volume.Mute
                ? ToolResult.Done("Muted.")
                : ToolResult.Done($"Volume at {percent} percent.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"I could not reach the audio device: {ex.Message}");
        }
    }

    private static bool Run(string file, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (process is null) return false;
        return !process.WaitForExit(2_000) || process.ExitCode == 0;
    }
}

public static class FileShellTools
{
    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "read_file",
            Category = "files",
            ReadOnly = true,
            Description = "Read a text file and return its contents (truncated to max_chars).",
            ParametersJson = T.Schema(
                T.Str("path", "Full path to the file.", true),
                T.Int("max_chars", "Maximum characters to return (default 8000).")),
            Handler = (_, args) =>
            {
                var path = Environment.ExpandEnvironmentVariables(Json.Str(args, "path"));
                try
                {
                    if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail($"There is no file at {path}."));
                    var max = Math.Clamp(Json.Int(args, "max_chars", 8000), 200, 60000);
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                    var buffer = new char[max];
                    var read = reader.ReadBlock(buffer, 0, max);
                    var text = new string(buffer, 0, read);
                    return Task.FromResult(ToolResult.Done(text.Length == 0 ? "(the file is empty)" : text));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail($"Could not read it: {ex.Message}")); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "write_file",
            Category = "files",
            ReadOnly = false,
            Description = "Write text to a file, creating folders as needed. Outside the user's own folders it asks first.",
            ParametersJson = T.Schema(
                T.Str("path", "Full path to write.", true),
                T.Str("content", "The complete new contents of the file.", true)),
            Describe = a => $"write the file {Path.GetFileName(Json.Str(a, "path"))}",
            Handler = (ctx, args) =>
            {
                var path = Environment.ExpandEnvironmentVariables(Json.Str(args, "path"));
                var content = Json.Str(args, "content");
                try
                {
                    if (string.IsNullOrWhiteSpace(ctx.ApprovedTargetPath))
                        return Task.FromResult(ToolResult.Fail("Could not write it: policy did not provide an approved destination."));
                    SafeFileWriter.WriteAllText(path, content, ctx.ApprovedTargetPath);
                    return Task.FromResult(ToolResult.Done($"Wrote {content.Length} characters to {path}."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail($"Could not write it: {ex.Message}")); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "search_files",
            Category = "files",
            ReadOnly = true,
            Description = "Search for files by name under the user's folders (or a given directory). Returns matching paths.",
            ParametersJson = T.Schema(
                T.Str("query", "Part of a file name, or a wildcard like *.pdf.", true),
                T.Str("directory", "Where to search. Empty searches Desktop, Documents, Downloads and the user folder."),
                T.Int("limit", "Maximum results (default 20).")),
            Handler = (_, args) =>
            {
                var query = Json.Str(args, "query");
                var directory = Json.Str(args, "directory");
                if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ToolResult.Fail("What should I look for?"));
                var limit = Math.Clamp(Json.Int(args, "limit", 20), 1, 80);
                var roots = new List<string>();
                if (!string.IsNullOrWhiteSpace(directory)) roots.Add(Environment.ExpandEnvironmentVariables(directory));
                else
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    roots.Add(Path.Combine(home, "Desktop"));
                    roots.Add(Path.Combine(home, "Documents"));
                    roots.Add(Path.Combine(home, "Downloads"));
                    roots.Add(home);
                }
                var results = FileSearch.Find(query, roots, limit);
                return Task.FromResult(ToolResult.Done(results.Count == 0
                    ? "Nothing matched."
                    : string.Join(Environment.NewLine, results)));
            }
        };

        yield return new ToolDefinition
        {
            Name = "delete_path",
            Category = "files",
            ReadOnly = false,
            Description = "Delete a file or folder (folders recurse). Always asks the user first.",
            ParametersJson = T.Schema(T.Str("path", "Full path to delete.", true)),
            Describe = a => $"delete {Json.Str(a, "path")}",
            Handler = (_, args) =>
            {
                var path = Environment.ExpandEnvironmentVariables(Json.Str(args, "path"));
                try
                {
                    if (File.Exists(path)) { File.Delete(path); return Task.FromResult(ToolResult.Done($"Deleted {path}.")); }
                    if (Directory.Exists(path)) { Directory.Delete(path, true); return Task.FromResult(ToolResult.Done($"Deleted the folder {path}.")); }
                    return Task.FromResult(ToolResult.Fail($"There is nothing at {path}."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail($"Could not delete it: {ex.Message}")); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "run_powershell",
            Category = "shell",
            ReadOnly = false,
            Description = "Run a PowerShell command on this PC and return its output. Risky commands ask the user first. " +
                          "Use this for real work: file operations, system queries, scripting, git, package tools.",
            ParametersJson = T.Schema(
                T.Str("command", "The PowerShell command to run.", true),
                T.Str("working_directory", "Directory to run it in; empty means the user's home folder.")),
            Describe = a => $"run the command: {Truncate(Json.Str(a, "command"), 70)}",
            Handler = async (ctx, args) =>
            {
                var command = Json.Str(args, "command");
                var cwd = Json.Str(args, "working_directory");
                if (string.IsNullOrWhiteSpace(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var result = await ShellRunner.RunAsync(command, cwd, TimeSpan.FromSeconds(120), ctx.Cancellation)
                    .ConfigureAwait(false);
                ctx.Services.Events.Emit("shell", Truncate(command, 120), true,
                    ("exit", result.ExitCode), ("ms", result.DurationMs));
                if (result.TimedOut)
                    return ToolResult.Fail($"That took longer than two minutes, so I stopped it.\n{Truncate(result.Output, 3000)}");
                if (result.ExitCode != 0)
                    return ToolResult.Fail($"The command exited with code {result.ExitCode} after {result.DurationMs} ms.\n{Truncate(result.Output, 8000)}");
                return ToolResult.Done($"exit 0 in {result.DurationMs} ms\n{Truncate(result.Output, 8000)}");
            }
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public static class FileSearch
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "obj", "bin", "AppData", "Windows", "ProgramData", "$Recycle.Bin",
        "System Volume Information", ".vs", "packages", "venv", ".venv", "__pycache__"
    };

    public static List<string> Find(string query, IEnumerable<string> roots, int limit)
    {
        var results = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        var pattern = query.Contains('*') || query.Contains('?') ? query : $"*{query}*";

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in EnumerateSafe(root, pattern, deadline))
                {
                    results.Add(file);
                    if (results.Count >= limit) return results;
                }
            }
            catch { }
        }
        return results;
    }

    private static IEnumerable<string> EnumerateSafe(string root, string pattern, DateTime deadline)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var scanned = 0;

        while (stack.Count > 0)
        {
            if (DateTime.UtcNow > deadline || scanned > 25000) yield break;
            var dir = stack.Pop();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, pattern); }
            catch { continue; }

            foreach (var f in files)
            {
                scanned++;
                yield return f;
                if (scanned > 25000) yield break;
            }

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (Skip.Contains(name) || name.StartsWith('.')) continue;
                stack.Push(d);
            }
        }
    }
}

public sealed record ShellResult(int ExitCode, string Output, bool TimedOut, long DurationMs);

public static class ShellRunner
{
    public static async Task<ShellResult> RunAsync(string command, string workingDirectory,
        TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + command));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, ex.Message, false, started.ElapsedMilliseconds);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        string output;
        try { output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)); }
        catch { output = ""; }

        return new ShellResult(timedOut ? -1 : process.ExitCode, output.Trim(), timedOut, started.ElapsedMilliseconds);
    }
}
