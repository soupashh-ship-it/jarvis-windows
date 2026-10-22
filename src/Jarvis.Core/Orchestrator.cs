using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

public enum AssistantState { Idle, Listening, Thinking, Speaking, Waiting, Paused, Error }

/// <summary>
/// The conductor. Owns the state machine, the voice loop, approvals, timers, workers and session
/// lifecycle. The UI only ever talks to this class.
/// </summary>
public sealed class Orchestrator : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly EventBus _events;
    private readonly MemoryStore _memory;
    private readonly PresenceTracker _presence;
    private readonly TtsService _tts;
    private readonly SpeechInput _ears;
    private readonly AgentSession _session;
    private readonly IntentEngine _intent;
    private readonly WorkerManager _workers;
    private readonly ToolRegistry _tools;
    private readonly JarvisMcpServer _mcpServer;
    private readonly PolicyEngine _policy;
    private readonly ApprovalService _approvals;
    private readonly TimerService _timers;
    private readonly ScreenshotState _screenshot;
    private readonly JarvisServices _services;
    private ILanguageModel? _sessionModel;
    private string _sessionModelKey;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sessionLifecycleGate = new(1, 1);
    private System.Threading.Timer? _idleTimer;
    private System.Threading.Timer? _voiceSync;
    private CancellationTokenSource? _turnCts;
    private AssistantState _state = AssistantState.Idle;
    private string _stepText = "";
    private string _userText = "";
    private string _replyText = "";
    private string _pendingQuestion = "";
    private string _partial = "";
    private DateTime _lastActivity = DateTime.UtcNow;
    private int _idleResetScheduled;
    private bool _paused;
    private int _followUpDepth;
    private readonly object _turnGate = new();
    private IDisposable? _eventSubscription;

    public Orchestrator(AppSettings settings, EventBus events, MemoryStore memory)
    {
        _settings = settings;
        _events = events;
        _memory = memory;
        _presence = new PresenceTracker(Paths.Root);

        _screenshot = new ScreenshotState();
        _approvals = new ApprovalService();
        _timers = new TimerService();
        _policy = new PolicyEngine(settings);
        _tools = new ToolRegistry()
            .AddRange(AppTools.Create())
            .AddRange(DesktopTools.Create())
            .AddRange(SystemTools.Create())
            .AddRange(FileShellTools.Create())
            .AddRange(MemoryTools.Create())
            .AddRange(WorkerTools.Create());

        _services = new JarvisServices
        {
            Settings = settings,
            Events = events,
            Memory = memory,
            Screenshot = _screenshot,
            Approvals = _approvals,
            Timers = _timers,
            Policy = _policy,
            Tools = _tools,
            DataDirectory = Paths.Root
        };

        _workers = new WorkerManager(_services, _presence);
        _services.Workers = _workers;
        _services.Speak = text => { Speak(text); return Task.CompletedTask; };
        _services.StopSpeaking = () => { _tts!.Stop(); return Task.CompletedTask; };
        _services.ListenOnce = async (prompt, ct) =>
        {
            if (!string.IsNullOrWhiteSpace(prompt)) await UnpromptedAsync(prompt).ConfigureAwait(false);
            return await _ears!.CaptureAsync(TimeSpan.FromSeconds(settings.WaitForSpeechSeconds), ct).ConfigureAwait(false);
        };

        _tts = new TtsService(settings) { Enabled = settings.SpeakReplies };
        _ears = new SpeechInput(settings);
        _sessionModelKey = ModelSettingsKey(settings);
        _sessionModel = ModelFactory.Create(settings);
        _session = new AgentSession(_services, _sessionModel, _presence, SpeakSentenceAsync);
        _mcpServer = new JarvisMcpServer(_tools, _events,
            (name, arguments, ct) => _session.ExecuteToolFromMcpAsync(name, arguments, ct));
        _intent = new IntentEngine(_services);

        _approvals.Handler = HandleApprovalAsync;
        _workers.OnFinished = OnWorkerFinishedAsync;
        _timers.Fired += OnTimerFired;
        _ears.WakeDetected += OnWakeWord;
        _ears.SpeechStarted += OnSpeechDetected;
        _ears.Hypothesis += text =>
        {
            _partial = text;
            PartialText?.Invoke(text);
        };
        _ears.StatusChanged += status => _events.Emit("voice_status", status, false);
        _tts.SentenceStarted += sentence =>
        {
            _replyText = sentence;
            _events.Emit("speaking_now", sentence, false);
        };
        // While JARVIS is speaking, demand a more confident wake word so it does not wake itself through speakers.
        _voiceSync = new System.Threading.Timer(_ =>
        {
            try { _ears.SpeakingHint = _tts.IsSpeaking; } catch { }
        }, null, 200, 200);
        _eventSubscription = _events.Subscribe(OnEvent);
    }

    // ---------- public surface for the UI ----------

    public AssistantState State => _state;
    public bool Paused => _paused;
    public bool VoiceAvailable => _ears.Available;
    public string VoiceStatus => _ears.Status;
    public string StepText => _stepText;
    public string UserText => _userText;
    public string ReplyText => _replyText;
    public string PendingQuestion => _pendingQuestion;
    public string LiveTranscript => _partial;
    public float MicLevel => _ears.Level;
    public float OutputLevel => _tts.Level;
    public int SessionTurns => _session.Turns;
    public int SessionMessages => _session.MessageCount;
    public DateTimeOffset SessionStarted => _session.StartedAt;
    public string ModelName => _settings.HasModel ? _settings.Model : "local mode";
    public string TtsEngine => _tts.LastEngine;

    public JarvisServices Services => _services;
    public WorkerManager Workers => _workers;
    public TtsService Tts => _tts;
    public SpeechInput Ears => _ears;
    public AgentSession Session => _session;
    public ApprovalService Approvals => _approvals;
    public ToolRegistry Tools => _tools;
    public PolicyEngine Policy => _policy;

    public event Action<AssistantState>? StateChanged;
    public event Action<string>? PartialText;
    public event Action? Changed;

    public string Activity
    {
        get
        {
            if (_paused) return "paused";
            if (_state == AssistantState.Waiting) return "waiting";
            if (_state == AssistantState.Listening) return "listening";
            if (_tts.IsSpeaking) return "speaking";
            if (_state == AssistantState.Thinking) return "thinking";
            if (_workers.All.Any(w => w.IsLive)) return "background";
            return "idle";
        }
    }

    public async Task StartAsync()
    {
        _memory.SeedNotesIfMissing();
        _events.Emit("started", $"JARVIS online, {_settings.Honorific}.", true,
            ("voice", _settings.Tts.ToString()), ("model", ModelName), ("tools", _tools.Count));

        var mcpReady = true;
        if (_sessionModel is AntigravityCliModel)
        {
            try { await EnsureMcpToolBridgeAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                mcpReady = false;
                Log.Warn("Jarvis MCP tool bridge could not start", ex);
                _events.Emit("tool_bridge_error", ex.Message, true);
            }
        }

        try { _ears.Start(); }
        catch (Exception ex) { Log.Warn("voice input failed to start", ex); }

        // Bring up the authenticated headless CLI while JARVIS is idle so its process startup does
        // not sit on the user's first response. This only opens the local session; it sends no prompt.
        if (mcpReady && _sessionModel is AntigravityCliModel antigravity)
            _ = PrepareAntigravityAsync(antigravity);

        if (_settings.GreetOnStart)
        {
            try { await Task.Delay(350, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (_lifetime.IsCancellationRequested) return;
            Speak($"Online and ready, {_settings.Honorific}.");
        }

        if (_lifetime.IsCancellationRequested) return;
        _idleTimer = new System.Threading.Timer(_ => CheckIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        await Task.CompletedTask;
    }

    private async Task PrepareAntigravityAsync(AntigravityCliModel model)
    {
        try
        {
            await model.PrepareAsync(_lifetime.Token).ConfigureAwait(false);
            Log.Info("Antigravity CLI session is ready.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Keep startup unaffected; the first request can retry and report a useful error.
            Log.Debug("Antigravity CLI warm-up deferred: " + ex.Message);
        }
    }

    private async Task EnsureMcpToolBridgeAsync(CancellationToken ct)
    {
        if (_mcpServer.IsRunning) return;
        await _mcpServer.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        _idleTimer?.Dispose();
        _turnCts?.Cancel();
        _workers.StopAll();
        _tts.Stop();
        try { await _mcpServer.StopAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Debug("MCP tool bridge shutdown failed: " + ex.Message); }
        await Task.CompletedTask;
    }

    /// <summary>Wake by hotkey, tray or the Talk button.</summary>
    public async Task TalkAsync()
    {
        if (_paused) { Speak("I'm paused, " + _settings.Honorific + "."); return; }
        if (_state == AssistantState.Thinking)
        {
            _events.Emit("barge_in", "hotkey", false);
            await StopAsync().ConfigureAwait(false);
        }
        if (_state == AssistantState.Listening) return;
        await ListenAsync().ConfigureAwait(false);
    }

    public async Task<string?> ListenAsync(bool playChime = true)
    {
        if (!_ears.Available)
        {
            Speak("Voice input is unavailable, " + _settings.Honorific + ". Type to me in the deck instead.");
            return null;
        }

        CancellationTokenSource cts;
        await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_paused) return null;
            cts = ReplaceTurnCancellation();
            _lastActivity = DateTime.UtcNow;
            SetState(AssistantState.Listening);
        }
        finally { _sessionLifecycleGate.Release(); }
        _events.Emit("listen_start", null, false);
        _partial = "";
        if (playChime)
        {
            _tts.Chime();
            // Let playback finish before arming VAD. Otherwise the prompt chime can look like the
            // first speech frame and complete an empty recording before the user starts talking.
            await _tts.WaitIdleAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        _ears.SpeakingHint = false;

        string? text;
        try
        {
            text = await _ears.CaptureAsync(TimeSpan.FromSeconds(_settings.WaitForSpeechSeconds), cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { text = null; }

        if (string.IsNullOrWhiteSpace(text))
        {
            _events.Emit("heard", "", true, ("empty", true));
            SetState(AssistantState.Idle);
            return null;
        }

        _events.Emit("heard", text!, true, ("source", "voice"));
        await SubmitAsync(text!, "voice", cts.Token).ConfigureAwait(false);
        return text;
    }

    public async Task SubmitAsync(string text, string source = "typed", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_paused) { Speak("I'm paused, " + _settings.Honorific + "."); return; }

        CancellationTokenSource cts;
        await _sessionLifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_paused) { Speak("I'm paused, " + _settings.Honorific + "."); return; }
            cts = ReplaceTurnCancellation(ct);
            _lastActivity = DateTime.UtcNow;
            _userText = text;
            _partial = "";
            _stepText = "";
            _replyText = "";
            SetState(AssistantState.Thinking);
        }
        finally { _sessionLifecycleGate.Release(); }
        var token = cts.Token;

        _events.Emit("turn_start", text, true, ("source", source));

        var interrupted = false;
        var turnSucceeded = false;
        try
        {
            var modelKey = ModelSettingsKey(_settings);
            if (!string.Equals(modelKey, _sessionModelKey, StringComparison.Ordinal))
            {
                await _session.WaitForIdleAsync(token).ConfigureAwait(false);
                _sessionModel = ModelFactory.Create(_settings);
                _session.SetModel(_sessionModel);
                _sessionModelKey = modelKey;
            }
            var model = _sessionModel;

            if (model is null)
            {
                var answer = await _intent.TryHandleAsync(text, token).ConfigureAwait(false);
                answer ??= "I don't have a model configured yet, " + _settings.Honorific +
                           ". Open Settings and add an API key, and I shall be considerably more useful.";
                Speak(answer);
                await _tts.WaitIdleAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
            }
            else
            {
                if (model is AntigravityCliModel)
                {
                    await EnsureMcpToolBridgeAsync(token).ConfigureAwait(false);
                    var firstText = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var acknowledgement = SpeakDelayedAcknowledgementAsync(firstText.Task, token);
                    try
                    {
                        await _session.AskAsync(text, token, () => firstText.TrySetResult(true)).ConfigureAwait(false);
                    }
                    finally
                    {
                        firstText.TrySetResult(true);
                        await acknowledgement.ConfigureAwait(false);
                    }
                }
                else
                {
                    await _session.AskAsync(text, token).ConfigureAwait(false);
                }
                await _tts.WaitIdleAsync(TimeSpan.FromSeconds(180)).ConfigureAwait(false);
            }
            turnSucceeded = true;
            lock (_turnGate) _lastActivity = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            _events.Emit("turn_cancelled", text, true);
            SetState(AssistantState.Idle);
        }
        catch (ModelException ex)
        {
            _events.Emit("error", ex.Message, true, ("where", "model"));
            SetState(AssistantState.Idle);
            Speak("I'm afraid the model failed: " + Trim(ex.Message, 300));
        }
        catch (Exception ex)
        {
            Log.Error("turn failed", ex);
            _events.Emit("error", ex.Message, true, ("where", "turn"));
            SetState(AssistantState.Idle);
            Speak("I'm afraid something went wrong on my end, " + _settings.Honorific + ".");
        }
        finally
        {
            if (_state is AssistantState.Thinking or AssistantState.Speaking)
                SetState(AssistantState.Idle);
        }

        if (interrupted || _paused || !turnSucceeded) return;
        if (source == "voice" && _settings.FollowUpSeconds > 0 && !token.IsCancellationRequested)
            await FollowUpAsync().ConfigureAwait(false);
    }

    private async Task FollowUpAsync()
    {
        if (_followUpDepth >= 2 || !_ears.Available || _state != AssistantState.Idle) return;
        _followUpDepth++;
        try
        {
            CancellationTokenSource cts;
            await _sessionLifecycleGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_paused || _state != AssistantState.Idle) return;
                SetState(AssistantState.Listening);
                cts = ReplaceTurnCancellation();
                _lastActivity = DateTime.UtcNow;
            }
            finally { _sessionLifecycleGate.Release(); }
            var text = await _ears.CaptureAsync(TimeSpan.FromSeconds(_settings.FollowUpSeconds), cts.Token)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetState(AssistantState.Idle);
                return;
            }
            _events.Emit("heard", text!, true, ("source", "follow-up"));
            await SubmitAsync(text!, "voice", cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { SetState(AssistantState.Idle); }
        finally { _followUpDepth--; }
    }

    public async Task StopAsync()
    {
        _events.Emit("stopped", null, false);
        _tts.Stop();
        _ears.CancelCapture();
        _approvals.Answer(ApprovalOutcome.Denied);
        try { _turnCts?.Cancel(); } catch { }
        if (_state is not (AssistantState.Idle or AssistantState.Paused)) SetState(AssistantState.Idle);
        await Task.CompletedTask;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) { _tts.Stop(); SetState(AssistantState.Paused); }
        else SetState(AssistantState.Idle);
        _events.Emit("paused", paused.ToString(), false);
    }

    public bool AnswerApproval(ApprovalOutcome outcome)
    {
        var answered = _approvals.Answer(outcome);
        if (answered) _events.Emit("approval_answered", outcome.ToString(), false);
        return answered;
    }

    public async Task NewSessionAsync(bool silent = false)
    {
        await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await ResetSessionCoreAsync(silent, cancelActive: true).ConfigureAwait(false); }
        finally { _sessionLifecycleGate.Release(); }
    }

    private async Task ResetSessionCoreAsync(bool silent, bool cancelActive)
    {
        if (cancelActive)
        {
            try { _turnCts?.Cancel(); } catch { }
        }
        _tts.Stop();
        await _session.WaitForIdleAsync().ConfigureAwait(false);
        var turns = _session.Turns;
        await SummarizeAsync().ConfigureAwait(false);
        _session.Reset();
        _events.Emit("session_reset", null, true, ("turns", turns));
        if (!silent) Speak("Very good, " + _settings.Honorific + ". Fresh session, ready when you are.");
        SetState(_paused ? AssistantState.Paused : AssistantState.Idle);
    }

    // ---------- speaking ----------

    private Task SpeakSentenceAsync(string sentence)
    {
        _events.Emit("say", sentence, false);
        _tts.Say(sentence);
        return Task.CompletedTask;
    }

    private async Task SpeakDelayedAcknowledgementAsync(Task firstText, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(850), ct).ConfigureAwait(false);
            if (!firstText.IsCompleted && !ct.IsCancellationRequested)
                Speak("One moment, sir.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warn("delayed acknowledgement failed", ex); }
    }

    private void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!_settings.SpeakReplies) { _events.Emit("say", text, false); return; }
        _tts.Say(text);
        _events.Emit("say", text, false);
    }

    private async Task UnpromptedAsync(string text)
    {
        if (_paused) return;
        if (_settings.PoliteMode)
        {
            var call = CallDetector.CurrentCallApp();
            if (call is not null)
            {
                _events.Emit("polite_hold", text, true, ("app", call));
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline && CallDetector.CurrentCallApp() is not null && !_lifetime.IsCancellationRequested)
                    await Task.Delay(2000).ConfigureAwait(false);
                if (CallDetector.CurrentCallApp() is not null)
                    await _services.Ui.NotifyAsync("JARVIS has an update", text).ConfigureAwait(false);
                _events.Emit("polite_release", text, true, ("app", call));
            }
        }
        Speak(text);
    }

    private void OnTimerFired(TimerItem item)
    {
        _events.Emit("timer_fired", item.Label, true, ("id", item.Id));
        _ = Task.Run(() => UnpromptedAsync(
            $"Your {item.Label} timer is up, {_settings.Honorific}."));
    }

    private async Task OnWorkerFinishedAsync(Worker worker)
    {
        var message = worker.State switch
        {
            "done" => $"The {worker.Name} worker has finished. {Trim(worker.LastMessage, 420)}",
            "waiting" => $"The {worker.Name} worker needs you. {CleanQuestion(worker.LastMessage)}",
            "failed" => $"I'm afraid the {worker.Name} worker failed: {Trim(worker.LastMessage, 300)}",
            "stopped" => $"The {worker.Name} worker has stopped, {_settings.Honorific}.",
            _ => $"The {worker.Name} worker is {worker.State}."
        };
        _events.Emit("worker_finished", message, true, ("name", worker.Name), ("state", worker.State));
        await UnpromptedAsync(message).ConfigureAwait(false);
    }

    private static string CleanQuestion(string text)
    {
        var index = text.IndexOf("NEED USER:", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? text[(index + 10)..].Trim() : Trim(text, 300);
    }

    // ---------- approvals ----------

    private async Task<ApprovalOutcome> HandleApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        _pendingQuestion = request.Question;
        SetState(AssistantState.Waiting);
        _events.Emit("approval_ask", request.Question, true, ("tool", request.ToolName));

        try
        {
            await _tts.WaitIdleAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);

            if (_settings.SpeakReplies) Speak(request.Question);
            await _tts.WaitIdleAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);

            if (_settings.ConfirmByVoice && _settings.SpeakReplies && _ears.Available)
            {
                _tts.Chime();
                var heard = await _ears.CaptureAsync(TimeSpan.FromSeconds(_settings.ConfirmTimeoutSeconds), ct)
                    .ConfigureAwait(false);
                _events.Emit("approval_heard", heard ?? "", true);
                if (AnswerParser.IsYes(heard)) return ApprovalOutcome.Allowed;
                if (AnswerParser.IsNo(heard)) return ApprovalOutcome.Denied;
                // Answered on screen while we were listening: do not open a second question.
                if (ct.IsCancellationRequested) return ApprovalOutcome.Denied;
            }

            var answer = await _services.Ui.AskAsync(request.Question, ct).ConfigureAwait(false);
            return answer switch
            {
                true => ApprovalOutcome.Allowed,
                false => ApprovalOutcome.Denied,
                _ => ApprovalOutcome.TimedOut
            };
        }
        catch (OperationCanceledException) { return ApprovalOutcome.Denied; }
        finally
        {
            _pendingQuestion = "";
            if (_state == AssistantState.Waiting) SetState(AssistantState.Thinking);
        }
    }

    // ---------- wake and barge-in ----------

    private void OnWakeWord(string phrase)
    {
        _events.Emit("wake", phrase, true);
        if (_paused) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var state = _state;
                if (state is AssistantState.Thinking or AssistantState.Speaking)
                {
                    // Wake recognition is intentionally sensitive so users can activate Jarvis
                    // from across the room. At a low threshold, false positives must not cancel
                    // a long-running request unless the user explicitly enabled barge-in.
                    if (!_settings.BargeIn)
                    {
                        Log.Info("Ignored wake-word interrupt while a turn was active; barge-in is disabled.");
                        return;
                    }
                    _events.Emit("barge_in", "wake word", false);
                    await StopAsync().ConfigureAwait(false);
                }
                else if (state is AssistantState.Listening or AssistantState.Waiting or AssistantState.Paused)
                {
                    return;
                }
                await ListenAsync(playChime: false).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("wake handling failed", ex); }
        });
    }

    private void OnSpeechDetected()
    {
        if (!_settings.BargeIn || _ears.IsCapturing ||
            _state is not (AssistantState.Thinking or AssistantState.Speaking)) return;
        if (_tts.IsSpeaking)
        {
            _events.Emit("barge_in", "voice", false);
            _tts.Stop();
            _ = StopAsync();
        }
    }

    // ---------- session lifecycle ----------

    private void CheckIdle()
    {
        try
        {
            if (_paused || _state != AssistantState.Idle || _tts.IsSpeaking || _session.Turns == 0) return;
            if ((DateTime.UtcNow - _lastActivity).TotalMinutes < Math.Max(5, _settings.SessionIdleResetMinutes)) return;
            if (Interlocked.CompareExchange(ref _idleResetScheduled, 1, 0) == 0)
                _ = Task.Run(ResetIfIdleAsync);
        }
        catch (Exception ex) { Log.Warn("idle check failed", ex); }
    }

    private async Task ResetIfIdleAsync()
    {
        try
        {
            await _sessionLifecycleGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                // Recheck only after taking the same lifecycle gate used to start a turn. A prompt
                // arriving after the timer fires therefore wins before any transcript reset.
                if (_paused || _state != AssistantState.Idle || _tts.IsSpeaking || _session.Turns == 0) return;
                if ((DateTime.UtcNow - _lastActivity).TotalMinutes < Math.Max(5, _settings.SessionIdleResetMinutes)) return;
                _events.Emit("idle_reset", null, false);
                await ResetSessionCoreAsync(silent: true, cancelActive: false).ConfigureAwait(false);
            }
            finally { _sessionLifecycleGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warn("idle reset failed", ex); }
        finally { Interlocked.Exchange(ref _idleResetScheduled, 0); }
    }

    private async Task SummarizeAsync()
    {
        if (!_settings.AutoUpdateNotes || _session.Turns == 0) return;
        var model = ModelFactory.Create(_settings);
        if (model is null) return;
        using var modelLifetime = model as IDisposable;

        try
        {
            var transcript = new StringBuilder();
            foreach (var message in _session.Transcript.TakeLast(14))
            {
                if (message.Images is { Count: > 0 }) continue;
                var role = message.Role switch { "user" => "USER", "assistant" => "JARVIS", _ => "TOOL" };
                var content = Trim(message.Content.Replace("\n", " "), 400);
                if (content.Length == 0) continue;
                transcript.AppendLine($"{role}: {content}");
            }
            if (transcript.Length == 0) return;

            var system = "You maintain a running notes file for a voice assistant. In at most three short lines, " +
                         "record what the user asked and what happened, plus any lasting lesson about how they work. " +
                         "Plain text, no markdown, no preamble. If nothing is worth keeping, reply exactly: NOTHING";
            var reply = await model.StreamAsync(new ModelRequest(system,
                new[] { ChatMessage.User(transcript.ToString()) }, new ToolRegistry(), 0, 160), null,
                new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token).ConfigureAwait(false);

            var summary = reply.Text.Trim();
            if (summary.Length > 0 && !summary.Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                _memory.AppendRecent(Trim(summary.Replace("\n", " "), 420));
        }
        catch (Exception ex) { Log.Warn("session summary failed", ex); }
    }

    // ---------- plumbing ----------

    private CancellationTokenSource ReplaceTurnCancellation(CancellationToken external = default)
    {
        lock (_turnGate)
        {
            if (_turnCts is not null && external != default && external == _turnCts.Token)
                return _turnCts;
            try { _turnCts?.Cancel(); } catch { }
            _turnCts = external == default
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, external);
            return _turnCts;
        }
    }

    private void SetState(AssistantState state)
    {
        if (_state == state) return;
        _state = state;
        _events.Emit("state", state.ToString(), false);
        try { StateChanged?.Invoke(state); } catch { }
        try { Changed?.Invoke(); } catch { }
    }

    private void OnEvent(JarvisEvent ev)
    {
        switch (ev.Kind)
        {
            case "tool_call":
                _stepText = HumanizeStep(ev.Str("name") ?? "", ev.Str("input") ?? "");
                try { Changed?.Invoke(); } catch { }
                break;
            case "turn_start":
                _userText = ev.Text ?? "";
                _stepText = "";
                _replyText = "";
                try { Changed?.Invoke(); } catch { }
                break;
            case "turn_end":
                _stepText = "";
                break;
            case "say":
            case "speaking_now":
                _replyText = ev.Text ?? _replyText;
                break;
        }
    }

    private static readonly Dictionary<string, string> StepWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["screenshot"] = "Looking at the screen", ["click_at"] = "Clicking", ["move_mouse"] = "Moving the mouse",
        ["scroll"] = "Scrolling", ["type_text"] = "Typing", ["press_keys"] = "Pressing keys",
        ["launch_app"] = "Opening an app", ["list_apps"] = "Checking installed apps", ["open_path"] = "Opening",
        ["close_app"] = "Closing", ["focus_window"] = "Switching windows", ["list_windows"] = "Checking windows",
        ["window_action"] = "Arranging windows", ["volume"] = "Adjusting the volume", ["media"] = "Media",
        ["clipboard"] = "The clipboard", ["notify"] = "Notifying", ["timer"] = "Setting a timer",
        ["system"] = "System", ["system_info"] = "Checking the system", ["weather"] = "Checking the weather",
        ["read_file"] = "Reading a file", ["write_file"] = "Writing a file", ["search_files"] = "Searching files",
        ["delete_path"] = "Deleting", ["run_powershell"] = "Running a command", ["annotate"] = "Drawing on the screen",
        ["clear_annotations"] = "Clearing the screen", ["remember"] = "Remembering", ["recall"] = "Recalling",
        ["note"] = "Taking a note", ["worker_start"] = "Starting a background job",
        ["worker_list"] = "Checking the workers", ["worker_message"] = "Briefing a worker",
        ["worker_stop"] = "Stopping a worker"
    };

    private static string HumanizeStep(string name, string inputJson)
    {
        var label = StepWords.TryGetValue(name, out var known) ? known : name.Replace('_', ' ');
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "description", "query", "name", "url", "action", "target", "path", "label" })
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString() ?? "";
                    if (text.Length == 0) continue;
                    detail = key == "path" ? Path.GetFileName(text) : text;
                    break;
                }
            }
        }
        catch { }
        return detail.Length > 0 ? $"{label}: {Trim(detail, 60)}" : label;
    }

    private static string Trim(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = Regex.Replace(text, @"\s+", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        try { _voiceSync?.Dispose(); } catch { }
        try { await ShutdownAsync().ConfigureAwait(false); } catch { }
        try { await _mcpServer.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); } catch { }
        try { _session.Dispose(); } catch { }
        try { _ears.Dispose(); } catch { }
        try { _tts.Dispose(); } catch { }
        try { _timers.Dispose(); } catch { }
        _lifetime.Dispose();
    }

    private static string ModelSettingsKey(AppSettings settings) => string.Join("\u001f",
        settings.Provider, settings.Preset, settings.BaseUrl, settings.Model, settings.ApiKeyEncrypted);

}
