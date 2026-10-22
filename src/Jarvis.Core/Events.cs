using System.Text.Json;

namespace Jarvis.Core;

/// <summary>One thing that happened. Everything the assistant does is reported as one of these.</summary>
public sealed class JarvisEvent
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public string Kind { get; init; } = "";
    public string? Text { get; init; }
    public Dictionary<string, object?> Data { get; init; } = new();

    public string? Str(string key) => Data.TryGetValue(key, out var v) ? v?.ToString() : null;

    public double Num(string key, double fallback = 0) =>
        Data.TryGetValue(key, out var v) && v is not null &&
        double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    public override string ToString() => Text is null ? Kind : $"{Kind}: {Text}";
}

/// <summary>
/// The single spine of the app: history for replay, JSONL for forensics, and subscribers for the
/// dashboard and the HUD. High-frequency events can opt out of persistence.
/// </summary>
public sealed class EventBus : IDisposable
{
    // Event history stays available to the live UI, but free-form user, model, file and shell
    // content must not be written to a long-lived JSONL file by default.
    private static readonly HashSet<string> PersistableKinds = new(StringComparer.Ordinal)
    {
        "started", "session_reset"
    };
    private readonly object _gate = new();
    private readonly List<JarvisEvent> _history = new();
    private readonly List<Action<JarvisEvent>> _subs = new();
    private readonly string _file;
    private long _next = 1;

    public int Capacity { get; init; } = 4000;

    public EventBus(string dataDirectory)
    {
        var dir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "events.jsonl");
        LoadTail();
    }

    public event Action<JarvisEvent>? Published;

    public IReadOnlyList<JarvisEvent> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    public JarvisEvent Emit(string kind, string? text = null, bool persist = true,
        params (string Key, object? Value)[] data)
    {
        persist &= PersistableKinds.Contains(kind);
        var ev = new JarvisEvent
        {
            Id = Interlocked.Increment(ref _next),
            At = DateTimeOffset.Now,
            Kind = kind,
            Text = text,
            Data = new Dictionary<string, object?>(StringComparer.Ordinal)
        };
        foreach (var (k, v) in data) ev.Data[k] = v;

        lock (_gate)
        {
            _history.Add(ev);
            if (_history.Count > Capacity) _history.RemoveRange(0, _history.Count - Capacity);
            if (persist)
            {
                try { File.AppendAllText(_file, JsonSerializer.Serialize(ev) + Environment.NewLine); }
                catch { }
            }
        }

        if (persist) TrimFile();
        try { Published?.Invoke(ev); } catch { }
        Action<JarvisEvent>[] subs;
        lock (_gate) subs = _subs.ToArray();
        foreach (var s in subs) { try { s(ev); } catch { } }
        return ev;
    }

    public IDisposable Subscribe(Action<JarvisEvent> callback)
    {
        lock (_gate) _subs.Add(callback);
        return new Sub(this, callback);
    }

    private void TrimFile()
    {
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.Length < 8 * 1024 * 1024) return;
            var keep = File.ReadAllLines(_file).TakeLast(2000);
            File.WriteAllLines(_file, keep);
        }
        catch { }
    }

    private void LoadTail()
    {
        try
        {
            if (!File.Exists(_file)) return;
            foreach (var line in File.ReadLines(_file).TakeLast(Capacity))
            {
                try
                {
                    var ev = JsonSerializer.Deserialize<JarvisEvent>(line);
                    if (ev is null || !PersistableKinds.Contains(ev.Kind)) continue;
                    _history.Add(ev);
                    if (ev.Id >= _next) _next = ev.Id + 1;
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate) _subs.Clear();
    }

    private sealed class Sub : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Action<JarvisEvent> _cb;
        public Sub(EventBus bus, Action<JarvisEvent> cb) { _bus = bus; _cb = cb; }
        public void Dispose() { lock (_bus._gate) _bus._subs.Remove(_cb); }
    }
}
