using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Core;

/// <summary>One playback endpoint Windows reports, as the legacy mixer sees it.</summary>
public sealed record AudioDevice(int Index, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// One recording endpoint. <paramref name="Id"/> is Windows' endpoint id, which survives a reboot
/// and a re-plug; <paramref name="Name"/> is the friendly name shown in Settings.
/// </summary>
public sealed record CaptureDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumerates playback and capture endpoints and opens the microphone.
/// Recording devices come from Windows' own endpoint list (the legacy mixer API reports some
/// modern microphones as "No such microphone &lt;hash&gt;", which is how a dead virtual cable used to
/// end up selected). Names are stored as ids because Windows renumbers devices whenever something
/// is plugged in, and a stale index silently points at the wrong headset.
/// </summary>
public static class AudioDevices
{
    /// <summary>The empty name means "whatever Windows is using".</summary>
    public const string SystemDefault = "";

    public static IReadOnlyList<AudioDevice> Outputs()
    {
        var list = new List<AudioDevice>();
        try
        {
            var count = WaveOut.DeviceCount;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var name = WaveOut.GetCapabilities(i).ProductName;
                    // Devices that do not really support the legacy mixer show up as a hash.
                    if (string.IsNullOrWhiteSpace(name) ||
                        name.StartsWith("No such ", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new AudioDevice(i, name));
                }
                catch (Exception ex) { Log.Debug($"playback device {i} unreadable: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug("enumerating playback devices failed: " + ex.Message); }
        return list;
    }

    /// <summary>Every active recording endpoint, by the name Windows itself shows.</summary>
    public static IReadOnlyList<CaptureDevice> Inputs()
    {
        var list = new List<CaptureDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    list.Add(new CaptureDevice(device.ID, device.FriendlyName));
                }
                catch (Exception ex) { Log.Debug("microphone unreadable: " + ex.Message); }
                finally
                {
                    try { device.Dispose(); } catch { }
                }
            }
        }
        catch (Exception ex) { Log.Debug("enumerating microphones failed: " + ex.Message); }
        return list;
    }

    /// <summary>The device index, or -1 for the Windows default. Unknown names fall back to the default.</summary>
    public static int ResolveOutput(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        foreach (var device in Outputs())
            if (string.Equals(device.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return device.Index;
        Log.Warn($"playback device '{name}' is not connected; using the Windows default");
        return -1;
    }

    /// <summary>
    /// Finds the chosen microphone by id, then by name (older settings stored a name). Falls back to
    /// the Windows default when it is gone, so a stale choice never leaves voice input dead.
    /// </summary>
    public static MMDevice? FindInput(string? idOrName)
    {
        var wanted = (idOrName ?? "").Trim();
        if (wanted.Length == 0) return DefaultInput();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? byName = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    if (string.Equals(device.ID, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        byName?.Dispose();
                        return device;
                    }
                    if (byName is null &&
                        string.Equals(device.FriendlyName, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        byName = device;
                        continue;
                    }
                }
                catch (Exception ex) { Log.Debug("microphone lookup failed: " + ex.Message); }
                device.Dispose();
            }
            if (byName is not null) return byName;
        }
        catch (Exception ex) { Log.Debug("microphone lookup failed: " + ex.Message); }

        var shown = wanted.Length <= 40 ? wanted : wanted[..40] + "...";
        Log.Warn($"microphone '{shown}' is not connected; using the Windows default");
        return DefaultInput();
    }

    /// <summary>The Windows default recording endpoint, or null when the machine has no microphone.</summary>
    public static MMDevice? DefaultInput()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex)
        {
            Log.Debug("the Windows default microphone is unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>The friendly name for a saved id or name, for status lines.</summary>
    public static string InputName(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return "the Windows default";
        try
        {
            using var device = FindInput(idOrName);
            return device?.FriendlyName ?? "the Windows default";
        }
        catch { return "the Windows default"; }
    }

    public static string NameOf(IReadOnlyList<AudioDevice> devices, int index) =>
        devices.FirstOrDefault(d => d.Index == index)?.Name ?? "the Windows default";

    /// <summary>
    /// Listens to a microphone for a moment and returns the loudest sample seen, 0..1.
    /// Used by Settings to prove the chosen device is actually picking up a voice.
    /// </summary>
    public static async Task<double> MeasureInputAsync(string? idOrName, TimeSpan window, CancellationToken ct)
    {
        using var capture = new MicrophoneCapture();
        capture.Start(idOrName);
        var peak = 0.0;
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            if (capture.Level > peak) peak = capture.Level;
        }
        return peak;
    }
}

/// <summary>
/// Opens a recording endpoint with WASAPI and delivers 16 kHz mono 16-bit PCM, whatever the
/// device's native rate or channel count. Also reports a live level, which is what makes a silent
/// microphone obvious in Settings instead of looking like a broken wake word.
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _samples;
    private readonly float[] _floats = new float[4096];
    private float _level;
    private bool _stoppedUnexpectedly;

    /// <summary>16 kHz mono PCM16, ready for the Windows recogniser.</summary>
    public event Action<byte[], int>? Data;
    public event Action<Exception?>? StoppedUnexpectedly;

    public string DeviceName { get; private set; } = "Windows default";

    /// <summary>Multiplier applied to the samples. A quiet headset can be lifted here.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Loudest recent sample, 0..1, after a gentle decay so the meter does not flicker.</summary>
    public float Level => Volatile.Read(ref _level);

    public bool Running { get { lock (_gate) return _capture is not null && !_stoppedUnexpectedly; } }

    public void Start(string? idOrName)
    {
        Stop();

        var device = AudioDevices.FindInput(idOrName)
                     ?? throw new InvalidOperationException("No microphone is connected.");
        var capture = new WasapiCapture(device);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            // This provider otherwise pads every short read with silence. Deliver() reads a
            // fixed-size block on each microphone callback, so padding makes the resampler
            // emit audio faster than real time and buries speech in silence before STT sees it.
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(600)
        };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (capture.WaveFormat.Channels > 1)
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        // Whatever the device's rate, the recogniser wants 16 kHz.
        var resampler = new WdlResamplingSampleProvider(samples, 16000);

        try { DeviceName = device.FriendlyName; }
        catch { DeviceName = "Windows default"; }

        lock (_gate)
        {
            _capture = capture;
            _device = device;
            _buffer = buffer;
            _samples = resampler;
            _stoppedUnexpectedly = false;
        }

        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                    _device = null;
                    _buffer = null;
                    _samples = null;
                }
            }
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try { capture.Dispose(); } catch { }
            try { device.Dispose(); } catch { }
            throw;
        }

        Volatile.Write(ref _level, 0f);
        Log.Info($"microphone open: {DeviceName} ({capture.WaveFormat})");
    }

    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_gate)
        {
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
            _buffer = null;
            _samples = null;
            _stoppedUnexpectedly = false;
        }

        if (capture is not null)
        {
            try { capture.DataAvailable -= OnData; } catch { }
            try { capture.RecordingStopped -= OnStopped; } catch { }
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }
        try { device?.Dispose(); } catch { }
        Volatile.Write(ref _level, 0f);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            Deliver(e);
        }
        catch (Exception ex)
        {
            // A throw here would kill the capture thread and silence the microphone for good.
            Log.Warn("microphone capture failed", ex);
            Volatile.Write(ref _level, 0f);
        }
    }

    private void Deliver(WaveInEventArgs e)
    {
        BufferedWaveProvider? buffer;
        ISampleProvider? samples;
        lock (_gate)
        {
            buffer = _buffer;
            samples = _samples;
        }
        if (buffer is null || samples is null) return;
        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // The resampler keeps whatever it could not use yet, so one read per callback is enough.
        var read = samples.Read(_floats, 0, _floats.Length);
        if (read <= 0) return;
        Emit(_floats, read);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Volatile.Write(ref _level, 0f);
        if (e.Exception is not null) Log.Warn("microphone stopped", e.Exception);
        lock (_gate)
        {
            if (!ReferenceEquals(_capture, sender)) return;
            _stoppedUnexpectedly = true;
        }
        try { StoppedUnexpectedly?.Invoke(e.Exception); } catch { }
    }

    /// <summary>Turns the 16 kHz mono samples into little-endian PCM16 and measures them.</summary>
    private void Emit(float[] samples, int count)
    {
        var payload = new byte[count * 2];
        var peak = 0f;
        for (var i = 0; i < count; i++)
        {
            // The meter shows what the recogniser will actually hear, gain included.
            var scaled = Math.Clamp(samples[i] * (float)Gain, -1f, 1f);
            var loud = Math.Abs(scaled);
            if (loud > peak) peak = loud;

            var pcm = (short)(scaled * 32767f);
            payload[i * 2] = (byte)(pcm & 0xFF);
            payload[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        // Fast attack, slow fall, so a word shows as a bar rather than a blinking dot.
        var current = Volatile.Read(ref _level);
        Volatile.Write(ref _level, peak > current ? peak : current * 0.72f);

        // Handlers run outside the lock: a slow one must never stall the audio callback.
        Data?.Invoke(payload, payload.Length);
    }
    public void Dispose() => Stop();
}

/// <summary>
/// A pull stream fed by a microphone callback. The recogniser reads it continuously; reads are paced
/// to the duration of the audio they ask for, so a dry microphone yields real-time silence instead of
/// letting the recogniser burn through an endless stream of empty samples.
/// </summary>
internal sealed class MicrophoneStream : Stream
{
    private const int MaxBytes = 16000 * 2 * 2; // two seconds of 16 kHz mono, the most we ever keep
    private const double MonoBytesPerMs = 32;   // 16 kHz × 16-bit
    private const double StereoBytesPerMs = 64;

    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;
    private int _queued;

    /// <summary>True when the capture format is stereo and each frame must be mixed to mono.</summary>
    public bool StereoInput { get; set; }

    public void Push(byte[] buffer, int count)
    {
        if (count <= 0) return;
        var slice = new byte[count];
        Buffer.BlockCopy(buffer, 0, slice, 0, count);
        lock (_gate)
        {
            _chunks.Enqueue(slice);
            _queued += count;
            // Drop the oldest audio rather than let latency grow without bound.
            while (_queued > MaxBytes && _chunks.Count > 1) _queued -= _chunks.Dequeue().Length;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        if (StereoInput)
        {
            // The engine asked for `count` mono bytes, which spans `count` stereo bytes of capture.
            var frames = count / 4;
            var got = ReadPaced(buffer, offset, frames * 4, StereoBytesPerMs);
            var real = got / 4;
            for (var f = 0; f < real; f++)
            {
                var left = BitConverter.ToInt16(buffer, offset + f * 4);
                var right = BitConverter.ToInt16(buffer, offset + f * 4 + 2);
                var mixed = (short)((left + right) / 2);
                buffer[offset + f * 2] = (byte)(mixed & 0xFF);
                buffer[offset + f * 2 + 1] = (byte)((mixed >> 8) & 0xFF);
            }
            return count;
        }

        ReadPaced(buffer, offset, count, MonoBytesPerMs);
        // The stream never reports an end: a dry read is padded with silence.
        return count;
    }

    private int ReadPaced(byte[] buffer, int offset, int count, double bytesPerMs)
    {
        var deadline = Environment.TickCount64 + (long)Math.Ceiling(count / bytesPerMs);
        var written = 0;
        lock (_gate)
        {
            while (written < count)
            {
                written += CopyAvailable(buffer, offset + written, count - written);
                if (written >= count) break;

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) break;
                Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
            }
        }
        return written;
    }

    private int CopyAvailable(byte[] buffer, int offset, int count)
    {
        var written = 0;
        while (written < count)
        {
            if (_current is null)
            {
                if (_chunks.Count == 0) break;
                _current = _chunks.Dequeue();
                _offset = 0;
                _queued -= _current.Length;
            }

            var take = Math.Min(_current.Length - _offset, count - written);
            Buffer.BlockCopy(_current, _offset, buffer, offset + written, take);
            written += take;
            _offset += take;
            if (_offset >= _current.Length)
            {
                _current = null;
                _offset = 0;
            }
        }
        return written;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 0;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => 0;
    public override void SetLength(long value) { }
    public override void Write(byte[] buffer, int offset, int count) { }
}
