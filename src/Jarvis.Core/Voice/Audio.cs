using System.Net;
using System.Speech.Synthesis;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Core;

/// <summary>
/// A pull-based PCM queue. It never blocks the audio thread, reports the current output level,
/// and can be cleared instantly so JARVIS can be talked over.
/// </summary>
public sealed class SpeechQueueProvider : IWaveProvider
{
    private readonly object _gate = new();
    private readonly Queue<(byte[] Pcm, string? Text)> _queue = new();
    private int _offset;
    private bool _announced;
    private int _level;

    public WaveFormat WaveFormat { get; } = new(24000, 16, 1);
    public Action<string>? SentenceStarted { get; set; }

    public bool HasData { get { lock (_gate) return _queue.Count > 0; } }

    public float Level => Volatile.Read(ref _level) / 32768f;

    public void Enqueue(byte[] pcm, string? text)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            _queue.Enqueue((pcm, text));
            if (_queue.Count == 1 && _offset == 0) _announced = false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _queue.Clear();
            _offset = 0;
            _announced = false;
        }
        Volatile.Write(ref _level, 0);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var written = 0;

        lock (_gate)
        {
            while (written < count && _queue.Count > 0)
            {
                var (pcm, text) = _queue.Peek();
                if (_offset == 0 && !_announced)
                {
                    _announced = true;
                    if (!string.IsNullOrWhiteSpace(text) && SentenceStarted is not null)
                    {
                        var sentence = text!;
                        var callback = SentenceStarted;
                        ThreadPool.QueueUserWorkItem(_ => { try { callback?.Invoke(sentence); } catch { } });
                    }
                }

                var available = pcm.Length - _offset;
                var take = Math.Min(available, count - written);
                Buffer.BlockCopy(pcm, _offset, buffer, offset + written, take);
                written += take;
                _offset += take;

                if (_offset >= pcm.Length)
                {
                    _queue.Dequeue();
                    _offset = 0;
                    _announced = false;
                }
            }
        }

        if (written > 0)
        {
            var peak = 0;
            for (var i = offset; i < offset + written - 1; i += 2)
            {
                var sample = Math.Abs(BitConverter.ToInt16(buffer, i));
                if (sample > peak) peak = sample;
            }
            Volatile.Write(ref _level, peak);
        }
        else
        {
            Volatile.Write(ref _level, 0);
        }

        return count;
    }
}

public sealed class AudioOutput : IDisposable
{
    private readonly SpeechQueueProvider _provider;
    private WaveOutEvent? _device;
    private bool _available;

    public AudioOutput(int volumePercent = 100, string? deviceName = null)
    {
        _provider = new SpeechQueueProvider();
        Reconfigure(volumePercent, deviceName);
    }

    public Action<string>? SentenceStarted
    {
        get => _provider.SentenceStarted;
        set => _provider.SentenceStarted = value;
    }

    public bool IsPlaying => _available && _provider.HasData;
    public bool IsAvailable => _available;
    public float Level => _available ? _provider.Level : 0f;
    public bool HasData => _available && _provider.HasData;

    /// <summary>Switches device or volume in one step, falling back to the Windows default.</summary>
    public void Reconfigure(int volumePercent, string? deviceName)
    {
        DisposeDevice();
        var wanted = AudioDevices.ResolveOutput(deviceName);

        if (TryOpen(wanted, volumePercent))
        {
            if (wanted >= 0)
                Log.Info($"speech output on '{AudioDevices.NameOf(AudioDevices.Outputs(), wanted)}'");
            return;
        }

        if (wanted >= 0 && TryOpen(-1, volumePercent))
        {
            Log.Warn("the chosen playback device could not be opened; using the Windows default");
            return;
        }

        Log.Warn("no audio output device; speech playback is disabled");
    }

