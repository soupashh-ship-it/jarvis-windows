using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Buffers.Binary;
using System.Threading.Channels;
using NanoWakeWord;

namespace Jarvis.Core;

/// <summary>
/// Uses an offline Hey Jarvis detector with the Windows grammar as a fallback, then records dictation
/// directly from the selected microphone and transcribes completed speech locally with Whisper.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    private const int WakeAudioWindowBytes = 16_000 * 2 * 2; // two seconds of mono PCM16
    private const int WakeFrameSamples = 512;
    private const int WakeFrameBytes = WakeFrameSamples * sizeof(short);
    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly object _reloadGate = new();
    private readonly LocalWhisperTranscriber _transcriber = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Channel<short[]> _wakeFrames = Channel.CreateBounded<short[]>(new BoundedChannelOptions(32)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private SpeechRecognitionEngine? _engine;
    private Grammar? _wakeGrammar;
    private MicrophoneCapture? _mic;
    private MicrophoneStream? _stream;
    private UtteranceRecorder? _capture;
    private CancellationTokenSource? _captureCts;
    private string _deviceLabel = "Windows default";
    private DateTime _lastWake = DateTime.MinValue;
    private DateTime _suppressWakeUntil = DateTime.MinValue;
    private readonly System.Threading.Timer _decay;
    private float _level;
    private volatile bool _disposed;
    private volatile bool _reloading;
    private volatile bool _recognizing;
    private bool _started;
    private bool _muted;
    private DateTime _lastRestartAttempt = DateTime.MinValue;
    private DateTime _lastMicRestartAttempt = DateTime.MinValue;
    private DateTime _silentSince = DateTime.MinValue;
    private bool _silenceWarned;
    private int _micRecoveryRunning;
    private readonly Queue<byte[]> _recentAudio = new();
    private readonly Queue<byte[]> _wakeAudio = new();
    private int _recentAudioBytes;
    private int _wakeAudioBytes;
    private bool _wakeCapturePending;
    private readonly byte[] _wakeFrameBuffer = new byte[WakeFrameBytes];
    private int _wakeFrameBytes;
    private Task? _wakeDetectorTask;
    private WakeWordConfig? _wakeWordConfig;
    private volatile bool _wakeWordReady;
    private readonly object _wakeDiagnosticsGate = new();
    private DateTime _lastWakeDiagnostic = DateTime.MinValue;
    private float _lastWakeInputRms;
    private float _wakeDiagnosticPeakScore;
    private float _wakeDiagnosticPeakInput;
    private float _wakeDiagnosticPeakRms;

    public SpeechInput(AppSettings settings)
    {
        _settings = settings;
        _transcriber.StatusChanged += SetStatus;
        _decay = new System.Threading.Timer(_ =>
        {
            if (_engine is not null && _recognizing)
            {
                var current = Volatile.Read(ref _level);
                if (current > 0.02f) Volatile.Write(ref _level, current * 0.82f);
            }
            CheckForSilence();
        }, null, 100, 100);
    }

    public bool Available => _mic?.Running == true;
    public bool Muted
    {
        get => _muted;
        set { _muted = value; SetReadyStatus(); }
    }
    public string Status { get; private set; } = "not started";
    public string Culture { get; private set; } = "";
    public string DeviceLabel => _deviceLabel;

    /// <summary>The microphone's own level where possible, so the meter shows what the mic hears.</summary>
    public float Level
    {
        get
        {
            var heard = Volatile.Read(ref _level);
            var mic = _mic;
            return mic is not null && mic.Level > heard ? mic.Level : heard;
        }
    }

    public bool IsCapturing { get { lock (_gate) return _capture is not null; } }

    /// <summary>The orchestrator sets this while the assistant is speaking, to make self-wake less likely.</summary>
    public bool SpeakingHint { get; set; }

    public event Action<string>? WakeDetected;
    public event Action? SpeechStarted;
    public event Action<string>? Hypothesis;
    public event Action<string>? Heard;
    public event Action<string>? StatusChanged;

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        StartDevice();
        if (!Available)
        {
            _started = false;
            return;
        }

        StartWakeWordDetector();
        StartWakeRecognizer();
        SetReadyStatus();
        _ = PrepareWhisperAsync();
    }

    /// <summary>Restarts the microphone and wake listeners so a newly chosen device takes effect immediately.</summary>
    public void Reload()
    {
        lock (_reloadGate)
        {
            if (_disposed) return;
            _reloading = true;
            try
            {
                CancelCapture();
                lock (_gate)
                {
                    ClearAudioQueue(_recentAudio, ref _recentAudioBytes);
                    ClearAudioQueue(_wakeAudio, ref _wakeAudioBytes);
                    _wakeCapturePending = false;
                    _wakeFrameBytes = 0;
                    while (_wakeFrames.Reader.TryRead(out _)) { }
                }
                _recognizing = false;
                Volatile.Write(ref _level, 0f);
                var engine = _engine;
                _engine = null;
                try { engine?.RecognizeAsyncCancel(); } catch { }
                try { engine?.Dispose(); } catch { }
                _wakeGrammar = null;
                _stream = null;
                StopDevice();
                _started = false;
            }
            finally { _reloading = false; }
            Start();
        }
    }

    private async Task PrepareWhisperAsync()
    {
        try
        {
            await _transcriber.PrepareAsync(_shutdown.Token).ConfigureAwait(false);
            if (!_disposed) SetReadyStatus();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { Log.Debug("local speech model preparation failed: " + ex.Message); }
    }

    private void StartDevice()
    {
        var mic = new MicrophoneCapture
        {
            Gain = Math.Clamp(_settings.MicGain, 0.25, 8.0)
        };
        try
        {
            mic.Data += OnMicData;
            mic.StoppedUnexpectedly += OnMicrophoneStopped;
            _stream = new MicrophoneStream();
            _mic = mic;
            mic.Start(_settings.MicDevice);
            _deviceLabel = mic.DeviceName;
            _silentSince = DateTime.MinValue;
            _silenceWarned = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"microphone '{AudioDevices.InputName(_settings.MicDevice)}' could not be opened", ex);
            try { mic.Data -= OnMicData; } catch { }
            try { mic.StoppedUnexpectedly -= OnMicrophoneStopped; } catch { }
            try { mic.Dispose(); } catch { }
            if (ReferenceEquals(_mic, mic)) _mic = null;
            _stream = null;
            _deviceLabel = "Windows default";
            SetStatus("Voice input could not open the microphone: " + ex.Message);
        }
    }

    private void StartWakeRecognizer()
    {
        SpeechRecognitionEngine? engine = null;
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var info = installed.FirstOrDefault(r => r.Culture.Name.Equals("en-GB", StringComparison.OrdinalIgnoreCase))
                       ?? installed.FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                       ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                       ?? installed.FirstOrDefault();

            if (info is null)
            {
                SetStatus("Microphone ready; no Windows wake recognizer is installed. Use Talk or the voice hotkey.");
                return;
            }

            Culture = info.Culture.Name;
            engine = new SpeechRecognitionEngine(info);
            ApplyInput(engine);

            var phrases = new Choices(WakePhrases(_settings));
            var builder = new GrammarBuilder(phrases) { Culture = info.Culture };
            _wakeGrammar = new Grammar(builder) { Name = "wake" };
            engine.LoadGrammar(_wakeGrammar);
            engine.SpeechDetected += OnSpeechDetected;
            engine.SpeechRecognized += OnSpeechRecognized;
            engine.RecognizeCompleted += OnRecognizeCompleted;
            _engine = engine;
            engine.RecognizeAsync(RecognizeMode.Multiple);
            _recognizing = true;
        }
        catch (Exception ex)
        {
            Log.Warn("wake phrase recognition unavailable", ex);
            try { engine?.Dispose(); } catch { }
            _engine = null;
            _wakeGrammar = null;
            _recognizing = false;
            SetStatus("Microphone ready; wake phrase recognition failed. Use Talk or the voice hotkey.");
        }
    }

    /// <summary>Feeds the live microphone to the wake listeners. Dictation uses the same PCM in the recorder.</summary>
    private void ApplyInput(SpeechRecognitionEngine engine)
    {
        _stream = new MicrophoneStream();
        engine.SetInputToAudioStream(_stream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
    }

    private void OnMicData(byte[] buffer, int count)
    {
        if (count <= 0) return;
        byte[] chunk;
        UtteranceRecorder? capture;
        lock (_gate)
        {
            chunk = new byte[count];
            Buffer.BlockCopy(buffer, 0, chunk, 0, count);
            EnqueueAudio(_recentAudio, ref _recentAudioBytes, chunk);

            if (_wakeCapturePending)
            {
                if (DateTime.UtcNow - _lastWake > TimeSpan.FromSeconds(3))
                {
                    ClearAudioQueue(_wakeAudio, ref _wakeAudioBytes);
                    _wakeCapturePending = false;
                }
                else
                {
                    EnqueueAudio(_wakeAudio, ref _wakeAudioBytes, chunk);
                }
            }
            QueueWakeFramesLocked(buffer, count);
            capture = _capture;
        }

        _stream?.Push(buffer, count);
        capture?.Push(buffer, count);
    }

    private void QueueWakeFramesLocked(byte[] buffer, int count)
    {
        if (_wakeDetectorTask is null || Volatile.Read(ref _wakeWordConfig) is null || _disposed || _reloading ||
            !IsDefaultWakePhrase()) return;
        var offset = 0;
        while (offset < count)
        {
            var copy = Math.Min(WakeFrameBytes - _wakeFrameBytes, count - offset);
            Buffer.BlockCopy(buffer, offset, _wakeFrameBuffer, _wakeFrameBytes, copy);
            offset += copy;
            _wakeFrameBytes += copy;
            if (_wakeFrameBytes < WakeFrameBytes) continue;

            var frame = new short[WakeFrameSamples];
            long sumSquares = 0;
            var peak = 0;
            for (var i = 0; i < frame.Length; i++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(_wakeFrameBuffer.AsSpan(i * sizeof(short), sizeof(short)));
                frame[i] = sample;
                var magnitude = Math.Abs((int)sample);
                if (magnitude > peak) peak = magnitude;
                sumSquares += (long)sample * sample;
            }
            Volatile.Write(ref _lastWakeInputRms, (float)(Math.Sqrt(sumSquares / (double)frame.Length) / 32768.0));
            lock (_wakeDiagnosticsGate)
            {
                _wakeDiagnosticPeakInput = Math.Max(_wakeDiagnosticPeakInput, peak / 32768f);
                _wakeDiagnosticPeakRms = Math.Max(_wakeDiagnosticPeakRms, Volatile.Read(ref _lastWakeInputRms));
            }

            var config = Volatile.Read(ref _wakeWordConfig);
            if (config is not null)
                config.Threshold = SpeakingHint
                    ? 0.85f
                    : (float)Math.Clamp(_settings.WakeConfidence, 0.01, 0.95);
            _wakeFrames.Writer.TryWrite(frame);
            _wakeFrameBytes = 0;
        }
    }

    private void StartWakeWordDetector()
    {
        if (_wakeDetectorTask is not null) return;
        if (!string.Equals((_settings.WakePhrase ?? "").Trim(), "hey jarvis", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Offline Hey Jarvis detector skipped because a custom wake phrase is configured.");
            return;
        }
        _wakeDetectorTask = Task.Run(RunWakeWordDetectorAsync);
    }

    private async Task RunWakeWordDetectorAsync()
    {
        WakeWordRuntime? runtime = null;
        try
        {
            var config = new WakeWordConfig
            {
                Model = "hey_jarvis_v0.1",
                Threshold = (float)Math.Clamp(_settings.WakeConfidence, 0.01, 0.95),
                // The package default requires four consecutive threshold crossings. The model
                // produces brief peaks on natural speech, so that setting suppresses real wakes.
                TriggerLevel = 1,
                Refractory = 20
            };
            Volatile.Write(ref _wakeWordConfig, config);
            runtime = new WakeWordRuntime(new WakeWordRuntimeConfig
            {
                WakeWords = [config],
                DebugAction = LogWakeDiagnostics
            });
            _wakeWordReady = true;
            Log.Info("Offline Hey Jarvis wake detector is ready.");
            if (!_disposed && !_reloading) SetReadyStatus();

            while (await _wakeFrames.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_wakeFrames.Reader.TryRead(out var frame))
                {
                    if (runtime.Process(frame) >= 0 && !Muted && IsDefaultWakePhrase())
                        HandleWake("hey jarvis", null);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _wakeWordReady = false;
            Volatile.Write(ref _wakeWordConfig, null);
            Log.Warn("offline Hey Jarvis wake detector failed; keeping the Windows grammar fallback", ex);
            if (!_disposed && !_reloading)
                SetStatus($"Offline wake detector unavailable. {(_engine is null ? "Use Talk or the voice hotkey." : "Windows wake recognition remains active.")}");
        }
        finally
        {
            _wakeWordReady = false;
            Volatile.Write(ref _wakeWordConfig, null);
            try { runtime?.Dispose(); } catch { }
        }
    }

    /// <summary>Logs only periodic model scores and signal levels; microphone audio is never written to disk.</summary>
    private void LogWakeDiagnostics(string model, float probability, bool detected)
    {
        var now = DateTime.UtcNow;
        float peakScore;
        float peakInput;
        float peakRms;
        float threshold;
        lock (_wakeDiagnosticsGate)
        {
            _wakeDiagnosticPeakScore = Math.Max(_wakeDiagnosticPeakScore, probability);
            if (!detected && now - _lastWakeDiagnostic < TimeSpan.FromSeconds(10)) return;

            peakScore = _wakeDiagnosticPeakScore;
            peakInput = _wakeDiagnosticPeakInput;
            peakRms = _wakeDiagnosticPeakRms;
            _wakeDiagnosticPeakScore = 0f;
            _wakeDiagnosticPeakInput = 0f;
            _wakeDiagnosticPeakRms = 0f;
            _lastWakeDiagnostic = now;
            threshold = Volatile.Read(ref _wakeWordConfig)?.Threshold ?? 0f;
        }
        Log.Info($"wake monitor: model={model}, score={probability:0.000}, peak score={peakScore:0.000}, " +
                 $"detected={detected}, threshold={threshold:0.00}, mic peak={peakInput:0.000}, rms={peakRms:0.000}");
    }

    private static void EnqueueAudio(Queue<byte[]> queue, ref int queuedBytes, byte[] chunk)
    {
        queue.Enqueue(chunk);
        queuedBytes += chunk.Length;
        while (queuedBytes > WakeAudioWindowBytes && queue.Count > 1)
            queuedBytes -= queue.Dequeue().Length;

        if (queuedBytes > WakeAudioWindowBytes && queue.Count == 1)
        {
            var tail = queue.Dequeue();
            var retained = new byte[WakeAudioWindowBytes];
            Buffer.BlockCopy(tail, tail.Length - retained.Length, retained, 0, retained.Length);
            queue.Enqueue(retained);
            queuedBytes = retained.Length;
        }
    }

    private static void ClearAudioQueue(Queue<byte[]> queue, ref int queuedBytes)
    {
        queue.Clear();
        queuedBytes = 0;
    }

    private static byte[] JoinAudio(Queue<byte[]> queue, int length)
    {
        if (length <= 0) return Array.Empty<byte>();
        var joined = new byte[length];
        var offset = 0;
        foreach (var chunk in queue)
        {
            var copy = Math.Min(chunk.Length, length - offset);
            if (copy <= 0) break;
            Buffer.BlockCopy(chunk, 0, joined, offset, copy);
            offset += copy;
        }
        return offset == joined.Length ? joined : joined[..offset];
    }

    private void StopDevice()
    {
        var mic = _mic;
        _mic = null;
        if (mic is not null)
        {
            try { mic.Data -= OnMicData; } catch { }
            try { mic.StoppedUnexpectedly -= OnMicrophoneStopped; } catch { }
            try { mic.Dispose(); } catch { }
        }
        _stream = null;
    }

    private void OnMicrophoneStopped(Exception? error)
    {
        if (_disposed || _reloading) return;
        var now = DateTime.UtcNow;
        if (now - _lastMicRestartAttempt < TimeSpan.FromSeconds(3)) return;
        _lastMicRestartAttempt = now;
        if (Interlocked.CompareExchange(ref _micRecoveryRunning, 1, 0) != 0) return;

        SetStatus("Microphone stopped unexpectedly. Trying to reconnect it.");
        if (error is not null) Log.Warn("microphone capture stopped unexpectedly; reconnecting", error);
        _ = Task.Run(RecoverMicrophoneAsync);
    }

    private async Task RecoverMicrophoneAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 5 && !_disposed; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), _shutdown.Token)
                    .ConfigureAwait(false);
                if (_disposed) return;
                try { Reload(); }
                catch (Exception ex) { Log.Warn("microphone recovery attempt failed", ex); }
                if (Available) return;
            }

            if (!_disposed)
                SetStatus("Microphone is unavailable. Check the selected device in Settings or use Talk/the voice hotkey.");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { Interlocked.Exchange(ref _micRecoveryRunning, 0); }
    }

    private string DeviceSuffix()
    {
        if (_deviceLabel.Length == 0 || _deviceLabel == "Windows default") return "";
        var name = _deviceLabel.Length > 34 ? _deviceLabel[..34] + "..." : _deviceLabel;
        return $" - {name}";
    }

    private void SetReadyStatus()
    {
        if (!Available) return;
        if (Muted)
        {
            SetStatus($"Microphone ready{DeviceSuffix()}; wake phrase is off.");
            return;
        }
        var sources = new List<string>();
        if (_wakeWordReady && IsDefaultWakePhrase()) sources.Add("offline detector");
        if (_engine is not null) sources.Add(Culture);
        if (sources.Count == 0)
        {
            SetStatus($"Microphone ready{DeviceSuffix()}; use Talk or the voice hotkey. Wake phrase unavailable.");
            return;
        }
        SetStatus($"Listening for \"{_settings.WakePhrase}\" ({string.Join(" + ", sources)}{DeviceSuffix()}).");
    }

    /// <summary>
    /// Warns once when the chosen microphone is not passing any sound at all. A virtual device
    /// (SteelSeries Sonar, VoiceMeeter, a phone bridge) is silent until its app routes the mic.
    /// </summary>
    private void CheckForSilence()
    {
        var mic = _mic;
        if (mic is null) return;
        if (mic.Level > 0.008f)
        {
            var clearSilentWarning = _silenceWarned &&
                                     Status.StartsWith("No sound is reaching", StringComparison.Ordinal) &&
                                     !IsCapturing;
            _silentSince = DateTime.MinValue;
            _silenceWarned = false;
            if (clearSilentWarning && !_disposed && !_reloading) SetReadyStatus();
            return;
        }
        if (_silentSince == DateTime.MinValue)
        {
            _silentSince = DateTime.UtcNow;
            return;
        }
        if (_silenceWarned || DateTime.UtcNow - _silentSince < TimeSpan.FromSeconds(10)) return;
        _silenceWarned = true;
        SetStatus($"No sound is reaching '{_deviceLabel}'. Check it in Settings, or pick another microphone.");
    }

    /// <summary>
    /// The configured phrase plus its bare final word, so "hey jarvis" also answers to "jarvis".
    /// The stock friendly variants are kept when the phrase has not been customised.
    /// </summary>
    private static string[] WakePhrases(AppSettings settings)
    {
        var phrase = string.IsNullOrWhiteSpace(settings.WakePhrase)
            ? "hey jarvis"
            : settings.WakePhrase.Trim().ToLowerInvariant();
        var list = new List<string> { phrase };
        var bare = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (bare is { Length: > 2 }) list.Add(bare);
        if (phrase == "hey jarvis") list.AddRange(new[] { "okay jarvis", "ok jarvis" });
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        Volatile.Write(ref _level, 0.9f);
        if (SpeakingHint && !IsCapturing && DateTime.UtcNow >= _suppressWakeUntil)
        {
            try { SpeechStarted?.Invoke(); } catch { }
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = (e.Result.Text ?? "").Trim();
        if (text.Length == 0 || !string.Equals(e.Result.Grammar?.Name, "wake", StringComparison.Ordinal)) return;
        if (Muted) return;

        var threshold = SpeakingHint ? 0.85f : (float)Math.Clamp(_settings.WakeConfidence, 0.01, 0.95);
        var phrase = string.IsNullOrWhiteSpace(_settings.WakePhrase) ? "hey jarvis" : _settings.WakePhrase.Trim();
        var bare = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(bare) &&
            !string.Equals(text, phrase, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(text, bare, StringComparison.OrdinalIgnoreCase))
            threshold = Math.Max(0.01f, threshold * 0.85f);
        if (e.Result.Confidence < threshold) return;
        HandleWake(text, e.Result.Confidence);
    }

    private void HandleWake(string text, float? confidence)
    {
        if (Muted || _disposed || _reloading) return;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_capture is not null || now < _suppressWakeUntil) return;
            if ((now - _lastWake).TotalMilliseconds < 1800) return;
            _lastWake = now;

            // Keep recent audio so a command spoken as "Hey Jarvis, ..." is still present when dictation starts.
            ClearAudioQueue(_wakeAudio, ref _wakeAudioBytes);
            if (_capture is null)
            {
                foreach (var chunk in _recentAudio)
                    EnqueueAudio(_wakeAudio, ref _wakeAudioBytes, chunk);
                _wakeCapturePending = _wakeAudioBytes > 0;
            }
        }
        Volatile.Write(ref _level, 1f);
        Log.Info(confidence is { } score ? $"wake word: {text} ({score:0.00})" : $"wake word: {text} (offline detector)");
        try { WakeDetected?.Invoke(text); } catch { }
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (_disposed || _reloading || !ReferenceEquals(sender, _engine)) return;
        _recognizing = false;
        if (e.Error is not null) Log.Warn("wake recognizer stopped: " + e.Error.Message);
        if (DateTime.UtcNow - _lastRestartAttempt < TimeSpan.FromSeconds(3)) return;
        _lastRestartAttempt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            await Task.Delay(400).ConfigureAwait(false);
            try
            {
                if (!_disposed && !_reloading && _engine is not null && !_recognizing)
                {
                    ApplyInput(_engine);
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                    _recognizing = true;
                    SetReadyStatus();
                }
            }
            catch (Exception ex) { Log.Debug("wake recognizer restart failed: " + ex.Message); }
        });
    }

    /// <summary>Records one VAD-delimited utterance, transcribes it locally, and returns text to Jarvis.</summary>
    public async Task<string?> CaptureAsync(TimeSpan firstSpeechWindow, CancellationToken ct)
    {
        if (!Available) return null;
        try { await _captureGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }

        var recorder = new UtteranceRecorder(_settings.EndSilenceSeconds, _settings.MaxUtteranceSeconds);
        using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var wokeCapture = false;
        lock (_gate)
        {
            if (_wakeCapturePending && DateTime.UtcNow - _lastWake <= TimeSpan.FromSeconds(3))
            {
                var preRoll = JoinAudio(_wakeAudio, _wakeAudioBytes);
                if (preRoll.Length > 0)
                {
                    recorder.Push(preRoll, preRoll.Length);
                    wokeCapture = true;
                }
            }
            ClearAudioQueue(_wakeAudio, ref _wakeAudioBytes);
            _wakeCapturePending = false;
            _capture = recorder;
            _captureCts = captureCts;
        }

        var restoreReadyStatus = true;
        try
        {
            SetStatus("Listening. Speak now.");
            var recording = recorder.WaitAsync(
                TimeSpan.FromSeconds(Math.Clamp(firstSpeechWindow.TotalSeconds, 0.1, 60)), captureCts.Token);
            var first = await Task.WhenAny(recorder.SpeechStarted, recording).ConfigureAwait(false);
            if (first == recorder.SpeechStarted && !captureCts.IsCancellationRequested)
            {
                Volatile.Write(ref _level, 0.9f);
                try { SpeechStarted?.Invoke(); } catch { }
            }

            var samples = await recording.ConfigureAwait(false);
            if (captureCts.IsCancellationRequested) return null;
            if (samples is null)
            {
                SetStatus("No speech detected. " + ReadyStatusText());
                restoreReadyStatus = false;
                return null;
            }

            SetStatus("Transcribing your speech locally...");
            var text = await _transcriber.TranscribeAsync(samples, captureCts.Token).ConfigureAwait(false);
            if (captureCts.IsCancellationRequested) return null;
            if (wokeCapture) text = StripWakePhrase(text);
            if (IsNonSpeechMarker(text))
            {
                SetStatus("No speech was detected. Please try again.");
                restoreReadyStatus = false;
                return null;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(wokeCapture
                    ? "I heard the wake phrase, but not a request. Say your request after the wake phrase."
                    : "I heard audio but could not make out words. Please try again.");
                restoreReadyStatus = false;
                return null;
            }

            try { Hypothesis?.Invoke(text); } catch { }
            try { Heard?.Invoke(text); } catch { }
            restoreReadyStatus = false;
            SetReadyStatus();
            return text;
        }
        catch (OperationCanceledException) when (captureCts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("voice transcription failed", ex);
            SetStatus("Voice transcription failed: " + ex.Message);
            restoreReadyStatus = false;
            return null;
        }
        finally
        {
            recorder.Cancel();
            lock (_gate)
            {
                if (ReferenceEquals(_capture, recorder)) _capture = null;
                if (ReferenceEquals(_captureCts, captureCts)) _captureCts = null;
            }
            // Let wake detection drain the captured command before accepting another wake.
            _suppressWakeUntil = DateTime.UtcNow.AddMilliseconds(1800);
            _captureGate.Release();
            if (restoreReadyStatus && !_disposed) SetReadyStatus();
        }
    }

    private static bool IsNonSpeechMarker(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text,
            @"^\s*\[\s*(?:BLANK[_ ]AUDIO|INAUDIBLE|UNINTELLIGIBLE|SILENCE)\s*\]\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    private string ReadyStatusText()
    {
        if (_engine is null && (!_wakeWordReady || !IsDefaultWakePhrase()))
            return $"use Talk or the voice hotkey{DeviceSuffix()}.";
        return Muted ? $"the wake phrase is off{DeviceSuffix()}." : $"say \"{_settings.WakePhrase}\" when ready.";
    }

    private bool IsDefaultWakePhrase() =>
        string.Equals((_settings.WakePhrase ?? "").Trim(), "hey jarvis", StringComparison.OrdinalIgnoreCase);

    private string StripWakePhrase(string text)
    {
        foreach (var phrase in WakePhrases(_settings).OrderByDescending(p => p.Length))
        {
            var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = @"^\s*" + string.Join(@"[\s,]+", words.Select(System.Text.RegularExpressions.Regex.Escape)) +
                          @"\b[\s,.:;!?—-]*";
            var stripped = System.Text.RegularExpressions.Regex.Replace(text, pattern, "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)).Trim();
            if (stripped.Length < text.Trim().Length) return stripped;
        }
        return text.Trim();
    }

    /// <summary>Drop a capture that is no longer wanted (e.g. the user answered on screen).</summary>
    public void CancelCapture()
    {
        lock (_gate)
        {
            try { _captureCts?.Cancel(); } catch { }
            _capture?.Cancel();
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        try { StatusChanged?.Invoke(status); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recognizing = false;
        try { _decay.Dispose(); } catch { }
        CancelCapture();
        try { _shutdown.Cancel(); } catch { }
        try { _wakeFrames.Writer.TryComplete(); } catch { }
        try { _wakeDetectorTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        var engine = _engine;
        _engine = null;
        try { engine?.RecognizeAsyncCancel(); } catch { }
        try { engine?.Dispose(); } catch { }
        StopDevice();
        _transcriber.Dispose();
        try { _shutdown.Dispose(); } catch { }
    }
}

/// <summary>Yes/no parsing that only accepts an answer at the start of what was said.</summary>
public static class AnswerParser
{
    private static readonly System.Text.RegularExpressions.Regex NoAnywhere =
        new(@"\b(no|nope|don'?t|do not|stop|cancel|deny|negative|never ?mind)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsYes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        // A no anywhere in the answer vetoes it: the safe reading of "yes, but no" is no.
        if (NoAnywhere.IsMatch(t)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(t,
            @"^(\W*)((um+|uh+|erm|hmm+|oh|ah|well)\W+)*(yes|yeah|yep|yup|sure|go ahead|do it|confirm(ed)?|affirmative|ok|okay|please do|proceed|send it|allow|permission granted)\b");
    }

    public static bool IsNo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(t,
            @"^(\W*)((um+|uh+|erm|hmm+|oh|ah|well)\W+)*(no|nope|don'?t|do not|stop|cancel|wait|hold on|never ?mind|deny|negative)\b");
    }
}

/// <summary>
/// Best-effort "is someone on a call?" check. Windows records microphone use per app in the
/// capability consent store; an app with a start newer than its stop is capturing right now.
/// </summary>
public static class CallDetector
{
    private static DateTime _checked = DateTime.MinValue;
    private static string? _cached;

    public static string? CurrentCallApp()
    {
        if (DateTime.UtcNow - _checked < TimeSpan.FromSeconds(2)) return _cached;
        _checked = DateTime.UtcNow;
        _cached = null;
        try
        {
            var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged");
            if (root is null) return null;
            var self = Environment.ProcessPath ?? "";
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                var stop = ToLong(key.GetValue("LastUsedTimeStop"));
                var start = ToLong(key.GetValue("LastUsedTimeStart"));
                if (stop != 0 || start == 0) continue;
                var path = name.Replace('#', '\\');
                if (path.Contains("Jarvis", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                _cached = Path.GetFileNameWithoutExtension(path);
                break;
            }
        }
        catch { }
        return _cached;
    }

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        _ => 0
    };
}
