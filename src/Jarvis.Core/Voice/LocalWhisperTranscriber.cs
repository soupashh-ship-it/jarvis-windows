using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Jarvis.Core;

/// <summary>Downloads a multilingual model once, then reuses it for local voice transcription.</summary>
internal sealed class LocalWhisperTranscriber : IDisposable
{
    private const string PreferredModelFileName = "ggml-small.bin";
    private const string ExistingModelFileName = "ggml-base.bin";
    private const string TranscriptionLanguage = "en";
    private const float NoSpeechProbabilityThreshold = 0.6f;
    private const long MinimumPreferredModelBytes = 300_000_000;
    private const long MinimumExistingModelBytes = 50_000_000;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task<WhisperSession>? _loadTask;
    private WhisperSession? _session;
    private bool _disposed;

    public event Action<string>? StatusChanged;

    public async Task PrepareAsync(CancellationToken ct = default)
    {
        _ = await GetSessionAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        if (samples.Length == 0) return "";

        var session = await GetSessionAsync(ct).ConfigureAwait(false);
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var transcript = new StringBuilder();
            await foreach (var segment in session.Processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                // Whisper can hallucinate text over silence and room noise. Use its per-segment
                // no-speech score so those guesses never become Jarvis prompts.
                if (segment.NoSpeechProbability >= NoSpeechProbabilityThreshold) continue;
                transcript.Append(segment.Text);
            }
            return transcript.ToString().Trim();
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private async Task<WhisperSession> GetSessionAsync(CancellationToken ct)
    {
        Task<WhisperSession> loading;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loadTask is { IsFaulted: true } or { IsCanceled: true }) _loadTask = null;
            _loadTask ??= Task.Run(() => LoadSessionAsync(_shutdown.Token));
            loading = _loadTask;
        }

        return await loading.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<WhisperSession> LoadSessionAsync(CancellationToken ct)
    {
        WhisperSession? session = null;
        try
        {
            var directory = Path.Combine(Paths.Root, "models");
            Directory.CreateDirectory(directory);
            var preferredPath = Path.Combine(directory, PreferredModelFileName);
            var existingPath = Path.Combine(directory, ExistingModelFileName);
            var candidates = new List<string>();

            try
            {
                await EnsureModelAsync(preferredPath, GgmlType.Small, MinimumPreferredModelBytes, ct)
                    .ConfigureAwait(false);
                candidates.Add(preferredPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Warn("higher-accuracy speech model could not be prepared", ex);
                if (IsUsableModel(existingPath, MinimumExistingModelBytes))
                    SetStatus("Using the installed speech model while the higher-accuracy model is unavailable.");
            }

            if (IsUsableModel(existingPath, MinimumExistingModelBytes) && !candidates.Contains(existingPath))
                candidates.Add(existingPath);
            if (candidates.Count == 0)
                throw new InvalidDataException("No usable local speech model is available.");

            Exception? lastLoadError = null;
            foreach (var modelPath in candidates)
            {
                try
                {
                    SetStatus(modelPath == preferredPath
                        ? "Loading the higher-accuracy local speech model..."
                        : "Loading the installed local speech model...");
                    session = await Task.Run(() => CreateSession(modelPath), ct).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastLoadError = ex;
                    Log.Warn($"local speech model could not be loaded: {Path.GetFileName(modelPath)}", ex);
                }
            }

            if (session is null)
                throw new InvalidDataException("No local speech model could be loaded.", lastLoadError);

            lock (_gate)
            {
                if (_disposed)
                {
                    session.Dispose();
                    session = null;
                    throw new OperationCanceledException("Speech transcription is shutting down.", ct);
                }
                _session = session;
            }

            SetStatus(session.ModelName == PreferredModelFileName
                ? "Higher-accuracy local speech model ready."
                : "Local speech model ready.");
            return session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            Log.Warn("local speech model unavailable", ex);
            SetStatus("Local speech transcription failed: " + Trim(ex.Message, 160));
            throw;
        }
    }

    private static bool IsUsableModel(string path, long minimumBytes) =>
        File.Exists(path) && new FileInfo(path).Length >= minimumBytes;

    private async Task EnsureModelAsync(string modelPath, GgmlType type, long minimumBytes,
        CancellationToken ct)
    {
        if (IsUsableModel(modelPath, minimumBytes)) return;

        SetStatus("Downloading the higher-accuracy multilingual speech model. This is a one-time download.");
        var temporaryPath = modelPath + ".download";
        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(type, cancellationToken: ct)
                .ConfigureAwait(false);
            await using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                await modelStream.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length < minimumBytes)
                throw new InvalidDataException("The downloaded speech model is incomplete.");
            File.Move(temporaryPath, modelPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static WhisperSession CreateSession(string modelPath)
    {
        var factory = WhisperFactory.FromPath(modelPath);
        try
        {
            var processor = factory.CreateBuilder()
                // The wake grammar and the app's voice UI are English. Automatic detection was
                // turning short English commands into unrelated Devanagari text, so pin dictation
                // to English too.
                .WithLanguage(TranscriptionLanguage)
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .Build();
            return new WhisperSession(factory, processor, Path.GetFileName(modelPath));
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private void SetStatus(string status)
    {
        try { StatusChanged?.Invoke(status); } catch { }
    }

    private static string Trim(string? text, int max)
    {
        var clean = (text ?? "Speech model unavailable.").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }

    public void Dispose()
    {
        WhisperSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        try { _shutdown.Cancel(); } catch { }
        if (session is null) return;

        if (_inferenceGate.Wait(TimeSpan.FromSeconds(2)))
        {
            try { session.Dispose(); }
            finally { _inferenceGate.Release(); }
        }
        else
        {
            _ = Task.Run(async () =>
            {
                await _inferenceGate.WaitAsync().ConfigureAwait(false);
                try { session.Dispose(); }
                finally { _inferenceGate.Release(); }
            });
        }
    }

    private sealed class WhisperSession(WhisperFactory factory, WhisperProcessor processor, string modelName) : IDisposable
    {
        public WhisperProcessor Processor { get; } = processor;
        public string ModelName { get; } = modelName;

        public void Dispose()
        {
            Processor.Dispose();
            factory.Dispose();
        }
    }
}
