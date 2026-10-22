namespace Jarvis.Core;

/// <summary>
/// Collects 80 ms microphone frames into one utterance. A short rolling pre-roll keeps the first
/// consonant, and an adaptive RMS gate ends the recording after sustained silence.
/// </summary>
internal sealed class UtteranceRecorder
{
    private const int SampleRate = 16_000;
    private const int FrameSamples = 1_280;
    private const int PreRollFrames = 4;

    private readonly object _gate = new();
    private readonly List<float> _pending = new(FrameSamples * 2);
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _audio = new(SampleRate * 8);
    private readonly TaskCompletionSource _speechStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<float[]?> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _endSilenceFrames;
    private readonly int _maxFrames;

    private float _noiseRms = 0.003f;
    private int _candidateFrames;
    private int _silentFrames;
    private bool _speaking;
    private bool _finished;

    public UtteranceRecorder(double endSilenceSeconds, double maxUtteranceSeconds)
    {
        _endSilenceFrames = Math.Max(3,
            (int)Math.Ceiling(Math.Clamp(endSilenceSeconds, 0.25, 4.0) * SampleRate / FrameSamples));
        _maxFrames = Math.Max(5,
            (int)Math.Ceiling(Math.Clamp(maxUtteranceSeconds, 1.0, 120.0) * SampleRate / FrameSamples));
    }

    public Task SpeechStarted => _speechStarted.Task;

    public async Task<float[]?> WaitAsync(TimeSpan firstSpeechWindow, CancellationToken ct)
    {
        try
        {
            var firstSpeech = await Task.WhenAny(_speechStarted.Task, Task.Delay(firstSpeechWindow, ct))
                .ConfigureAwait(false);
            if (firstSpeech != _speechStarted.Task) return null;

            var maxDuration = TimeSpan.FromSeconds((double)_maxFrames * FrameSamples / SampleRate);
            var completed = await Task.WhenAny(_completed.Task, Task.Delay(maxDuration, ct))
                .ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;
            if (completed == _completed.Task)
                return await _completed.Task.ConfigureAwait(false);

            return Finish();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void Push(byte[] pcm16, int count)
    {
        if (count < 2) return;

        lock (_gate)
        {
            if (_finished) return;

            var usable = count & ~1;
            for (var i = 0; i < usable; i += 2)
                _pending.Add(BitConverter.ToInt16(pcm16, i) / 32768f);

            var consumed = 0;
            while (!_finished && _pending.Count - consumed >= FrameSamples)
            {
                var frame = new float[FrameSamples];
                _pending.CopyTo(consumed, frame, 0, FrameSamples);
                consumed += FrameSamples;
                ProcessFrame(frame);
            }

            if (consumed > 0) _pending.RemoveRange(0, consumed);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _finished = true;
            _completed.TrySetResult(null);
        }
    }

    private void ProcessFrame(float[] frame)
    {
        var rms = Rms(frame);

        if (!_speaking)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();

            // The old 0.012 RMS floor missed normal speech from quiet laptop and headset mics.
            // Keep the gate above the measured noise floor, but let low-level speech start a turn.
            var startThreshold = Math.Clamp(Math.Max(0.0055f, _noiseRms * 2.2f), 0.0055f, 0.03f);
            if (rms >= startThreshold)
            {
                _candidateFrames++;
            }
            else
            {
                _candidateFrames = 0;
                _noiseRms = (_noiseRms * 0.98f) + (rms * 0.02f);
            }

            // Two consecutive frames (160 ms) reject shorter clicks while allowing quiet speech
            // to start before its initial consonant falls out of the rolling pre-roll.
            if (_candidateFrames >= 2)
            {
                _speaking = true;
                foreach (var buffered in _preRoll) _audio.AddRange(buffered);
                _speechStarted.TrySetResult();
            }
            return;
        }

        _audio.AddRange(frame);
        var silenceThreshold = Math.Max(0.0035f, _noiseRms * 1.8f);
        _silentFrames = rms < silenceThreshold ? _silentFrames + 1 : 0;

        var recordedFrames = _audio.Count / FrameSamples;
        if (_silentFrames >= _endSilenceFrames || recordedFrames >= _maxFrames)
            Complete();
    }

    private float[]? Finish()
    {
        lock (_gate)
        {
            if (_finished) return _completed.Task.IsCompletedSuccessfully ? _completed.Task.Result : null;
            if (!_speaking || _audio.Count == 0)
            {
                _finished = true;
                _completed.TrySetResult(null);
                return null;
            }

            _finished = true;
            var samples = _audio.ToArray();
            _completed.TrySetResult(samples);
            return samples;
        }
    }

    private void Complete()
    {
        _finished = true;
        _completed.TrySetResult(_audio.ToArray());
    }

    private static float Rms(float[] frame)
    {
        double sum = 0;
        foreach (var sample in frame) sum += sample * sample;
        return (float)Math.Sqrt(sum / frame.Length);
    }
}
