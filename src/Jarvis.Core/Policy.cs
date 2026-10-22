using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Core;

public enum RiskLevel { Allow, Confirm, Deny }

public sealed record PolicyDecision(RiskLevel Level, string Reason, string? RuleKey = null, string? TargetPath = null)
{
    public static readonly PolicyDecision Allow = new(RiskLevel.Allow, "");
    public static PolicyDecision Confirm(string rule, string? targetPath = null) => new(RiskLevel.Confirm, "", rule, targetPath);
    public static PolicyDecision Deny(string reason, string rule) => new(RiskLevel.Deny, reason, rule);
}

/// <summary>
/// The gate. Every tool call passes through here before it runs.
/// Rules are deliberately conservative: anything destructive, remote, or outside the user's own
/// files becomes a spoken question; a small set of catastrophic commands is refused outright.
/// </summary>
public sealed class PolicyEngine
{
    private readonly AppSettings _settings;

    public PolicyEngine(AppSettings settings) => _settings = settings;

    private static readonly string[] Catastrophic =
    {
        @"\bformat\s+[a-z]:", @"\bdiskpart\b", @"\bbcdedit\b", @"\bvssadmin\s+delete",
        @"\bRemove-Item\b[^\n]*\bHKLM:\\?SYSTEM", @"\bcipher\s+/w", @"\bwbadmin\s+delete",
        @"\bnet\s+user\s+\S+\s+/delete", @"\breg\s+delete\s+HKLM\\SAM"
    };

    public PolicyDecision Evaluate(ToolDefinition tool, JsonElement args)
    {
        var name = tool.Name;

        switch (name)
        {
            case "read_file":
                return EvaluateRead(Get(args, "path"));
            case "write_file":
            {
                var path = Get(args, "path");
                return EvaluateWrite(path);
            }
            case "run_powershell":
            {
                var command = Get(args, "command");
                foreach (var p in Catastrophic)
                    if (Regex.IsMatch(command, p, RegexOptions.IgnoreCase))
                        return PolicyDecision.Deny(
                            "That command is destructive in a way I will not carry out, sir.", "deny.catastrophic");
                // A heuristic scan cannot prove that a shell command is harmless. Require an
                // explicit approval for every command, and scope "always allow" to this exact
                // command so a later prompt-injected command cannot inherit the approval.
                return PolicyDecision.Confirm("shell.command." + StableKey(command));
            }
            case "press_keys":
            {
                var keys = Get(args, "keys").ToLowerInvariant();
                var combo = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (combo.Contains("enter") || combo.Contains("return"))
                    return PolicyDecision.Confirm("keys.enter");
                if (combo.Contains("win") && (combo.Contains("r") || combo.Contains("x")))
                    return PolicyDecision.Confirm("keys.system");
                return PolicyDecision.Allow;
            }
            case "window_action":
            {
                var action = Get(args, "action").ToLowerInvariant();
                return action is "close" ? PolicyDecision.Confirm("window.close") : PolicyDecision.Allow;
            }
            case "close_app":
            case "kill_process":
                return PolicyDecision.Confirm("process.kill");
            case "system":
            {
                var action = Get(args, "action").ToLowerInvariant();
                return action is "lock" or "sleep" or "shutdown" or "restart" or "signout"
                    ? PolicyDecision.Confirm("system.power")
                    : PolicyDecision.Allow;
            }
            case "open_path":
            {
                var target = Get(args, "target").ToLowerInvariant();
                var ext = Path.GetExtension(target);
                if (ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".reg" or ".scr" or ".vbs"
                    or ".jar" or ".lnk" or ".hta" or ".cpl" or ".msc" or ".application")
                    return PolicyDecision.Confirm("open.executable");
                return PolicyDecision.Allow;
            }
            case "delete_path":
                return PolicyDecision.Confirm("file.delete");
            case "move_path":
                return PolicyDecision.Confirm("file.move");
            case "install_package":
                return PolicyDecision.Confirm("system.install");
            case "send_email":
            case "post_message":
                return PolicyDecision.Confirm("send.external");
        }

        if (name.StartsWith("worker_", StringComparison.Ordinal)) return PolicyDecision.Allow;
        if (tool.ReadOnly) return PolicyDecision.Allow;
        if (_settings.RequireConfirmForRisky) return PolicyDecision.Confirm("tool.default");
        return PolicyDecision.Allow;
    }

