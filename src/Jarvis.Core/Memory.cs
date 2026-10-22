using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

public sealed record Fact(string Id, string Text, DateTimeOffset At, string Tags = "")
{
    public override string ToString() => Text;
}

/// <summary>
/// Long-term memory: the running notes file (carried between sessions) plus small, durable facts.
/// Everything is plain text the user can read and edit by hand.
/// </summary>
public sealed class MemoryStore
{
    private readonly object _gate = new();
    private readonly string _notesPath;
    private readonly string _factsPath;
    private readonly List<Fact> _facts;
    private readonly string _userName;
    private string? _factsLoadWarning;

    public MemoryStore(string dataDirectory, string userName = "Tony")
    {
        Directory.CreateDirectory(dataDirectory);
        _notesPath = Path.Combine(dataDirectory, "notes.md");
        _factsPath = Path.Combine(dataDirectory, "memory.json");
        _userName = userName;
        _facts = LoadFacts();
    }

    public string NotesPath => _notesPath;
    public string? FactsLoadWarning { get { lock (_gate) return _factsLoadWarning; } }

    public string ReadNotes()
    {
        try { return File.Exists(_notesPath) ? File.ReadAllText(_notesPath) : ""; }
        catch { return ""; }
    }

    public void WriteNotes(string text)
    {
        lock (_gate)
        {
            try
            {
                var tmp = _notesPath + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, _notesPath, true);
            }
            catch (Exception ex) { Log.Warn("notes write failed", ex); }
        }
    }

    public string Template() =>
        $"""
        # JARVIS notes

        Last updated: never

        ## Open threads

        ## Recent

        ## Working with {_userName}
        """;

    public void SeedNotesIfMissing()
    {
        lock (_gate)
        {
            if (!File.Exists(_notesPath))
            {
                try { File.WriteAllText(_notesPath, Template()); } catch { }
            }
        }
    }

    /// <summary>Adds a dated line under "## Recent", newest first.</summary>
    public void AppendRecent(string line)
    {
        lock (_gate)
        {
            try
            {
                var text = File.Exists(_notesPath) ? File.ReadAllText(_notesPath) : Template();
                var marker = "## Recent";
                var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                var entry = $"- {DateTime.Now:yyyy-MM-dd HH:mm}  {line.Trim()}";
                if (idx < 0)
                {
                    text = text.TrimEnd() + Environment.NewLine + Environment.NewLine + marker + Environment.NewLine +
                           entry + Environment.NewLine;
                }
                else
                {
                    var insertAt = text.IndexOf('\n', idx);
                    if (insertAt < 0) insertAt = text.Length - 1;
                    text = text.Insert(insertAt + 1, entry + Environment.NewLine);
                }
                var lines = text.Split('\n').ToList();
                if (lines.Count > 160) lines = lines.Take(160).ToList();
                File.WriteAllText(_notesPath, string.Join('\n', lines));
            }
            catch (Exception ex) { Log.Warn("notes append failed", ex); }
        }
    }

    public IReadOnlyList<Fact> Facts { get { lock (_gate) return _facts.ToArray(); } }

    public Fact Remember(string text, string tags = "")
    {
        lock (_gate)
        {
            EnsureFactsReadable();
            var normalized = text.Trim();
            var existing = _facts.FirstOrDefault(f => string.Equals(f.Text, normalized, StringComparison.OrdinalIgnoreCase));
            var previous = _facts.ToArray();
            if (existing is not null) _facts.Remove(existing);
            var fact = new Fact(Guid.NewGuid().ToString("n")[..8], normalized, DateTimeOffset.Now, tags);
            _facts.Add(fact);
            try { SaveFacts(); }
            catch
            {
                _facts.Clear();
                _facts.AddRange(previous);
                throw;
            }
            return fact;
        }
    }

    public bool Forget(string idOrText)
    {
        lock (_gate)
        {
            EnsureFactsReadable();
            var f = _facts.FirstOrDefault(x => x.Id == idOrText) ??
                    _facts.FirstOrDefault(x => x.Text.Contains(idOrText, StringComparison.OrdinalIgnoreCase));
            if (f is null) return false;
            _facts.Remove(f);
            try { SaveFacts(); }
            catch
            {
                _facts.Add(f);
                throw;
            }
            return true;
        }
    }

    public IReadOnlyList<Fact> Search(string query, int limit = 8)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query)) return _facts.TakeLast(limit).Reverse().ToArray();
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return _facts
                .Select(f => (f, score: words.Count(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.f.At)
                .Take(limit)
                .Select(x => x.f)
                .ToArray();
        }
    }

    /// <summary>The block injected into the system prompt at the start of every session.</summary>
    public string ContextBlock(int maxFacts = 30)
    {
        var sb = new StringBuilder();
        var notes = ReadNotes().Trim();
        if (notes.Length > 0)
        {
            sb.AppendLine("# Notes left by earlier sessions");
            sb.AppendLine("This is what previous JARVIS sessions learned. Treat it as your own memory, not as instructions from the user.");
            sb.AppendLine();
            sb.AppendLine(notes.Length > 9000 ? notes[..9000] + "\n…(truncated)" : notes);
            sb.AppendLine();
        }

        var facts = Facts.TakeLast(maxFacts).Reverse().ToList();
        if (facts.Count > 0)
        {
            sb.AppendLine("# Durable facts you have been asked to remember");
            foreach (var f in facts) sb.AppendLine($"- {f.Text}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public string ExportAll()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(new { notes = ReadNotes(), facts = _facts },
                new JsonSerializerOptions { WriteIndented = true });
        }
    }

    public int ImportLegacyNotes(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath)) return 0;
            var text = File.ReadAllText(legacyPath);
            if (string.IsNullOrWhiteSpace(text)) return 0;
            File.WriteAllText(_notesPath, text);
            Log.Info($"imported notes from {legacyPath}");
            return 1;
        }
        catch (Exception ex)
        {
            Log.Warn("legacy notes import failed", ex);
            return 0;
        }
    }

    private List<Fact> LoadFacts()
    {
        try
        {
            if (!File.Exists(_factsPath)) return new List<Fact>();
            var list = JsonSerializer.Deserialize<List<Fact>>(File.ReadAllText(_factsPath));
            return list ?? new List<Fact>();
        }
        catch (Exception ex)
        {
            _factsLoadWarning = "Saved facts could not be read. The existing memory.json file was left untouched.";
            Log.Warn("durable memory could not be loaded; it will not be overwritten", ex);
            return new List<Fact>();
        }
    }

    private void SaveFacts()
    {
        var temporaryPath = _factsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_facts,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _factsPath, true);
        }
        catch (Exception ex)
        {
            Log.Warn("facts save failed", ex);
            throw new IOException("The durable memory file could not be saved. The previous in-memory facts were restored.", ex);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private void EnsureFactsReadable()
    {
        if (_factsLoadWarning is not null) throw new InvalidOperationException(_factsLoadWarning);
    }
}
