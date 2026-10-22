using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Jarvis.Core;

public sealed record ImageAttachment(string Base64, string Mime);

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public List<ImageAttachment>? Images { get; set; }

    public static ChatMessage User(string text, List<ImageAttachment>? images = null) =>
        new() { Role = "user", Content = text, Images = images };

    public static ChatMessage Assistant(string text, List<ToolCall>? calls = null) =>
        new() { Role = "assistant", Content = text, ToolCalls = calls };

    public static ChatMessage Tool(string toolCallId, string text) =>
        new() { Role = "tool", Content = text, ToolCallId = toolCallId };

    public int ApproxTokens =>
        (Content.Length + 3) / 4 +
        (ToolCalls?.Sum(c => (c.Name.Length + c.ArgumentsJson.Length) / 4) ?? 0) +
        (Images?.Count * 900 ?? 0);
}

/// <summary>
/// A tool the model asked for. <paramref name="ThoughtSignature"/> is the mark Gemini 3 gives a
/// tool call; it has to be sent back with the call or the next turn is refused.
/// </summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson, string ThoughtSignature = "");

public sealed record ModelRequest(
    string System,
    IReadOnlyList<ChatMessage> Messages,
    ToolRegistry Tools,
    double Temperature,
    int MaxTokens);

public sealed class ModelReply
{
    public string Text { get; set; } = "";
    public List<ToolCall> ToolCalls { get; } = new();
    public string StopReason { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public interface ILanguageModel
{
    string DisplayName { get; }
    Task<ModelReply> StreamAsync(ModelRequest request, Func<string, Task>? onText, CancellationToken ct);
}

public interface IConversationResettable
{
    void ResetConversation();
}

public sealed class ModelException : Exception
{
    public ModelException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class ModelFactory
{
    public static ILanguageModel? Create(AppSettings settings)
    {
        if (settings.Provider == ProviderKind.None) return null;
        if (settings.Provider == ProviderKind.AntigravityCli) return new AntigravityCliModel(settings);
        if (settings.Provider is ProviderKind.GoogleAccount or ProviderKind.Antigravity)
            return null; // Legacy consumer sign-ins are migrated to the supported Gemini API key provider.
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme is not ("http" or "https")))
            return null;
        if (endpoint.Scheme == Uri.UriSchemeHttp && !AppSettings.IsLocalEndpoint(settings.BaseUrl))
            return new ConfigurationErrorModel(
                "Remote model endpoints must use HTTPS so the API key and conversation are encrypted in transit.");
        // A hosted provider with no key would fail on the first turn; stay in local mode instead.
        if (string.IsNullOrWhiteSpace(settings.ApiKey) && !AppSettings.IsLocalEndpoint(settings.BaseUrl)) return null;
        return settings.Provider switch
        {
            ProviderKind.Anthropic => new AnthropicModel(settings),
            _ => new OpenAiCompatibleModel(settings)
        };
    }

    private sealed class ConfigurationErrorModel(string message) : ILanguageModel
    {
        public string DisplayName => "Invalid model endpoint";
        public Task<ModelReply> StreamAsync(ModelRequest request, Func<string, Task>? onText, CancellationToken ct) =>
            Task.FromException<ModelReply>(new ModelException(message));
    }
}

/// <summary>Runs a streamed Antigravity headless request for each turn.</summary>
public sealed class AntigravityCliModel : ILanguageModel, IConversationResettable, IDisposable
{
    // Reuse a single headless CLI conversation so later turns skip process startup and can reuse
    // Antigravity's prompt cache. User cancellation is the only per-turn deadline; complex actions
    // can legitimately take longer than a fixed wall-clock timeout.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private const int MaxTurnsPerProcess = 8;
    private const int MaxPromptCharacters = 28_000;
    private const string OmittedTurnsNotice = "[Earlier conversation omitted to fit the Antigravity prompt limit.]";
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private CliSession? _session;
    private bool _disposed;

