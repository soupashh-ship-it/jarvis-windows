using System.Text;

namespace Jarvis.Core;

public sealed class Worker
{
    internal readonly CancellationTokenSource Cts = new();
    internal readonly SemaphoreSlim Turn = new(1, 1);
    private readonly Queue<string> _pending = new();
    private readonly object _pendingGate = new();
    private bool _runnerActive;

    public Worker(string name, string brief)
    {
        Name = name;
        Brief = brief;
        Started = DateTimeOffset.Now;
        State = "starting";
    }

    public string Name { get; }
    public string Brief { get; }
    public DateTimeOffset Started { get; }
    public string State { get; internal set; }
    public string LastMessage { get; internal set; } = "";
    public int Turns { get; internal set; }
    public bool HasRun { get; internal set; }
    public Task? Runner { get; internal set; }
    public bool IsLive => State is "starting" or "running" or "waiting";

    internal void StartRunner()
    {
        lock (_pendingGate) _runnerActive = true;
    }

    internal bool EnqueueAndStartIfIdle(string text)
    {
        lock (_pendingGate)
        {
            if (Cts.IsCancellationRequested || State is "stopped" or "failed")
                throw new InvalidOperationException($"The {Name} worker is {State}; start a new one.");
            _pending.Enqueue(text);
            State = "running";
            if (_runnerActive) return false;
            _runnerActive = true;
            return true;
        }
    }

    public bool TryDequeue(out string text)
    {
        lock (_pendingGate)
        {
            if (_pending.Count > 0) { text = _pending.Dequeue(); return true; }
            text = "";
            return false;
        }
    }

    internal bool HasPending
    {
        get { lock (_pendingGate) return _pending.Count > 0; }
    }

    /// <summary>Close the current runner, or reserve the runner slot for a queued follow-up.</summary>
    internal bool RestartIfPending()
    {
        lock (_pendingGate)
        {
            if (_pending.Count > 0 && !Cts.IsCancellationRequested && State is not ("stopped" or "failed")) return true;
            _runnerActive = false;
            return false;
        }
    }

    internal void StopWorker()
    {
        lock (_pendingGate) State = "stopped";
        try { Cts.Cancel(); } catch { }
    }
}

