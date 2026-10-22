using System.Text;

namespace Jarvis.Core;

/// <summary>Thread-safe rolling text log plus an event for the UI log view.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "logs");
    private static string? _file;

    public static string Directory => _dir;
    public static bool Verbose { get; set; }
    public static event Action<string>? Line;

    public static void Init(string? dir = null)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(dir)) _dir = dir!;
            System.IO.Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, $"jarvis-{DateTime.Now:yyyyMMdd}.log");
        }
    }

    public static void Debug(string message) { if (Verbose) Write("DEBUG", message, null); }
    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        lock (Gate)
        {
            try
            {
                if (_file is null) Init();
                File.AppendAllText(_file!, line + Environment.NewLine, Encoding.UTF8);
                // Keep one log per day, prune anything older than 14 days.
                foreach (var old in System.IO.Directory.EnumerateFiles(_dir, "jarvis-*.log"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-14)) File.Delete(old);
            }
            catch { /* logging must never take the assistant down */ }
        }
        try { Line?.Invoke(line); } catch { }
    }
}
