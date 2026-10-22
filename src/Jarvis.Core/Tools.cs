using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Core;

public sealed record ToolResult(bool Success, string Content, string? ImageBase64 = null, string? ImageMime = null)
{
    public static ToolResult Done(string content) => new(true, content);
    public static ToolResult Fail(string content) => new(false, content);
    public static ToolResult WithImage(string content, byte[] image, string mime = "image/jpeg") =>
        new(true, content, Convert.ToBase64String(image), mime);
}

public sealed class ToolContext
{
    public required JarvisServices Services { get; init; }
    public string Worker { get; init; } = "";
    public bool IsWorker => Worker.Length > 0;
    public CancellationToken Cancellation { get; init; }
    /// <summary>The resolved path the policy check approved for a file write.</summary>
    public string? ApprovedTargetPath { get; init; }
}

public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ParametersJson { get; init; }
    public bool ReadOnly { get; init; }
    public string Category { get; init; } = "general";
    public required Func<ToolContext, JsonElement, Task<ToolResult>> Handler { get; init; }
    /// <summary>A short phrase for the spoken confirmation, e.g. "delete the file report.docx".</summary>
    public Func<JsonElement, string>? Describe { get; init; }

    public string DescribeCall(JsonElement args)
    {
        try
        {
            var d = Describe?.Invoke(args);
            if (!string.IsNullOrWhiteSpace(d)) return d!;
        }
        catch { }
        return Name.Replace('_', ' ');
    }
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolDefinition> _order = new();

    public IReadOnlyList<ToolDefinition> All => _order;
    public int Count => _order.Count;

    public ToolRegistry Add(ToolDefinition tool)
    {
        if (_tools.ContainsKey(tool.Name)) throw new InvalidOperationException($"duplicate tool {tool.Name}");
        _tools[tool.Name] = tool;
        _order.Add(tool);
        return this;
    }

    public ToolRegistry AddRange(IEnumerable<ToolDefinition> tools)
    {
        foreach (var t in tools) Add(t);
        return this;
    }

    public ToolDefinition? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    public JsonArray ToOpenAiSchema()
    {
        var arr = new JsonArray();
        foreach (var t in _order)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersJson)
                }
            });
        }
        return arr;
    }

    public JsonArray ToAnthropicSchema()
    {
        var arr = new JsonArray();
        foreach (var t in _order)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.ParametersJson)
            });
        }
        return arr;
    }
}

// ---------- tiny JSON-schema builder so tool schemas stay readable ----------

public sealed record ToolParam(
    string Name, string Kind, string Description, bool Required = false,
    string[]? Values = null, ToolParam[]? Properties = null);

public static class T
{
    public static ToolParam Str(string name, string description, bool required = false) =>
        new(name, "string", description, required);

    public static ToolParam Int(string name, string description, bool required = false) =>
        new(name, "integer", description, required);

    public static ToolParam Num(string name, string description, bool required = false) =>
        new(name, "number", description, required);

    public static ToolParam Bool(string name, string description, bool required = false) =>
        new(name, "boolean", description, required);

    public static ToolParam OneOf(string name, string description, string[] values, bool required = false) =>
        new(name, "string", description, required, values);

    public static ToolParam Strings(string name, string description, bool required = false) =>
        new(name, "string[]", description, required);

    public static ToolParam Objects(string name, string description, ToolParam[] properties, bool required = false) =>
        new(name, "object[]", description, required, null, properties);

    public static string Schema(params ToolParam[] parameters)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            props[p.Name] = Property(p);
            if (p.Required) required.Add(p.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        schema["additionalProperties"] = false;
        return schema.ToJsonString();
    }

    private static JsonObject Property(ToolParam p)
    {
        switch (p.Kind)
        {
            case "object[]":
            {
                var innerProps = new JsonObject();
                var innerRequired = new JsonArray();
                foreach (var child in p.Properties ?? Array.Empty<ToolParam>())
                {
                    innerProps[child.Name] = Property(child);
                    if (child.Required) innerRequired.Add(child.Name);
                }
                var item = new JsonObject { ["type"] = "object", ["properties"] = innerProps };
                if (innerRequired.Count > 0) item["required"] = innerRequired;
                var arr = new JsonObject { ["type"] = "array", ["items"] = item };
                if (!string.IsNullOrEmpty(p.Description)) arr["description"] = p.Description;
                return arr;
            }
            case "string[]":
                return new JsonObject { ["type"] = "array", ["description"] = p.Description, ["items"] = new JsonObject { ["type"] = "string" } };
            default:
            {
                var o = new JsonObject { ["type"] = p.Kind, ["description"] = p.Description };
                if (p.Values is { Length: > 0 })
                {
                    o["type"] = "string";
                    var values = new JsonArray();
                    foreach (var v in p.Values) values.Add(v);
                    o["enum"] = values;
                }
                return o;
            }
        }
    }
}

// ---------- annotation overlay contract (implemented by the WPF app) ----------

public sealed record AnnotationShape(
    string Type, double X, double Y, double? Radius = null, double? FromX = null, double? FromY = null,
    double? Width = null, double? Height = null, string? Label = null, int? Step = null);

public sealed record AnnotationRequest(
    IReadOnlyList<AnnotationShape> Shapes, TimeSpan Duration, int DesktopX, int DesktopY, double Scale);

public interface IAnnotationSink
{
    Task ShowAsync(AnnotationRequest request);
    void Clear();
}

public sealed class NullAnnotations : IAnnotationSink
{
    public Task ShowAsync(AnnotationRequest request) => Task.CompletedTask;
    public void Clear() { }
}