/// <summary>
/// Background agents. Each has its own conversation and brief, keeps working while the user talks to JARVIS,
/// and reports back in one spoken sentence. Actions requiring approval are sent directly to the user.
/// </summary>
public sealed class WorkerManager
{
    private const int MaxActiveWorkers = 4;
    private const int MaxRetainedWorkers = 40;
    private readonly JarvisServices _services;
    private readonly PresenceTracker _presence;
    private readonly object _gate = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.OrdinalIgnoreCase);

    public WorkerManager(JarvisServices services, PresenceTracker presence)
    {
        _services = services;
        _presence = presence;
    }

    /// <summary>Called when a worker finishes, fails, stops, or needs the user.</summary>
    public Func<Worker, Task>? OnFinished { get; set; }

    public IReadOnlyList<Worker> All
    {
        get { lock (_gate) return _workers.Values.OrderByDescending(w => w.Started).ToArray(); }
    }

    public Worker? Get(string name)
    {
        lock (_gate) return _workers.TryGetValue(name.Trim(), out var w) ? w : null;
    }

    public Worker Start(string name, string brief)
    {
        var key = Normalize(name);
        lock (_gate)
        {
            if (_workers.TryGetValue(key, out var existing) && existing.IsLive)
                throw new InvalidOperationException($"A worker called {key} is already {existing.State}.");
            if (_workers.Values.Count(w => w.IsLive) >= MaxActiveWorkers)
                throw new InvalidOperationException($"Jarvis can run up to {MaxActiveWorkers} background jobs at once. Wait for one to finish, then try again.");
            var worker = new Worker(key, brief);
            _workers[key] = worker;
            foreach (var old in _workers.Values.Where(w => !w.IsLive)
                         .OrderByDescending(w => w.Started).Skip(MaxRetainedWorkers).ToArray())
            {
                if (_workers.TryGetValue(old.Name, out var current) && ReferenceEquals(current, old))
                    _workers.Remove(old.Name);
            }
            worker.StartRunner();
            worker.Runner = Task.Run(() => RunAsync(worker));
            return worker;
        }
    }

    public void Send(string name, string text)
    {
        var worker = Get(name) ?? throw new InvalidOperationException($"There is no worker called {name}.");
        var startRunner = worker.EnqueueAndStartIfIdle(text);
        Emit(worker);
        if (startRunner)
            worker.Runner = Task.Run(() => RunAsync(worker));
    }

    public void Stop(string name)
    {
        var worker = Get(name) ?? throw new InvalidOperationException($"There is no worker called {name}.");
        worker.StopWorker();
        Emit(worker);
    }

    public void StopAll()
    {
        foreach (var w in All) w.StopWorker();
    }

    private async Task RunAsync(Worker worker)
    {
        var model = ModelFactory.Create(_services.Settings);
        if (model is null)
        {
            worker.State = "failed";
            _ = worker.RestartIfPending();
            worker.LastMessage = "No model is configured, so I cannot run background work.";
            Emit(worker);
            await Notify(worker).ConfigureAwait(false);
            return;
        }

        var workerServices = new JarvisServices
        {
            Settings = _services.Settings,
            Events = _services.Events,
            Memory = _services.Memory,
            Screenshot = new ScreenshotState(),
            Approvals = _services.Approvals,
            Timers = _services.Timers,
            Policy = _services.Policy,
            Tools = _services.Tools,
            DesktopActions = _services.DesktopActions,
            Annotations = _services.Annotations,
            Ui = _services.Ui,
            Workers = this,
            DataDirectory = _services.DataDirectory,
            ListenOnce = _services.ListenOnce,
            Speak = _services.Speak,
            StopSpeaking = _services.StopSpeaking
        };
        var session = new AgentSession(workerServices, model, _presence, _ => Task.CompletedTask,
            worker.Name, (request, ct) => RequestApprovalAsync(worker, request, ct));

        worker.State = "running";
        Emit(worker);

        try
        {
            var first = !worker.HasRun;
            worker.HasRun = true;
            while (true)
            {
                worker.Cts.Token.ThrowIfCancellationRequested();
                await worker.Turn.WaitAsync(worker.Cts.Token).ConfigureAwait(false);
                try
                {
                    var prompt = first ? WorkerBrief(worker) : worker.TryDequeue(out var queued) ? queued : "";
                    first = false;
                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        if (worker.TryDequeue(out var next)) prompt = next;
                        else break;
                    }

                    worker.State = "running";
                    Emit(worker);
                    var answer = await session.AskAsync(prompt, worker.Cts.Token).ConfigureAwait(false);
                    worker.Turns++;
                    worker.LastMessage = string.IsNullOrWhiteSpace(answer) ? worker.LastMessage : answer.Trim();

                    if (worker.LastMessage.Contains("NEED USER:", StringComparison.OrdinalIgnoreCase))
                        worker.State = "waiting";
                    else
                        worker.State = "done";
                }
                finally
                {
                    worker.Turn.Release();
                }

                if (worker.State == "waiting") break;
                if (!worker.HasPending) break;
            }

            if (worker.State is not ("waiting" or "stopped")) worker.State = "done";
        }
        catch (OperationCanceledException)
        {
            worker.State = "stopped";
            worker.LastMessage = worker.LastMessage.Length > 0 ? worker.LastMessage : "Stopped on request.";
        }
        catch (Exception ex)
        {
            Log.Warn($"worker {worker.Name} failed", ex);
            worker.State = "failed";
            worker.LastMessage = ex.Message;
        }
        finally
        {
            session.Dispose();
            Emit(worker);
            await Notify(worker).ConfigureAwait(false);
            if (worker.RestartIfPending())
            {
                worker.State = "running";
                worker.Runner = Task.Run(() => RunAsync(worker));
            }
        }
    }

    private string WorkerBrief(Worker worker) =>
        $"""
         You are a JARVIS worker. {_services.Settings.UserName} asked JARVIS to have you do one job in the background,
         and is not watching this conversation.

         # The job
         {worker.Brief}

         # How to work
         - Work autonomously within the existing tool policy. Anything requiring confirmation is sent to
           {_services.Settings.UserName} as a spoken question; never approve it yourself.
         - Use the same Windows tools JARVIS has. Do not talk to the user directly; nobody is listening to you.
         - End your turn with one or two plain sentences: what you did and where the result is. That line is read aloud.
         - If you need a decision or information, end the turn with a line that starts "NEED USER:" and the question.
         - Never close or reuse the user's windows or tabs unless the brief is about them.
         """;

    private async Task<ApprovalOutcome> RequestApprovalAsync(Worker worker, ApprovalRequest request, CancellationToken ct)
    {
        var escalated = request with
        {
            Question = $"The {worker.Name} worker asks: {request.Question}"
        };
        worker.State = "waiting";
        Emit(worker);
        var outcome = await _services.Approvals.RequestAsync(escalated, ct).ConfigureAwait(false);
        worker.State = "running";
        Emit(worker);
        return outcome;
    }

    private void Emit(Worker worker) =>
        _services.Events.Emit("worker", worker.LastMessage, true,
            ("name", worker.Name), ("state", worker.State), ("minutes", Math.Round((DateTimeOffset.Now - worker.Started).TotalMinutes, 1)));

    private async Task Notify(Worker worker)
    {
        if (OnFinished is null) return;
        try { await OnFinished(worker).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("worker notification failed", ex); }
    }

    private static string Normalize(string name)
    {
        var cleaned = new string(name.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray());
        cleaned = cleaned.Replace('_', ' ').Trim();
        return cleaned.Length == 0 ? "job" : cleaned;
    }

    public string Describe() 
    {
        var all = All;
        if (all.Count == 0) return "No workers.";
        var sb = new StringBuilder();
        foreach (var w in all)
            sb.AppendLine($"{w.Name}: {w.State}, {(int)(DateTimeOffset.Now - w.Started).TotalMinutes} min, " +
                          $"{w.Turns} turn(s). Last: {Trim(w.LastMessage, 300)}");
        return sb.ToString().TrimEnd();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public static class WorkerTools
{
    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "worker_start",
            Category = "workers",
            ReadOnly = false,
            Description = "Start a background worker: a separate agent with the same Windows tools, for a job that will " +
                          "take more than a minute or that should run in parallel. Give it a short spoken name and a " +
                          "complete, self-contained brief (it cannot see this conversation).",
            ParametersJson = T.Schema(
                T.Str("name", "Short name, e.g. 'the video edit'.", true),
                T.Str("task", "A complete brief: the goal, constraints, where the result should go.", true)),
            Handler = (ctx, args) =>
            {
                if (ctx.Services.Workers is null) return Task.FromResult(ToolResult.Fail("Workers are not available."));
                if (ctx.Services.Settings.Provider == ProviderKind.AntigravityCli)
                    return Task.FromResult(ToolResult.Fail(
                        "Background workers cannot route Antigravity tool calls to the right Jarvis session. " +
                        "Continue this task in the current conversation using the available tools."));
                try
                {
                    var worker = ctx.Services.Workers.Start(Json.Str(args, "name"), Json.Str(args, "task"));
                    return Task.FromResult(ToolResult.Done(
                        $"Started the {worker.Name} worker. I will report back when it finishes or needs you."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "worker_list",
            Category = "workers",
            ReadOnly = true,
            Description = "List background workers with their state, how long they have been running, and their last message.",
            ParametersJson = T.Schema(),
            Handler = (ctx, _) => Task.FromResult(ToolResult.Done(
                ctx.Services.Workers?.Describe() ?? "Workers are not available."))
        };

        yield return new ToolDefinition
        {
            Name = "worker_message",
            Category = "workers",
            ReadOnly = false,
            Description = "Send a follow-up instruction, or the user's answer to a worker's question, to a named worker.",
            ParametersJson = T.Schema(
                T.Str("name", "The worker's name.", true),
                T.Str("text", "What to tell it.", true)),
            Handler = (ctx, args) =>
            {
                if (ctx.Services.Workers is null) return Task.FromResult(ToolResult.Fail("Workers are not available."));
                try
                {
                    ctx.Services.Workers.Send(Json.Str(args, "name"), Json.Str(args, "text"));
                    return Task.FromResult(ToolResult.Done("Sent."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
            }
        };

        yield return new ToolDefinition
        {
            Name = "worker_stop",
            Category = "workers",
            ReadOnly = false,
            Description = "Stop a background worker by name.",
            ParametersJson = T.Schema(T.Str("name", "The worker's name.", true)),
            Handler = (ctx, args) =>
            {
                if (ctx.Services.Workers is null) return Task.FromResult(ToolResult.Fail("Workers are not available."));
                try
                {
                    ctx.Services.Workers.Stop(Json.Str(args, "name"));
                    return Task.FromResult(ToolResult.Done("Stopped."));
                }
                catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
            }
        };
    }
}

public static class MemoryTools
{
    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "remember",
            Category = "memory",
            ReadOnly = false,
            Description = "Store a durable fact about the user (a preference, a name, a correction) that should survive " +
                          "between sessions. One short factual sentence.",
            ParametersJson = T.Schema(
                T.Str("fact", "The fact, written as a short sentence.", true),
                T.Str("tags", "Optional comma-separated tags.")),
            Handler = (ctx, args) =>
            {
                var fact = ctx.Services.Memory.Remember(Json.Str(args, "fact"), Json.Str(args, "tags"));
                return Task.FromResult(ToolResult.Done($"Remembered: {fact.Text}"));
            }
        };

        yield return new ToolDefinition
        {
            Name = "recall",
            Category = "memory",
            ReadOnly = true,
            Description = "Search durable facts you have been asked to remember.",
            ParametersJson = T.Schema(T.Str("query", "What to look for; empty lists the most recent.", true)),
            Handler = (ctx, args) =>
            {
                if (ctx.Services.Memory.FactsLoadWarning is { } warning)
                    return Task.FromResult(ToolResult.Fail(warning));
                var facts = ctx.Services.Memory.Search(Json.Str(args, "query"));
                return Task.FromResult(facts.Count == 0
                    ? ToolResult.Done("I do not have anything recorded about that.")
                    : ToolResult.Done(string.Join(Environment.NewLine, facts.Select(f => $"- {f.Text}"))));
            }
        };

        yield return new ToolDefinition
        {
            Name = "note",
            Category = "memory",
            ReadOnly = false,
            Description = "Add a one-line record of something you did to the running notes, so later sessions know " +
                          "what happened. Use it after finishing a meaningful task.",
            ParametersJson = T.Schema(T.Str("line", "One line: what they asked, what you did, how it went.", true)),
            Handler = (ctx, args) =>
            {
                ctx.Services.Memory.AppendRecent(Json.Str(args, "line"));
                return Task.FromResult(ToolResult.Done("Noted."));
            }
        };
    }
}
