using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

/// <summary>
/// One conversation with one model. Streams sentences to the voice as they arrive, runs tools through the
/// safety gate, and feeds images back to vision models.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly JarvisServices _services;
    private readonly PresenceTracker _presence;
    private readonly List<ChatMessage> _messages = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _messagesGate = new();
    private readonly object _activeTurnGate = new();
    private readonly Func<string, Task> _onSentence;
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalOutcome>> _requestApproval;
    private ILanguageModel? _model;
    private CancellationTokenSource? _activeTurnCancellation;
    private int _turnActive;

    public AgentSession(JarvisServices services, ILanguageModel? model, PresenceTracker presence,
        Func<string, Task> onSentence, string workerName = "",
        Func<ApprovalRequest, CancellationToken, Task<ApprovalOutcome>>? approvals = null)
    {
        _services = services;
        _model = model;
        _presence = presence;
        _onSentence = onSentence;
        WorkerName = workerName;
        _requestApproval = approvals ?? ((request, token) => _services.Approvals.RequestAsync(request, token));
    }

    public string WorkerName { get; }
    public int Turns { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public bool InTurn => Volatile.Read(ref _turnActive) != 0;
    public int MessageCount { get { lock (_messagesGate) return _messages.Count; } }

    public IReadOnlyList<ChatMessage> Transcript
    {
        get { lock (_messagesGate) return _messages.ToArray(); }
    }

    public void SetModel(ILanguageModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        var previous = _model;
        _model = model;
        if (previous is IDisposable disposable) disposable.Dispose();
    }

    public void Reset()
    {
        lock (_messagesGate) _messages.Clear();
        Turns = 0;
        if (_model is IConversationResettable resettable) resettable.ResetConversation();
    }

    public void Dispose()
    {
        if (_model is IDisposable disposable) disposable.Dispose();
        _model = null;
    }

    /// <summary>Wait until the current model turn has released the transcript.</summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        _turnGate.Release();
    }

    public async Task<string> AskAsync(string userText, CancellationToken ct, Action? onFirstText = null)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_activeTurnGate) _activeTurnCancellation = turnCancellation;
        ct = turnCancellation.Token;
        Volatile.Write(ref _turnActive, 1);
        try
        {
            var model = _model ?? throw new ModelException("No model is configured.");
            var tag = _presence.TimeTag();
            AddMessage(ChatMessage.User(tag + userText));
            _services.Events.Emit("user", userText, true, ("worker", WorkerName), ("turns", Turns));

            var finalText = new StringBuilder();
            var firstTextNotified = false;
            void SignalFirstText()
            {
                if (firstTextNotified) return;
                firstTextNotified = true;
                try { onFirstText?.Invoke(); }
                catch (Exception ex) { Log.Debug("First-response notification failed: " + ex.Message); }
            }

            var maxToolCalls = Math.Clamp(_services.Settings.MaxToolSteps, 1, 64);
            var maxModelSteps = maxToolCalls + 2; // Leave room for the model to report after its last tool call.
            var toolCallsExecuted = 0;
            for (var step = 0; step < maxModelSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                TrimContext();

                var system = PromptBuilder.Build(_services.Settings, _services.Memory, _services.Tools, tag.Trim());
                var request = new ModelRequest(system, SnapshotMessages(), _services.Tools,
                    _services.Settings.Temperature, _services.Settings.MaxTokens);

                var splitter = new SentenceSplitter();
                var stopwatch = Stopwatch.StartNew();

                ModelReply reply;
                try
                {
                    reply = await model.StreamAsync(request, async chunk =>
                    {
                        if (!string.IsNullOrWhiteSpace(chunk)) SignalFirstText();
                        foreach (var sentence in splitter.Feed(chunk))
                            await _onSentence(sentence).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    foreach (var sentence in splitter.Flush())
                        await _onSentence(sentence).ConfigureAwait(false);
                    Log.Warn("model call failed", ex);
                    throw ex is ModelException modelError ? modelError : new ModelException(ex.Message, ex);
                }

                stopwatch.Stop();
                if (!string.IsNullOrWhiteSpace(reply.Text)) SignalFirstText();
                foreach (var sentence in splitter.Flush())
                    await _onSentence(sentence).ConfigureAwait(false);

                TotalInputTokens += reply.InputTokens;
                TotalOutputTokens += reply.OutputTokens;

                if (reply.HasToolCalls)
                {
                    AddMessage(ChatMessage.Assistant(reply.Text, reply.ToolCalls));
                    _services.Events.Emit("assistant_step", reply.Text, true,
                        ("steps", reply.ToolCalls.Count), ("ms", stopwatch.ElapsedMilliseconds));

                    foreach (var call in reply.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        ToolResult toolResult;
                        if (toolCallsExecuted >= maxToolCalls)
                        {
                            toolResult = ToolResult.Fail("The tool-call budget is exhausted. No action was run.");
                        }
                        else
                        {
                            toolCallsExecuted++;
                            toolResult = await ExecuteToolAsync(call, step, ct).ConfigureAwait(false);
                        }
                        AddMessage(ChatMessage.Tool(call.Id, toolResult.Content));

                        if (toolResult.ImageBase64 is not null)
                        {
                            AddMessage(ChatMessage.User(
                                "That screenshot is attached. Its pixel coordinates are the ones to use for " +
                                "click_at and annotate.",
                                new List<ImageAttachment>
                                {
                                    new(toolResult.ImageBase64, toolResult.ImageMime ?? "image/jpeg")
                                }));
                        }
                    }
                    continue;
                }

                AddMessage(ChatMessage.Assistant(reply.Text));
                Turns++;
                finalText.Append(reply.Text);
                _services.Events.Emit("turn_end", reply.Text, true,
                    ("ms", stopwatch.ElapsedMilliseconds), ("step", step),
                    ("tokens_in", reply.InputTokens), ("tokens_out", reply.OutputTokens),
                    ("worker", WorkerName));
                return reply.Text;
            }

            const string budget = "I have reached my tool-call limit for this request, sir. Shall I continue?";
            _services.Events.Emit("turn_end", budget, true, ("step", "limit"));
            await _onSentence(budget).ConfigureAwait(false);
            return finalText.Length > 0 ? finalText.ToString() : budget;
        }
        finally
        {
            Volatile.Write(ref _turnActive, 0);
            lock (_activeTurnGate)
            {
                if (ReferenceEquals(_activeTurnCancellation, turnCancellation))
                    _activeTurnCancellation = null;
            }
            try { turnCancellation.Cancel(); } catch { }
            _turnGate.Release();
        }
    }

    internal async Task<ToolResult> ExecuteToolFromMcpAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        CancellationToken turnToken;
        lock (_activeTurnGate)
        {
            if (!InTurn || _activeTurnCancellation is null)
                return ToolResult.Fail("Jarvis is not processing an active user request. No action was run.");
            turnToken = _activeTurnCancellation.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, turnToken);
        var json = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        var call = new ToolCall("mcp-" + Guid.NewGuid().ToString("N"), name, json);
        return await ExecuteToolAsync(call, Interlocked.Increment(ref _externalToolStep), linked.Token)
            .ConfigureAwait(false);
    }

    private int _externalToolStep;

    private async Task<ToolResult> ExecuteToolAsync(ToolCall call, int step, CancellationToken ct)
    {
        var tool = _services.Tools.Get(call.Name);
        if (tool is null)
            return ToolResult.Fail($"There is no tool called '{call.Name}'.");

        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch
        {
            return ToolResult.Fail("Those tool arguments were not valid JSON.");
        }

        _services.Events.Emit("tool_call", tool.Name, true,
            ("input", Truncate(call.ArgumentsJson, 1200)), ("step", step), ("worker", WorkerName));

        var decision = _services.Policy.Evaluate(tool, args);
        if (decision.Level == RiskLevel.Deny)
        {
            _services.Events.Emit("tool_denied", tool.Name, true, ("reason", decision.Reason));
            return ToolResult.Fail("Refused: " + decision.Reason);
        }

        if (decision.Level == RiskLevel.Confirm)
        {
            var question = $"Shall I {tool.DescribeCall(args)}?";
            var request = new ApprovalRequest(tool.Name, question, Truncate(call.ArgumentsJson, 300),
                decision.RuleKey ?? tool.Name, decision.Level);
            var outcome = await _requestApproval(request, ct).ConfigureAwait(false);
            if (outcome is ApprovalOutcome.Denied or ApprovalOutcome.TimedOut)
            {
                _services.Events.Emit("tool_denied", tool.Name, true, ("reason", outcome.ToString()));
                return ToolResult.Fail("The user said no, or did not answer. Do not retry; say so briefly.");
            }
        }

        // Approval can take long enough for a symlink, policy setting, or target to change.
        // Re-evaluate immediately before dispatch and never carry an approval across a changed
        // risk decision.
        var currentDecision = _services.Policy.Evaluate(tool, args);
        if (currentDecision.Level == RiskLevel.Deny)
        {
            _services.Events.Emit("tool_denied", tool.Name, true,
                ("reason", "policy changed before execution"));
            return ToolResult.Fail("Refused: the action became disallowed while waiting for approval. No action was run.");
        }
        if (currentDecision.Level == RiskLevel.Confirm &&
            (decision.Level != RiskLevel.Confirm ||
             !string.Equals(decision.RuleKey, currentDecision.RuleKey, StringComparison.Ordinal)))
        {
            _services.Events.Emit("tool_denied", tool.Name, true,
                ("reason", "approval target or risk changed before execution"));
            return ToolResult.Fail("The action target or risk changed while waiting for approval. No action was run; please ask again.");
        }

        var context = new ToolContext
        {
            Services = _services,
            Worker = WorkerName,
            Cancellation = ct,
            ApprovedTargetPath = currentDecision.TargetPath
        };
        var stopwatch = Stopwatch.StartNew();
        ToolResult result;
        var serializeDesktopAction = tool.Category is "desktop" or "screen" || tool.Name == "media";
        var desktopActions = _services.DesktopActions;
        if (serializeDesktopAction)
            await desktopActions.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await tool.Handler(context, args).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"tool {tool.Name} failed", ex);
            result = ToolResult.Fail($"The tool failed: {ex.Message}");
        }
        finally
        {
            if (serializeDesktopAction)
            {
                if (!tool.ReadOnly) desktopActions.RecordChange();
                desktopActions.Gate.Release();
            }
        }
        stopwatch.Stop();

        _services.Events.Emit("tool_result", tool.Name, true,
            ("ok", result.Success), ("preview", Truncate(result.Content, 600)),
            ("ms", stopwatch.ElapsedMilliseconds), ("worker", WorkerName));

        var content = Truncate(result.Content, 14000);
        if (!result.Success) content = "FAILED: " + content;
        if (result.ImageBase64 is not null) content += "\n[image attached]";
        return new ToolResult(result.Success, content, result.ImageBase64, result.ImageMime);
    }

    private void TrimContext()
    {
        const int budget = 26000;
        int count;
        lock (_messagesGate)
        {
            var tokens = _messages.Sum(m => m.ApproxTokens);
            if (tokens <= budget) return;

            // Never cut in the middle of a tool exchange: drop whole turns and resume at a real user message.
            var keepFrom = _messages.FindLastIndex(m => m.Role == "user" && m.Images is null);
            if (keepFrom <= 0)
            {
                count = -_messages.Count;
            }
            else
            {
                _messages.RemoveRange(0, keepFrom);
                if (_messages.Count == 0 || !_messages[0].Content.StartsWith("[earlier", StringComparison.Ordinal))
                    _messages.Insert(0, ChatMessage.User("[earlier turns were trimmed to fit the context window]"));
                count = _messages.Count;
            }
        }

        if (count < 0)
            _services.Events.Emit("context_trim_skipped", null, false, ("messages", -count));
        else
            _services.Events.Emit("context_trimmed", null, false, ("messages", count));
    }

    private void AddMessage(ChatMessage message)
    {
        lock (_messagesGate) _messages.Add(message);
    }

    private ChatMessage[] SnapshotMessages()
    {
        lock (_messagesGate) return _messages.ToArray();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