    private bool TryOpen(int deviceNumber, int volumePercent)
    {
        try
        {
            var device = new WaveOutEvent
            {
                DeviceNumber = deviceNumber,
                DesiredLatency = 120,
                NumberOfBuffers = 3,
                Volume = Math.Clamp(volumePercent, 0, 100) / 100f
            };
            device.Init(_provider);
            device.Play();
            _device = device;
            _available = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"audio device {deviceNumber} unavailable: {ex.Message}");
            return false;
        }
    }

    public void Play(byte[] pcm, string? text)
    {
        if (_available) _provider.Enqueue(pcm, text);
    }

    public void Stop() => _provider.Clear();

    public void SetVolume(int percent)
    {
        if (_device is null) return;
        try { _device.Volume = Math.Clamp(percent, 0, 100) / 100f; } catch { }
    }

    public async Task WaitDrainedAsync(TimeSpan timeout)
    {
        if (!_available) return;
        var deadline = DateTime.UtcNow + timeout;
        while (_provider.HasData && DateTime.UtcNow < deadline)
            await Task.Delay(30).ConfigureAwait(false);
    }

    private void DisposeDevice()
    {
        _available = false;
        try { _device?.Stop(); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    public void Dispose() => DisposeDevice();
}

public static class AudioConvert
{
    /// <summary>Anything NAudio can decode becomes 24 kHz mono 16-bit PCM, the format the queue expects.</summary>
    public static byte[] ToPcm16(ISampleProvider source, int targetRate = 24000)
    {
        if (source.WaveFormat.Channels > 1)
        {
            if (source.WaveFormat.Channels == 2)
                source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else
                source = new MonoSampleProvider(source);
        }

        if (source.WaveFormat.SampleRate != targetRate)
            source = new WdlResamplingSampleProvider(source, targetRate);

        var buffer = new float[8192];
        using var output = new MemoryStream();
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var i = 0; i < read; i++)
            {
                var sample = (short)Math.Clamp((int)Math.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue);
                output.WriteByte((byte)(sample & 0xFF));
                output.WriteByte((byte)((sample >> 8) & 0xFF));
            }
        }
        return output.ToArray();
    }

    private sealed class MonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public MonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = _source.Read(_buffer, 0, needed);
            var frames = read / _channels;
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }
}

/// <summary>
/// Microsoft Azure neural voices: the same en-GB-RyanNeural that sits closest to JARVIS, using a key the user
/// can get free. The offline Windows voices remain the fallback, so the assistant always speaks.
/// </summary>
public sealed class AzureTts : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly AppSettings _settings;

    public AzureTts(AppSettings settings) => _settings = settings;

    public bool Configured => _settings.Tts == TtsEngine.Azure &&
                              !string.IsNullOrWhiteSpace(_settings.AzureKey) &&
                              !string.IsNullOrWhiteSpace(_settings.AzureRegion);

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        var key = _settings.AzureKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ModelException("No Azure Speech key is configured.");
        var region = string.IsNullOrWhiteSpace(_settings.AzureRegion) ? "uksouth" : _settings.AzureRegion.Trim();
        var uri = new Uri($"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        request.Headers.UserAgent.ParseAdd("JarvisForWindows/1.0");
        request.Content = new StringContent(BuildSsml(text), Encoding.UTF8, "application/ssml+xml");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = "";
            try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            var hint = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Check the key and the region."
                : body.Length > 200 ? body[..200] : "";
            throw new ModelException($"Azure Speech returned {(int)response.StatusCode}. {hint}".Trim());
        }

        var mp3 = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (mp3.Length < 128) throw new ModelException("Azure Speech returned no audio.");
        return DecodeMp3(mp3);
    }

    internal static byte[] DecodeMp3(byte[] mp3)
    {
        using var stream = new MemoryStream(mp3);
        using var reader = new Mp3FileReader(stream);
        return AudioConvert.ToPcm16(reader.ToSampleProvider());
    }

    private string BuildSsml(string text)
    {
        var rate = (int)Math.Round((_settings.SpeechRate - 1.0) * 100);
        var pitch = (int)Math.Round((_settings.SpeechPitch - 1.0) * 50);
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
        var voice = string.IsNullOrWhiteSpace(_settings.AzureVoice) ? "en-GB-RyanNeural" : _settings.AzureVoice;
        return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-GB'>" +
               $"<voice name='{voice}'>" +
               $"<prosody pitch='{(pitch >= 0 ? "+" : "")}{pitch}Hz' " +
               $"rate='{(rate >= 0 ? "+" : "")}{rate}%' volume='+0%'>" +
               escaped + "</prosody></voice></speak>";
    }

    public void Dispose() { }
}