    public AntigravityCliModel(AppSettings settings) => _settings = settings;
    public string DisplayName => $"{ConfiguredModel(_settings.Model)} via Google Antigravity";

    /// <summary>Starts the CLI session before the first request, without spending model tokens.</summary>
    public async Task PrepareAsync(CancellationToken ct = default)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = await EnsureSessionAsync(ct).ConfigureAwait(false);
        }
        finally { _turnGate.Release(); }
    }

    public async Task<ModelReply> StreamAsync(ModelRequest request, Func<string, Task>? onText, CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var model = ConfiguredModel(_settings.Model);
            var contextKey = StableContextKey(request.System);
            var session = _session;
            if (session is not null &&
                (session.Process.HasExited ||
                 (session.CompletedTurns > 0 &&
                  (session.CompletedTurns >= MaxTurnsPerProcess ||
                   !string.Equals(session.ContextKey, contextKey, StringComparison.Ordinal) ||
                   request.Messages.Count < session.LastMessageCount))))
            {
                await StopSessionAsync(session).ConfigureAwait(false);
            }

            session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            var firstTurn = session.CompletedTurns == 0;
            if (firstTurn) session.ContextKey = contextKey;
            var prompt = firstTurn
                ? BuildPrompt(request, session.ServerName)
                : BuildContinuationPrompt(request, session.ServerName);
            var turn = new PendingTurn(onText, Stopwatch.StartNew());
            lock (session.Gate)
            {
                if (session.ActiveTurn is not null)
                    throw new ModelException("Antigravity CLI already has an active turn.");
                session.ActiveTurn = turn;
            }

            try
            {
                var input = JsonSerializer.Serialize(new
                {
                    @event = "user",
                    message = new { content = prompt }
                });
                await session.Process.StandardInput.WriteLineAsync(input.AsMemory(), ct)
                    .ConfigureAwait(false);
                await session.Process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
                var cliResult = await turn.Completion.Task.WaitAsync(ct).ConfigureAwait(false);

                if (!string.Equals(cliResult.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    var error = cliResult.Error;
                    if (string.IsNullOrWhiteSpace(error)) error = await ReadStderrAsync(session).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(error)) error = "Antigravity CLI ended the turn without a success result.";
                    await StopSessionAsync(session).ConfigureAwait(false);
                    throw new ModelException(ExplainCliError(model, error));
                }

                var text = string.IsNullOrWhiteSpace(cliResult.Response)
                    ? cliResult.StreamedText
                    : cliResult.Response;
                if (string.IsNullOrWhiteSpace(text))
                {
                    var detail = string.IsNullOrWhiteSpace(cliResult.Error)
                        ? "No response text was returned."
                        : ExplainCliError(model, cliResult.Error);
                    Log.Warn($"Antigravity returned no text (status={cliResult.Status}, streamed={cliResult.StreamedText.Length} chars). {detail}");
                    await StopSessionAsync(session).ConfigureAwait(false);
                    throw new ModelException($"Google Antigravity returned no text for {model}. {detail}");
                }

                if (cliResult.StreamedText.Length == 0 && onText is not null)
                    await onText(text).ConfigureAwait(false);

                session.CompletedTurns++;
                session.LastMessageCount = request.Messages.Count;
                Log.Info($"Antigravity turn completed in {turn.Clock.ElapsedMilliseconds} ms " +
                         $"(first text {turn.FirstTextMilliseconds?.ToString() ?? "none"} ms, {text.Length} chars).");
                return new ModelReply { Text = text };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The output reader may still finish with an exception while StopSessionAsync
                // kills the process. Complete and detach the canceled turn first so it cannot
                // leave an unobserved task fault behind.
                lock (session.Gate)
                {
                    if (ReferenceEquals(session.ActiveTurn, turn)) session.ActiveTurn = null;
                }
                turn.Completion.TrySetCanceled(ct);
                await StopSessionAsync(session).ConfigureAwait(false);
                throw;
            }
            catch (ModelException) { throw; }
            catch (Exception ex)
            {
                await StopSessionAsync(session).ConfigureAwait(false);
                throw new ModelException($"Antigravity CLI session failed: {ex.Message}", ex);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModelException) { throw; }
        catch (Exception ex)
        {
            throw new ModelException($"Antigravity CLI could not complete the {ConfiguredModel(_settings.Model)} request: {ex.Message}", ex);
        }
        finally { _turnGate.Release(); }
    }

    private async Task<CliSession> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { } current && !current.Process.HasExited) return current;
        if (_session is { } stale) await StopSessionAsync(stale).ConfigureAwait(false);

        var serverName = AntigravityCliSetup.EnsureJarvisToolsAvailable();
        await JarvisMcpServer.VerifyAvailableAsync(ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = FindAgyExecutable(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.LaunchDirectory
        };
        var model = ConfiguredModel(_settings.Model);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--effort");
        psi.ArgumentList.Add(ModelEffort(model));
        psi.ArgumentList.Add("--input-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        // The CLI's print timeout is intentionally disabled too. stream-json requests may include
        // lengthy reasoning and MCP work; only Jarvis's explicit Stop/cancellation ends a turn.
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--disable-slash-commands");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new ModelException("Antigravity CLI (agy) could not start.");
        }
        catch (ModelException) { process.Dispose(); throw; }
        catch (Exception ex)
        {
            process.Dispose();
            throw new ModelException("Antigravity CLI (agy) could not start. Install it and sign in first.", ex);
        }

        var session = new CliSession(process, serverName);
        _session = session;
        session.StderrTask = process.StandardError.ReadToEndAsync();
        session.OutputTask = ReadSessionOutputAsync(session);
        try
        {
            await session.Ready.Task.WaitAsync(StartupTimeout, ct).ConfigureAwait(false);
            await JarvisMcpServer.WaitForClientConnectionAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await StopSessionAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadSessionOutputAsync(CliSession session)
    {
        Exception? failure = null;
        try
        {
            while (await session.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var eventName = root.TryGetProperty("event", out var eventElement)
                        ? eventElement.GetString()
                        : null;

                    if (string.Equals(eventName, "init", StringComparison.Ordinal))
                    {
                        session.Ready.TrySetResult(true);
                        continue;
                    }

                    if (string.Equals(eventName, "step_update", StringComparison.Ordinal) &&
                        root.TryGetProperty("step_update", out var update) &&
                        update.TryGetProperty("text_delta", out var deltaElement) &&
                        deltaElement.ValueKind == JsonValueKind.String)
                    {
                        var delta = deltaElement.GetString() ?? "";
                        if (delta.Length == 0) continue;
                        PendingTurn? turn;
                        lock (session.Gate) turn = session.ActiveTurn;
                        if (turn is null) continue;
                        turn.StreamedText.Append(delta);
                        turn.FirstTextMilliseconds ??= turn.Clock.ElapsedMilliseconds;
                        if (turn.OnText is not null)
                        {
                            try { await turn.OnText(delta).ConfigureAwait(false); }
                            catch (Exception ex) { turn.CallbackError ??= ex; }
                        }
                        continue;
                    }

                    if (string.Equals(eventName, "result", StringComparison.Ordinal) &&
                        root.TryGetProperty("result", out var result))
                    {
                        var cliResult = new CliStreamResult(
                            result.TryGetProperty("status", out var status) ? status.GetString() : null,
                            result.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String
                                ? response.GetString() ?? ""
                                : "",
                            result.TryGetProperty("error", out var error) ? error.GetString() : null,
                            "");
                        PendingTurn? turn;
                        lock (session.Gate)
                        {
                            turn = session.ActiveTurn;
                            if (turn is not null) session.ActiveTurn = null;
                        }
                        if (turn is not null)
                        {
                            cliResult = cliResult with { StreamedText = turn.StreamedText.ToString() };
                            if (turn.CallbackError is not null) turn.Completion.TrySetException(turn.CallbackError);
                            else turn.Completion.TrySetResult(cliResult);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log.Debug("Antigravity CLI emitted an invalid stream event: " + ex.Message);
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            var diagnostic = await ReadStderrAsync(session).ConfigureAwait(false);
            var message = string.IsNullOrWhiteSpace(diagnostic)
                ? "Antigravity CLI session ended unexpectedly."
                : "Antigravity CLI session ended unexpectedly: " + Truncate(diagnostic.Trim(), 1200);
            if (failure is not null) message += " " + failure.Message;
            var error = new ModelException(message, failure);
            session.Ready.TrySetException(error);
            PendingTurn? active;
            lock (session.Gate)
            {
                active = session.ActiveTurn;
                session.ActiveTurn = null;
            }
            active?.Completion.TrySetException(error);
        }
    }

    private async Task<string> ReadStderrAsync(CliSession session)
    {
        try { return await session.StderrTask.ConfigureAwait(false); }
        catch { return ""; }
    }

    private async Task StopSessionAsync(CliSession session)
    {
        if (ReferenceEquals(_session, session)) _session = null;
        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
        }
        catch { }
        try { await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { }
        try { await session.OutputTask.ConfigureAwait(false); } catch { }
        try { session.Process.Dispose(); } catch { }
    }

    private static string BuildContinuationPrompt(ModelRequest request, string serverName)
    {
        var latest = request.Messages.LastOrDefault(message => message.Role == "user" && message.Images is null);
        if (latest is null) throw new ModelException("Jarvis did not provide a user message to Antigravity.");
        return "New user turn for JARVIS. Follow the JARVIS instructions already established in this conversation.\n" +
               ToolUseInstructions(serverName) + "\nUSER: " + latest.Content;
    }

    private static string StableContextKey(string system)
    {
        var lines = system.Split('\n');
        return string.Join('\n', lines.Where(line =>
            !line.TrimStart().StartsWith("- Local time now:", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("- Message tag:", StringComparison.Ordinal)));
    }

    private sealed class CliSession(Process process, string serverName)
    {
        public Process Process { get; } = process;
        public string ServerName { get; } = serverName;
        public object Gate { get; } = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> StderrTask { get; set; } = Task.FromResult("");
        public Task OutputTask { get; set; } = Task.CompletedTask;
        public PendingTurn? ActiveTurn { get; set; }
        public string ContextKey { get; set; } = "";
        public int CompletedTurns { get; set; }
        public int LastMessageCount { get; set; }
    }

    private sealed class PendingTurn(Func<string, Task>? onText, Stopwatch clock)
    {
        public Func<string, Task>? OnText { get; } = onText;
        public Stopwatch Clock { get; } = clock;
        public StringBuilder StreamedText { get; } = new();
        public TaskCompletionSource<CliStreamResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? FirstTextMilliseconds { get; set; }
        public Exception? CallbackError { get; set; }
    }

    private sealed record CliStreamResult(string? Status, string Response, string? Error, string StreamedText);

    private static string ConfiguredModel(string configured) =>
        string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
            ? "gemini-3.8-flash-medium" : configured.Trim();

    private static string ModelEffort(string model)
    {
        foreach (var effort in new[] { "low", "medium", "high", "max" })
            if (model.EndsWith("-" + effort, StringComparison.OrdinalIgnoreCase)) return effort;
        return "medium";
    }

    private static string BuildPrompt(ModelRequest request, string serverName)
    {
        if (request.Messages.Any(m => m.Images is { Count: > 0 }))
            throw new ModelException("The Antigravity CLI accepts text only; image attachments cannot be sent through this Google login session.");

        var header = new StringBuilder()
            .AppendLine(request.System)
            .AppendLine(ToolUseInstructions(serverName))
            .ToString();
        if (header.Length + OmittedTurnsNotice.Length >= MaxPromptCharacters)
            throw new ModelException("Jarvis instructions exceed the Antigravity CLI prompt limit.");

        var renderedMessages = request.Messages.Select(RenderMessage).ToArray();
        var retainedNewestFirst = new List<string>();
        var messageBudget = MaxPromptCharacters - header.Length - OmittedTurnsNotice.Length;
        var used = 0;
        for (var i = renderedMessages.Length - 1; i >= 0; i--)
        {
            if (used + renderedMessages[i].Length > messageBudget)
            {
                if (retainedNewestFirst.Count == 0)
                    throw new ModelException("The latest message exceeds the Antigravity CLI prompt limit.");
                break;
            }
            retainedNewestFirst.Add(renderedMessages[i]);
            used += renderedMessages[i].Length;
        }

        var omittedEarlierTurns = retainedNewestFirst.Count < renderedMessages.Length;
        var prompt = new StringBuilder(header);
        if (omittedEarlierTurns) prompt.AppendLine(OmittedTurnsNotice);
        for (var i = retainedNewestFirst.Count - 1; i >= 0; i--)
            prompt.Append(retainedNewestFirst[i]);
        return prompt.ToString();
    }

    private static string ToolUseInstructions(string serverName) =>
        $"Answer conversational questions directly. When the user asks you to inspect, control, or change their computer, use the Jarvis MCP tools exposed as mcp__{serverName}__<tool>; Jarvis verifies and executes them through its own safety and approval flow. Do not use Antigravity's shell, file, browser, or other external tools. For requested actions, call the relevant Jarvis tool, wait for its result, and report success only when that result confirms it. If a tool fails, is refused, or needs approval, say that accurately instead of pretending the action happened.";

    private static string RenderMessage(ChatMessage message)
    {
        var sb = new StringBuilder();
        if (message.Role == "tool") sb.Append("TOOL RESULT: ");
        else sb.Append(message.Role.ToUpperInvariant()).Append(": ");
        sb.AppendLine(message.Content);
        if (message.ToolCalls is { Count: > 0 })
            foreach (var call in message.ToolCalls)
                sb.Append("Tool call: ").Append(call.Name).Append(' ').AppendLine(call.ArgumentsJson);
        return sb.ToString();
    }

    private static string FindAgyExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var standardInstall = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        if (File.Exists(standardInstall)) return standardInstall;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "agy.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "agy.exe";
    }

    private static string ExplainCliError(string model, string error)
    {
        if (error.Contains("503", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("No capacity available", StringComparison.OrdinalIgnoreCase))
            return $"Google Antigravity currently has no server capacity for {model} (503 UNAVAILABLE). Jarvis did not switch models; try the same model again shortly.";
        if (error.Contains("model is not available on this account", StringComparison.OrdinalIgnoreCase))
            return $"Google Antigravity says {model} is not available on the signed-in account. Jarvis did not switch models. Check the account's model access in Antigravity.";
        return error;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";

    public void ResetConversation()
    {
        _turnGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } session) StopSessionAsync(session).GetAwaiter().GetResult();
        }
        finally { _turnGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _turnGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_session is { } session) StopSessionAsync(session).GetAwaiter().GetResult();
        }
        finally
        {
            _turnGate.Release();
            _turnGate.Dispose();
        }
    }
}

/// <summary>
/// Works with every OpenAI-compatible endpoint: DeepSeek, OpenAI, OpenRouter, Groq, Ollama,
/// LM Studio, vLLM, and anything else that speaks /chat/completions.
/// </summary>
public sealed class OpenAiCompatibleModel : ILanguageModel
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AppSettings _settings;

    public OpenAiCompatibleModel(AppSettings settings) => _settings = settings;

    public string DisplayName => $"{_settings.Model} via {new Uri(_settings.BaseUrl).Host}";

    public async Task<ModelReply> StreamAsync(ModelRequest request, Func<string, Task>? onText, CancellationToken ct)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var isGeminiApi = Uri.TryCreate(_settings.BaseUrl, UriKind.Absolute, out var endpoint) &&
                          endpoint.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
        var isGemini4Argon = isGeminiApi &&
                             _settings.Model.Equals("gemini-4-argon", StringComparison.OrdinalIgnoreCase);
        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = Messages(request),
            // Argon's reasoning and answer share the output limit. Google's compatibility docs
            // recommend at least 32K for it; this is a cap, not a reservation or fixed bill.
            ["max_tokens"] = isGemini4Argon ? Math.Max(request.MaxTokens, 32_768) : request.MaxTokens,
            ["stream"] = true
        };
        if (!isGemini4Argon)
        {
            payload["temperature"] = request.Temperature;
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.ToOpenAiSchema();
            payload["tool_choice"] = "auto";
        }

        using var response = await SendAsync(url, payload, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = "";
            try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

            // Gemini's thinking models refuse a custom temperature, and some endpoints reject
            // stream_options. Retry once with the portable subset before reporting the error.
            if (body.Contains("temperature", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
            {
                payload.Remove("temperature");
                payload.Remove("stream_options");
                using var retry = await SendAsync(url, payload, ct).ConfigureAwait(false);
                if (!retry.IsSuccessStatusCode)
                {
                    var retryBody = "";
                    try { retryBody = await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                throw Failure(retry.StatusCode, retryBody, isGeminiApi);
                }
                return await ReadStreamAsync(retry, onText, ct).ConfigureAwait(false);
            }
            throw Failure(response.StatusCode, body, isGeminiApi);
        }

        return await ReadStreamAsync(response, onText, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, JsonObject payload, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        var key = _settings.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static async Task<ModelReply> ReadStreamAsync(
        HttpResponseMessage response, Func<string, Task>? onText, CancellationToken ct)
    {
        var reply = new ModelReply();
        var partials = new Dictionary<int, (string id, StringBuilder name, StringBuilder args, string thoughtSignature)>();
        var text = new StringBuilder();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }
            if (node is not JsonObject obj) continue;

            if (obj["error"] is JsonObject error)
                throw new ModelException(error["message"]?.GetValue<string>() ?? "The provider reported a streaming error.");

            if (obj["usage"] is JsonObject usage)
            {
                reply.InputTokens = usage["prompt_tokens"]?.GetValue<int>() ?? reply.InputTokens;
                reply.OutputTokens = usage["completion_tokens"]?.GetValue<int>() ?? reply.OutputTokens;
            }

            if (obj["choices"] is not JsonArray choices || choices.Count == 0) continue;
            if (choices[0] is not JsonObject choice) continue;

            if (choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var reason))
                reply.StopReason = reason;

            if (choice["delta"] is not JsonObject delta) continue;

            if (delta["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var deltaText) &&
                !string.IsNullOrEmpty(deltaText))
            {
                text.Append(deltaText);
                if (onText is not null) await onText(deltaText).ConfigureAwait(false);
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var entry in toolCalls)
                {
                    if (entry is not JsonObject call) continue;
                    var index = call["index"]?.GetValue<int>() ?? partials.Count;
                    if (!partials.TryGetValue(index, out var partial))
                    {
                        partial = (call["id"]?.GetValue<string>() ?? $"call_{index}",
                            new StringBuilder(), new StringBuilder(), "");
                        partials[index] = partial;
                    }
                    if (call["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                        partial.id = id;
                    if (call["function"] is JsonObject fn)
                    {
                        if (fn["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
                            partial.name.Append(name);
                        if (fn["arguments"] is JsonValue argValue && argValue.TryGetValue<string>(out var args))
                            partial.args.Append(args);
                    }
                    if (call["extra_content"] is JsonObject extra && extra["google"] is JsonObject google)
                    {
                        var signature = google["thought_signature"]?.GetValue<string>() ??
                                        google["thoughtSignature"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(signature)) partial.thoughtSignature = signature;
                    }
                    partials[index] = partial;
                }
            }
        }

        reply.Text = text.ToString();
        foreach (var partial in partials.OrderBy(p => p.Key))
        {
            reply.ToolCalls.Add(new ToolCall(partial.Value.id, partial.Value.name.ToString(),
                partial.Value.args.ToString() is { Length: > 0 } a ? a : "{}", partial.Value.thoughtSignature));
        }
        if (reply.StopReason.Length == 0)
            reply.StopReason = reply.HasToolCalls ? "tool_calls" : "stop";
        if (!reply.HasToolCalls && string.IsNullOrWhiteSpace(reply.Text))
            throw new ModelException("The provider ended the response stream without returning text or a tool call.");
        return reply;
    }

    private JsonArray Messages(ModelRequest request)
    {
        var arr = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
            arr.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "assistant":
                {
                    var msg = new JsonObject { ["role"] = "assistant" };
                    msg["content"] = m.Content.Length > 0 ? m.Content : null;
                    if (m.ToolCalls is { Count: > 0 })
                    {
                        var calls = new JsonArray();
                        foreach (var c in m.ToolCalls)
                        {
                            var call = new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson }
                            };
                            if (!string.IsNullOrEmpty(c.ThoughtSignature))
                                call["extra_content"] = new JsonObject
                                {
                                    ["google"] = new JsonObject { ["thought_signature"] = c.ThoughtSignature }
                                };
                            calls.Add(call);
                        }
                        msg["tool_calls"] = calls;
                    }
                    arr.Add(msg);
                    break;
                }
                case "tool":
                    arr.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = m.ToolCallId ?? "",
                        ["content"] = m.Content
                    });
                    break;
                default:
                {
                    if (m.Images is { Count: > 0 })
                    {
                        var parts = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = m.Content }
                        };
                        foreach (var img in m.Images)
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = $"data:{img.Mime};base64,{img.Base64}" }
                            });
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    else
                    {
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content });
                    }
                    break;
                }
            }
        }
        return arr;
    }

    internal static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
        throw Failure(response.StatusCode, body);
    }

    private static ModelException Failure(HttpStatusCode status, string body, bool isGeminiApi = false)
    {
        if (body.Length > 600) body = body[..600];
        var hint = status switch
        {
            HttpStatusCode.Unauthorized when isGeminiApi =>
                "The Gemini API key was rejected. Check the AI Studio key and its API restrictions in Settings.",
            HttpStatusCode.Unauthorized => "The API key was rejected. Check it in Settings.",
            HttpStatusCode.Forbidden when isGeminiApi =>
                "This Gemini API project does not have access to that model. Check its AI Studio API tier or choose an available model.",
            HttpStatusCode.Forbidden => "This key is not allowed to use that model.",
            HttpStatusCode.NotFound => "That endpoint or model does not exist. Check the base URL and model name.",
            HttpStatusCode.TooManyRequests => "Rate limited by the provider. Try again in a moment.",
            _ when (int)status >= 500 => "The provider had a server error. Try again shortly.",
            _ => $"The provider returned {(int)status}."
        };
        return new ModelException($"{hint} {body}".Trim());
    }
}