    private PolicyDecision EvaluateWrite(string rawPath)
    {
        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath)); }
        catch { return PolicyDecision.Deny("That path is not valid.", "path.invalid"); }

        // Apply write-root checks to the destination Windows will follow, not just the lexical
        // path. A junction inside the profile can otherwise point writes outside the allow-list.
        var hasReparsePoint = HasReparsePointInPath(path);
        path = ResolveLinkTarget(path);

        // Bind an approval to the resolved destination. If a link is retargeted while Jarvis
        // waits for the user, the second policy check will have a different key and will refuse
        // to use the stale approval.
        if (hasReparsePoint)
            return PolicyDecision.Confirm("file.link." + StableKey(path), path);

        if (IsSensitive(path))
            return PolicyDecision.Confirm("file.sensitive." + StableKey(path), path);

        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath(),
            Paths.Root
        };
        roots.AddRange(_settings.WriteOkDirs.Where(d => !string.IsNullOrWhiteSpace(d)));

        var allowed = roots.Any(r =>
        {
            try
            {
                var full = ResolveLinkTarget(Path.GetFullPath(r));
                return path.StartsWith(full.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, full, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        });

        return allowed
            ? new PolicyDecision(RiskLevel.Allow, "", TargetPath: path)
            : PolicyDecision.Confirm("file.outside." + StableKey(path), path);
    }

    internal static string ResolveWriteTarget(string rawPath)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath));
        return ResolveLinkTarget(fullPath);
    }

    private PolicyDecision EvaluateRead(string rawPath)
    {
        string path;
        try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath)); }
        catch { return PolicyDecision.Deny("That path is not valid.", "path.invalid"); }

        return IsSensitive(path)
            ? PolicyDecision.Confirm("file.read." + StableKey(path))
            : PolicyDecision.Allow;
    }

    private static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16];

    private static readonly string[] SensitiveRelative =
    {
        @".ssh", @".aws", @".azure", @".gnupg", @".config\gcloud", @".config\gh", @".docker", @".kube",
        @"AppData\Roaming\Microsoft\Credentials", @"AppData\Local\Microsoft\Credentials",
        @"AppData\Roaming\Microsoft\Protect",
        @"AppData\Local\Google\Chrome\User Data", @"AppData\Local\Microsoft\Edge\User Data",
        @"AppData\Roaming\Mozilla\Firefox\Profiles",
        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"
    };

    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials", "credentials.json",
        "token.json", "secrets.json", "service-account.json", "settings.json"
    };

    private bool IsSensitive(string path)
    {
        path = ResolveLinkTarget(path);
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (SensitiveFileNames.Contains(fileName) || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".p12", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".key", StringComparison.OrdinalIgnoreCase))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var rel in SensitiveRelative)
        {
            var full = Path.Combine(home, rel);
            if (IsWithin(path, full)) return true;
        }
        // The assistant's own configuration and logs can contain API keys and user content.
        return IsWithin(path, Paths.Root);
    }

    private static string ResolveLinkTarget(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            var item = File.Exists(fullPath) ? (FileSystemInfo)new FileInfo(fullPath) : new DirectoryInfo(fullPath);
            var target = item.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) return Path.GetFullPath(target.FullName);

            var parentPath = Path.GetDirectoryName(fullPath);
            var parent = string.IsNullOrWhiteSpace(parentPath) ? null : new DirectoryInfo(parentPath);
            while (parent is not null)
            {
                target = parent.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    var suffix = Path.GetRelativePath(parent.FullName, fullPath);
                    return Path.GetFullPath(Path.Combine(target.FullName, suffix));
                }
                parent = parent.Parent;
            }
        }
        catch { }
        return fullPath;
    }

    private static bool HasReparsePointInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return true;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception) { return true; }
        }
        return false;
    }

    private static bool IsWithin(string path, string directory)
    {
        try
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Get(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}

/// <summary>Command-risk heuristics for shell execution. Pattern matching, not a sandbox.</summary>
public static class RiskScan
{
    // A command word only counts where a command can start: beginning, after a separator or a wrapper.
    private const string Cmd =
        @"(?:^|[;&|(\n`]|\$\(|\b(?:then|do|else|exec|start-process|invoke-expression|iex|call|&)\s)" +
        @"(?:\s*-\S+|\s*\d+[smh]?\b)*\s*(?:[""']?\S*[\\/])?";

    private static readonly (string Pattern, string Label)[] Rules =
    {
        (Cmd + @"(rm|rmdir|del|erase|Remove-Item|Clear-Content|Move-Item|mv|rename|Ren)\b", "delete or move files"),
        (Cmd + @"(Stop-Process|taskkill|kill|pkill)\b", "stop a running process"),
        (Cmd + @"(shutdown|Restart-Computer|Stop-Computer|logoff)\b", "shut down or restart the machine"),
        (Cmd + @"(Set-Service|sc(\.exe)?\s+(config|stop|start|delete)|net\s+stop)\b", "change a Windows service"),
        (Cmd + @"(reg(\.exe)?\s+(add|delete)|(Set|New)-ItemProperty\s+HK)", "change the registry"),
        (Cmd + @"(winget\s+(install|uninstall|upgrade)|choco\s+install|msiexec)\b", "install or remove software"),
        (Cmd + @"(Set-ExecutionPolicy|Enable-PSRemoting|New-LocalUser|Add-LocalGroupMember)\b", "change system security"),
        (Cmd + @"(ssh|scp|rsync|Invoke-Command|Enter-PSSession)\b", "control another machine"),
        (@"\bgit\s+(push|reset\s+--hard|clean\s+-[a-z]*f)\b", "rewrite or publish a git repository"),
        (@"(Invoke-WebRequest|Invoke-RestMethod|curl|wget)[^\n]*(-Method\s+(Post|Put|Patch|Delete)|-Body\b|--data\b)",
            "send data to a web service"),
        (@"\b(Invoke-Expression|iex)\b[^\n]*(DownloadString|Invoke-WebRequest|curl)", "run code downloaded from the internet"),
        (@"(>|Out-File|Set-Content)[^\n]*(Startup|HKLM|HKLM:)", "write to a startup location"),
        (@"\b(\.ssh|id_rsa|credentials|password|\.aws|\.azure)\b", "touch credentials")
    };

    public static bool IsRisky(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        foreach (var (pattern, _) in Rules)
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                return true;
        return false;
    }

    public static string Describe(string command)
    {
        foreach (var (pattern, label) in Rules)
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                return label;
        return "run a shell command";
    }
}

public sealed record ApprovalRequest(
    string ToolName, string Question, string ArgsPreview, string RuleKey, RiskLevel Level)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public enum ApprovalOutcome { Allowed, Denied, TimedOut }

/// <summary>One question at a time, answered by voice or by the dialog the UI provides.</summary>
public sealed class ApprovalService
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private TaskCompletionSource<ApprovalOutcome>? _pending;

    /// <summary>The orchestrator installs this: speak the question, listen for yes or no.</summary>
    public Func<ApprovalRequest, CancellationToken, Task<ApprovalOutcome>>? Handler { get; set; }

    public event Action<ApprovalRequest>? Requested;
    public event Action<ApprovalRequest, ApprovalOutcome>? Resolved;

    public bool IsPending { get { lock (_gate) return _pending is not null; } }

    public async Task<ApprovalOutcome> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<ApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_gate) _pending = tcs;
            Requested?.Invoke(request);

            if (Handler is null)
            {
                tcs.TrySetResult(ApprovalOutcome.Denied);
            }
            else
            {
                // If the user answers on screen, the voice capture must be cancelled too, not left listening.
                using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var work = Handler(request, handlerCts.Token);
                var completed = await Task.WhenAny(work, tcs.Task).ConfigureAwait(false);
                if (completed == work)
                {
                    try { tcs.TrySetResult(await work.ConfigureAwait(false)); }
                    catch (OperationCanceledException) { tcs.TrySetResult(ApprovalOutcome.Denied); }
                    catch (Exception ex)
                    {
                        Log.Warn("approval handler failed", ex);
                        tcs.TrySetResult(ApprovalOutcome.Denied);
                    }
                }
                else
                {
                    handlerCts.Cancel();
                }
            }

            var result = await tcs.Task.ConfigureAwait(false);
            Resolved?.Invoke(request, result);
            return result;
        }
        finally
        {
            lock (_gate) _pending = null;
            _one.Release();
        }
    }

    /// <summary>Answer from a UI button, a hotkey, or the tray.</summary>
    public bool Answer(ApprovalOutcome outcome)
    {
        lock (_gate)
        {
            if (_pending is null) return false;
            _pending.TrySetResult(outcome);
            return true;
        }
    }

    public void CancelPending()
    {
        lock (_gate) _pending?.TrySetResult(ApprovalOutcome.Denied);
    }

}