/// <summary>The Windows voice, used when Azure is not configured or the network is down.</summary>
public sealed class SapiTts
{
    private readonly AppSettings _settings;

    public SapiTts(AppSettings settings) => _settings = settings;

    public static IReadOnlyList<string> Voices()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Array.Empty<byte>();

        using var synth = new SpeechSynthesizer();
        try
        {
            var wanted = _settings.SapiVoice;
            var installed = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
            if (string.IsNullOrWhiteSpace(wanted))
                wanted = installed.FirstOrDefault(v => v.Contains("George", StringComparison.OrdinalIgnoreCase))
                         ?? installed.FirstOrDefault(v => v.Contains("Hazel", StringComparison.OrdinalIgnoreCase))
                         ?? installed.FirstOrDefault() ?? "";
            if (!string.IsNullOrWhiteSpace(wanted) && installed.Contains(wanted))
                synth.SelectVoice(wanted);

            synth.Rate = Math.Clamp((int)Math.Round((_settings.SpeechRate - 1.0) * 10), -10, 10);
            synth.Volume = 100;

            using var stream = new MemoryStream();
            synth.SetOutputToWaveStream(stream);
            var completed = new TaskCompletionSource<SpeakCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<SpeakCompletedEventArgs> onCompleted = (_, args) => completed.TrySetResult(args);
            synth.SpeakCompleted += onCompleted;
            try
            {
                // Start first, then register cancellation. Register invokes immediately if Stop
                // raced with startup, so no synchronous Speak call can hold the pump hostage.
                synth.SpeakAsync(text);
                using var cancellation = ct.Register(static state =>
                {
                    try { ((SpeechSynthesizer)state!).SpeakAsyncCancelAll(); }
                    catch { }
                }, synth);

                var result = await completed.Task.ConfigureAwait(false);
                if (ct.IsCancellationRequested || result.Cancelled) return Array.Empty<byte>();
                if (result.Error is not null) throw result.Error;
            }
            finally
            {
                synth.SpeakCompleted -= onCompleted;
            }
            synth.SetOutputToNull();

            stream.Position = 0;
            using var reader = new WaveFileReader(stream);
            return AudioConvert.ToPcm16(reader.ToSampleProvider());
        }
        catch (Exception ex)
        {
            Log.Warn("SAPI speech failed", ex);
            return Array.Empty<byte>();
        }
    }
}

/// <summary>Speaks whole sentences as soon as they are ready, with instant interruption.</summary>
public sealed class TtsService : IDisposable
{
    private readonly AppSettings _settings;
    private AudioOutput _output;
    private readonly AzureTts _azure;
    private readonly SapiTts _sapi;
    private readonly List<string> _pending = new();
    private readonly object _gate = new();
    private bool _pumping;
    private volatile bool _synthesizing;
    private int _generation;
    private int _pumpId;
    private CancellationTokenSource? _synthesisCancellation;
    private DateTime _azureBlockedUntil = DateTime.MinValue;

    public TtsService(AppSettings settings)
    {
        _settings = settings;
        _output = new AudioOutput(settings.TtsVolume, settings.SpeakerDevice);
        _azure = new AzureTts(settings);
        _sapi = new SapiTts(settings);
        _output.SentenceStarted = sentence => SentenceStarted?.Invoke(sentence);
    }

    public event Action<string>? SentenceStarted;
    public bool Enabled { get; set; } = true;
    public bool IsSpeaking
    {
        get
        {
            lock (_gate) return _synthesizing || _output.HasData || _pending.Count > 0;
        }
    }
    public float Level => _output.Level;
    public string LastEngine { get; private set; } = "";

    public void Say(string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        var spoken = SpeechText.Speakable(text);
        if (spoken.Length == 0) return;

        int pumpId;
        lock (_gate)
        {
            _pending.Add(spoken);
            if (_pumping) return;
            _pumping = true;
            pumpId = ++_pumpId;
        }
        _ = Task.Run(() => PumpAsync(pumpId));
    }

    public void SaySentences(IEnumerable<string> sentences)
    {
        foreach (var s in sentences) Say(s);
    }