public sealed class AnthropicModel : ILanguageModel
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AppSettings _settings;

    public AnthropicModel(AppSettings settings) => _settings = settings;

    public string DisplayName => $"{_settings.Model} via Anthropic";

    public async Task<ModelReply> StreamAsync(ModelRequest request, Func<string, Task>? onText, CancellationToken ct)
    {
        var url = _settings.BaseUrl.TrimEnd('/') + "/v1/messages";
        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = Math.Clamp(request.Temperature, 0, 1),
            ["stream"] = true,
            ["system"] = request.System,
            ["messages"] = Messages(request)
        };
        if (request.Tools.Count > 0) payload["tools"] = request.Tools.ToAnthropicSchema();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-api-key", _settings.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await Http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await OpenAiCompatibleModel.EnsureSuccess(response, ct).ConfigureAwait(false);

        var reply = new ModelReply();
        var text = new StringBuilder();
        var blocks = new Dictionary<int, (string type, string id, string name, StringBuilder json)>();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;

            JsonNode? node;
            try { node = JsonNode.Parse(data); }
            catch { continue; }
            if (node is not JsonObject obj) continue;

            var type = obj["type"]?.GetValue<string>();
            switch (type)
            {
                case "message_start":
                    if (obj["message"]?["usage"] is JsonObject startUsage)
                        reply.InputTokens = startUsage["input_tokens"]?.GetValue<int>() ?? 0;
                    break;
                case "content_block_start":
                {
                    var index = obj["index"]?.GetValue<int>() ?? 0;
                    if (obj["content_block"] is JsonObject block)
                    {
                        var blockType = block["type"]?.GetValue<string>() ?? "";
                        if (blockType == "tool_use")
                            blocks[index] = ("tool_use", block["id"]?.GetValue<string>() ?? $"tool_{index}",
                                block["name"]?.GetValue<string>() ?? "", new StringBuilder());
                        else
                            blocks[index] = ("text", "", "", new StringBuilder());
                    }
                    break;
                }
                case "content_block_delta":
                {
                    var index = obj["index"]?.GetValue<int>() ?? 0;
                    if (obj["delta"] is not JsonObject delta) break;
                    var deltaType = delta["type"]?.GetValue<string>();
                    if (deltaType == "text_delta")
                    {
                        var chunk = delta["text"]?.GetValue<string>() ?? "";
                        if (chunk.Length > 0)
                        {
                            text.Append(chunk);
                            if (onText is not null) await onText(chunk).ConfigureAwait(false);
                        }
                    }
                    else if (deltaType == "input_json_delta" && blocks.TryGetValue(index, out var block))
                    {
                        block.json.Append(delta["partial_json"]?.GetValue<string>() ?? "");
                    }
                    break;
                }
                case "message_delta":
                    if (obj["delta"]?["stop_reason"] is JsonValue stop && stop.TryGetValue<string>(out var reason))
                        reply.StopReason = reason;
                    if (obj["usage"] is JsonObject usage)
                        reply.OutputTokens = usage["output_tokens"]?.GetValue<int>() ?? reply.OutputTokens;
                    break;
                case "error":
                    throw new ModelException(obj["error"]?["message"]?.GetValue<string>() ?? "The provider reported an error.");
            }
        }

        reply.Text = text.ToString();
        foreach (var block in blocks.OrderBy(b => b.Key).Where(b => b.Value.type == "tool_use"))
        {
            var json = block.Value.json.ToString();
            reply.ToolCalls.Add(new ToolCall(block.Value.id, block.Value.name,
                json.Length > 0 ? json : "{}"));
        }
        if (reply.StopReason.Length == 0)
            reply.StopReason = reply.HasToolCalls ? "tool_use" : "end_turn";
        if (!reply.HasToolCalls && string.IsNullOrWhiteSpace(reply.Text))
            throw new ModelException("Anthropic ended the response stream without returning text or a tool call.");
        return reply;
    }

    private JsonArray Messages(ModelRequest request)
    {
        var arr = new JsonArray();
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case "assistant":
                {
                    var content = new JsonArray();
                    if (m.Content.Length > 0)
                        content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                    if (m.ToolCalls is { Count: > 0 })
                    {
                        foreach (var c in m.ToolCalls)
                        {
                            JsonNode input;
                            try { input = JsonNode.Parse(c.ArgumentsJson) ?? new JsonObject(); }
                            catch { input = new JsonObject(); }
                            content.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = c.Id,
                                ["name"] = c.Name,
                                ["input"] = input
                            });
                        }
                    }
                    if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = "(no content)" });
                    arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }
                case "tool":
                {
                    var content = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = m.Content }
                    };
                    arr.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = m.ToolCallId ?? "",
                                ["content"] = content
                            }
                        }
                    });
                    break;
                }
                default:
                {
                    if (m.Images is { Count: > 0 })
                    {
                        var content = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = m.Content }
                        };
                        foreach (var img in m.Images)
                            content.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = img.Mime,
                                    ["data"] = img.Base64
                                }
                            });
                        arr.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    }
                    else
                    {
                        arr.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } }
                        });
                    }
                    break;
                }
            }
        }
        return arr;
    }
}