// ---------- small platform services the core needs from the shell ----------

public interface IUserInterface
{
    Task NotifyAsync(string title, string body, CancellationToken ct = default);
    Task<bool?> AskAsync(string question, CancellationToken ct = default);
    void ShowDeck();
}

public sealed class NullUserInterface : IUserInterface
{
    public Task NotifyAsync(string title, string body, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool?> AskAsync(string question, CancellationToken ct = default) => Task.FromResult<bool?>(null);
    public void ShowDeck() { }
}

public sealed class ScreenshotState
{
    private readonly object _gate = new();
    private int _x, _y;
    private double _scale = 1;
    private DateTime _at = DateTime.MinValue;
    private long _desktopVersion;
    private nint _window;
    private string _label = "";

    public void Update(CapturedImage image) => Update(image, desktopVersion: 0);

    public void Update(CapturedImage image, long desktopVersion)
    {
        lock (_gate)
        {
            _x = image.DesktopX;
            _y = image.DesktopY;
            _scale = image.Scale;
            _window = image.WindowHandle;
            _label = image.Label;
            _desktopVersion = desktopVersion;
            _at = DateTime.UtcNow;
        }
    }

    public (int X, int Y) ToDesktop(double imageX, double imageY)
    {
        lock (_gate) return ((int)Math.Round(_x + imageX * _scale), (int)Math.Round(_y + imageY * _scale));
    }

    public double Scale { get { lock (_gate) return _scale; } }
    public string Label { get { lock (_gate) return _label; } }
    public bool HasShot { get { lock (_gate) return _at > DateTime.MinValue; } }
    public bool Stale { get { lock (_gate) return (DateTime.UtcNow - _at).TotalSeconds > 60; } }

    public string? FocusRefusal(long? currentDesktopVersion = null)
    {
        lock (_gate)
        {
            if (_at > DateTime.MinValue && (DateTime.UtcNow - _at).TotalSeconds > 60)
                return "Not done: the last screenshot is over a minute old. Take a fresh one before clicking.";

            if (currentDesktopVersion is { } current && _at > DateTime.MinValue &&
                current != _desktopVersion)
                return "Not done: the desktop changed after that screenshot. Take a fresh screenshot before using those coordinates.";

            if (_window == IntPtr.Zero) return null;
            var active = WindowApi.Active();
            if (active is not null && active.Handle != _window)
                return $"Not done: '{active.Title}' took focus since the last screenshot. Take a fresh screenshot first.";
        }
        return null;
    }

    public void RememberFocus(nint hwnd) { lock (_gate) _window = hwnd; }
}

public sealed record TimerItem(string Id, string Label, DateTimeOffset Due);

public sealed class TimerService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Timer timer, TimerItem item)> _timers = new();
    private int _next = 1;

    public event Action<TimerItem>? Fired;

    public TimerItem Add(TimeSpan delay, string label)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        lock (_gate)
        {
            var item = new TimerItem($"t{_next++}", string.IsNullOrWhiteSpace(label) ? "timer" : label.Trim(),
                DateTimeOffset.Now + delay);
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (_gate) _timers.Remove(item.Id);
                try { Fired?.Invoke(item); } catch (Exception ex) { Log.Warn("timer handler failed", ex); }
                timer?.Dispose();
            }, null, delay, Timeout.InfiniteTimeSpan);
            _timers[item.Id] = (timer, item);
            return item;
        }
    }

    public bool Cancel(string idOrLabel)
    {
        lock (_gate)
        {
            var key = _timers.Keys.FirstOrDefault(k => k == idOrLabel) ??
                      _timers.Keys.FirstOrDefault(k => _timers[k].item.Label.Contains(idOrLabel, StringComparison.OrdinalIgnoreCase));
            if (key is null) return false;
            _timers[key].timer.Dispose();
            _timers.Remove(key);
            return true;
        }
    }

    public IReadOnlyList<TimerItem> Active
    {
        get { lock (_gate) return _timers.Values.Select(v => v.item).OrderBy(i => i.Due).ToArray(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (timer, _) in _timers.Values) timer.Dispose();
            _timers.Clear();
        }
    }
}

/// <summary>Everything a tool might need, wired once at startup.</summary>
public sealed class JarvisServices
{
    public required AppSettings Settings { get; init; }
    public required EventBus Events { get; init; }
    public required MemoryStore Memory { get; init; }
    public required ScreenshotState Screenshot { get; init; }
    public required ApprovalService Approvals { get; init; }
    public required TimerService Timers { get; init; }
    public required PolicyEngine Policy { get; init; }
    public required ToolRegistry Tools { get; init; }
    public DesktopActionCoordinator DesktopActions { get; init; } = new();
    public SemaphoreSlim DesktopActionGate => DesktopActions.Gate;
    public IAnnotationSink Annotations { get; set; } = new NullAnnotations();
    public IUserInterface Ui { get; set; } = new NullUserInterface();
    public WorkerManager? Workers { get; set; }
    public string DataDirectory { get; init; } = Paths.Root;
    public Func<string, CancellationToken, Task<string?>>? ListenOnce { get; set; }
    public Func<string, Task>? Speak { get; set; }
    public Func<Task>? StopSpeaking { get; set; }
}

/// <summary>Serializes desktop operations and invalidates screenshots after any state-changing action.</summary>
public sealed class DesktopActionCoordinator
{
    private long _version;

    public SemaphoreSlim Gate { get; } = new(1, 1);
    public long Version => Interlocked.Read(ref _version);
    public void RecordChange() => Interlocked.Increment(ref _version);
}