    private async Task PumpAsync(int pumpId)
    {
        try
        {
            while (true)
            {
                string? text;
                int generation;
                CancellationTokenSource synthesisCancellation;
                lock (_gate)
                {
                    if (_pending.Count == 0) { _pumping = false; return; }
                    text = _pending[0];
                    _pending.RemoveAt(0);
                    generation = _generation;
                    synthesisCancellation = new CancellationTokenSource();
                    _synthesisCancellation = synthesisCancellation;
                }

                if (text is null) continue;
                byte[] pcm = Array.Empty<byte>();
                _synthesizing = true;
                try
                {
                    if (DateTime.UtcNow > _azureBlockedUntil && _settings.Tts == TtsEngine.Azure &&
                        !string.IsNullOrWhiteSpace(_settings.AzureKey))
                    {
                        try
                        {
                            pcm = await _azure.SynthesizeAsync(text, synthesisCancellation.Token).ConfigureAwait(false);
                            LastEngine = "Azure neural";
                        }
                        catch (OperationCanceledException) when (synthesisCancellation.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            Log.Warn("azure tts failed; falling back to the Windows voice", ex);
                            _azureBlockedUntil = DateTime.UtcNow.AddMinutes(10);
                            pcm = Array.Empty<byte>();
                        }
                    }

                    if (pcm.Length == 0 && !synthesisCancellation.IsCancellationRequested)
                    {
                        pcm = await _sapi.SynthesizeAsync(text, synthesisCancellation.Token).ConfigureAwait(false);
                        LastEngine = "Windows voice";
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("speech synthesis failed", ex);
                }
                finally
                {
                    _synthesizing = false;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_synthesisCancellation, synthesisCancellation))
                            _synthesisCancellation = null;
                    }
                    synthesisCancellation.Dispose();
                }

                lock (_gate)
                {
                    if (generation != _generation) continue;
                }
                if (pcm.Length > 0) _output.Play(pcm, text);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("speech output pump failed", ex);
        }
        finally
        {
            int nextPump = 0;
            lock (_gate)
            {
                // An older pump may finish after Say has already installed a successor.
                // Only the current owner may release or transfer the pump slot.
                if (_pumpId == pumpId)
                {
                    if (_pending.Count == 0) _pumping = false;
                    else nextPump = _pumpId = _pumpId + 1;
                }
            }
            if (nextPump != 0) _ = Task.Run(() => PumpAsync(nextPump));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? activeSynthesis;
        lock (_gate)
        {
            _pending.Clear();
            _generation++;
            activeSynthesis = _synthesisCancellation;
        }
        try { activeSynthesis?.Cancel(); } catch { }
        _output.Stop();
    }

    public async Task WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((IsSpeaking || _output.HasData) && DateTime.UtcNow < deadline)
            await Task.Delay(40).ConfigureAwait(false);
    }

    public void SetVolume(int percent) => _output.SetVolume(percent);

    /// <summary>
    /// Rebuilds the playback device from the current settings. Called after Settings saves, so a
    /// new speaker takes effect without a restart; any sentence in flight is cut off, which is the
    /// honest thing to do when the output device disappears.
    /// </summary>
    public void ReloadOutput()
    {
        Stop();
        _output.Reconfigure(_settings.TtsVolume, _settings.SpeakerDevice);
    }

    /// <summary>A short two-note chime, so the user knows the microphone is open.</summary>
    public void Chime(bool error = false)
    {
        try
        {
            var notes = error ? new[] { 300.0, 220.0 } : new[] { 660.0, 990.0 };
            _output.Play(Tone(notes, error ? 0.15 : 0.09), null);
        }
        catch (Exception ex) { Log.Debug("chime failed: " + ex.Message); }
    }

    private static byte[] Tone(double[] frequencies, double duration, double volume = 0.18)
    {
        const int rate = 24000;
        var frames = (int)(rate * duration);
        var total = frames * frequencies.Length;
        var pcm = new byte[total * 2];
        var index = 0;
        foreach (var frequency in frequencies)
        {
            for (var i = 0; i < frames; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, frames - i) / (rate * 0.012));
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * envelope * volume * short.MaxValue);
                pcm[index++] = (byte)(sample & 0xFF);
                pcm[index++] = (byte)((sample >> 8) & 0xFF);
            }
        }
        return pcm;
    }

    public void Dispose()
    {
        Stop();
        _output.Dispose();
        _azure.Dispose();
    }
}
