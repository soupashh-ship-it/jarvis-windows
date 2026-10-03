"""Microphone, wake word, end-of-speech detection and speech-to-text."""
import asyncio
import collections
import io
import logging
import os
import re
import wave

import aiohttp
import numpy as np
import openwakeword
import sounddevice as sd
from faster_whisper import WhisperModel
from openwakeword.model import Model
from openwakeword.vad import VAD

import config

log = logging.getLogger("ears")
RATE = 16000
FRAME = 1280            # 80 ms, the frame size openWakeWord expects
FRAME_S = FRAME / RATE

# Whisper invents these on silence or noise
JUNK = re.compile(r"^\W*(thank you|thanks for watching|you|bye|\.+|okay)?\W*$", re.I)
WAKE_RMS_BLOCKS = 30    # match openWakeWord's 30-frame score history when its peak trails the phrase


def describe(i):
    d = sd.query_devices(i)
    return f"[{i}] {d['name']} ({sd.query_hostapis(d['hostapi'])['name']}, {d['default_samplerate']:.0f} Hz, " \
           f"{d['max_input_channels']} ch)"


def pick_mic(want):
    """MIC_DEVICE -> a device number. On Windows each mic is listed once per audio API (MME, DirectSound, WASAPI,
    WDM-KS), so a name matches several; prefer MME, which records at 16 kHz and lets Windows do the resampling."""
    if want is None or isinstance(want, int):
        return want
    found = [i for i, d in enumerate(sd.query_devices())
             if d["max_input_channels"] > 0 and str(want).lower() in d["name"].lower()]
    if not found:
        raise ValueError(f"no microphone matches MIC_DEVICE = {want!r} (the inputs are listed in logs/jarvis.log)")
    return min(found, key=lambda i: "MME" not in sd.query_hostapis(sd.query_devices(i)["hostapi"])["name"])


class Recorder:
    """Collects one utterance frame by frame. feed() returns None while still listening,
    "timeout" if nobody spoke, or the audio once they stop talking."""

    def __init__(self, vad, wait_for_speech):
        self.vad = vad
        self.wait_frames = int(wait_for_speech / FRAME_S)
        self.pre = collections.deque(maxlen=4)
        self.frames = []
        self.speaking = False
        self.silent = 0
        self.waited = 0
        vad.reset_states()

    def feed(self, chunk):
        p = self.vad.predict(chunk, frame_size=640)
        if not self.speaking:
            self.pre.append(chunk)
            self.waited += 1
            if p > 0.5:
                self.speaking = True
                self.frames.extend(self.pre)
            elif self.waited > self.wait_frames:
                return "timeout"
            return None
        self.frames.append(chunk)
        self.silent = self.silent + 1 if p < 0.3 else 0
        if self.silent * FRAME_S >= config.END_SILENCE_S or len(self.frames) * FRAME_S >= config.MAX_UTTERANCE_S:
            return np.concatenate(self.frames)
        return None


class Ears:
    def __init__(self, loop):
        self.loop = loop
        self.q = asyncio.Queue(maxsize=300)
        models_dir = os.path.join(os.path.dirname(openwakeword.__file__), "resources", "models")
        wanted = list(getattr(config, "WAKE_WORD_MODELS", ["hey_jarvis_v0.1.onnx"]) or ["hey_jarvis_v0.1.onnx"])
        found = []
        for entry in wanted:
            if os.path.isabs(entry) and os.path.isfile(entry):
                found.append(entry)
                continue
            for base in (models_dir, os.path.join(config.JARVIS_DIR, "models")):
                path = os.path.join(base, os.path.basename(entry))
                if os.path.isfile(path):
                    found.append(path)
                    break
            else:
                log.warning("wake word model %r not found; skipping", entry)
        if not found:
            found = [os.path.join(models_dir, "hey_jarvis_v0.1.onnx")]
        log.info("wake word models: %s", ", ".join(os.path.basename(p) for p in found))
        self.oww = Model(wakeword_models=found,
                         inference_framework="onnx", vad_threshold=config.WAKE_VAD_THRESHOLD)
        self.vad = VAD()
        self.last_score = 0.0
        self.mic_name = "?"
        if config.STT_ENGINE == "whisper":
            self.whisper = WhisperModel(config.WHISPER_MODEL, device="cpu", compute_type="int8")
            self.fast = WhisperModel("base.en", device="cpu", compute_type="int8")   # rough live text while you talk
        self.recent_rms = collections.deque(maxlen=WAKE_RMS_BLOCKS)
        self.attempt_peak_score = self.attempt_peak_rms = 0.0
        self.attempt_has_audio = False
        self.attempt_quiet_blocks = 0
        self.suppressed_logged = False
        self.dropped = 0                 # audio blocks lost (the mic overflowed, or Jarvis fell behind)
        log.info("microphones: %s", "; ".join(describe(i) for i, d in enumerate(sd.query_devices())
                                              if d["max_input_channels"] > 0))
        self.device = pick_mic(config.MIC_DEVICE)
        info = sd.query_devices(self.device, kind="input")
        api = sd.query_hostapis(info["hostapi"])["name"]
        self.mic_name = f"{info['name']} ({api})"
        # WASAPI refuses 16 kHz unless Windows is allowed to convert; MME and DirectSound convert anyway.
        extra = sd.WasapiSettings(auto_convert=True) if "WASAPI" in api else None
        self.stream = sd.InputStream(samplerate=RATE, channels=1, dtype="int16", blocksize=FRAME,
                                     device=self.device, extra_settings=extra, callback=self._callback)

    def start(self):
        self.stream.start()
        log.info("mic open: %s, native %.0f Hz, recording %d Hz mono; wake threshold %.2f; RMS gate %.0f over %.2f s; VAD %.2f",
                 describe(self.stream.device), sd.query_devices(self.stream.device)["default_samplerate"], RATE,
                 config.WAKE_THRESHOLD, config.WAKE_MIN_RMS, WAKE_RMS_BLOCKS * FRAME_S,
                 config.WAKE_VAD_THRESHOLD)

    def _callback(self, indata, frames, t, status):
        if status.input_overflow:
            self.dropped += 1
        self.loop.call_soon_threadsafe(self._put, indata[:, 0].copy())

    def _put(self, chunk):
        if self.q.full():
            self.q.get_nowait()
            self.dropped += 1
        self.q.put_nowait(chunk)

    def heard_wake_word(self, chunk):
        score = max(self.oww.predict(chunk).values())
        self.last_score = score
        rms = float(np.sqrt(np.mean(chunk.astype(np.float32) ** 2)))
        self.recent_rms.append(rms)
        recent_rms = max(self.recent_rms, default=0.0)
        self.attempt_peak_score = max(self.attempt_peak_score, score)
        self.attempt_peak_rms = max(self.attempt_peak_rms, rms)
        if rms >= 8:
            self.attempt_has_audio = True
            self.attempt_quiet_blocks = 0
        elif self.attempt_has_audio:
            self.attempt_quiet_blocks += 1
        if score >= config.WAKE_THRESHOLD:
            if recent_rms >= config.WAKE_MIN_RMS:
                log.info("wake score %.2f (threshold %.2f): woke; recent RMS %.0f, %d audio blocks dropped",
                         score, config.WAKE_THRESHOLD, recent_rms, self.dropped)
                self.reset_wake()
                return True
            if not self.suppressed_logged:
                log.info("wake candidate held: score %.2f; recent RMS %.0f below speech gate %.0f",
                         score, recent_rms, config.WAKE_MIN_RMS)
                self.suppressed_logged = True
        if self.attempt_has_audio and self.attempt_quiet_blocks >= WAKE_RMS_BLOCKS:
            self.finish_wake_attempt("speech ended")
            self.oww.reset()
            self.recent_rms.clear()
        return False

    def finish_wake_attempt(self, reason="manual activation"):
        """Preserve score and signal level when a manual trigger interrupts a missed wake attempt."""
        if self.attempt_has_audio:
            log.info("wake attempt %s: peak score %.2f / %.2f threshold; peak RMS %.0f / %.0f speech gate",
                     reason, self.attempt_peak_score, config.WAKE_THRESHOLD,
                     self.attempt_peak_rms, config.WAKE_MIN_RMS)
        self._clear_wake_attempt()

    def reset_wake(self):
        self.oww.reset()
        self.recent_rms.clear()
        self._clear_wake_attempt()

    def _clear_wake_attempt(self):
        self.attempt_peak_score = self.attempt_peak_rms = 0.0
        self.attempt_has_audio = False
        self.attempt_quiet_blocks = 0
        self.suppressed_logged = False

    def recorder(self, wait_for_speech):
        return Recorder(self.vad, wait_for_speech)

    async def partial(self, audio):
        if config.STT_ENGINE != "whisper":
            return ""                            # no live text from a remote service

        def run():
            segs, _ = self.fast.transcribe(audio.astype(np.float32) / 32768.0, language="en", beam_size=1,
                                           without_timestamps=True, initial_prompt=config.VOCAB)
            return " ".join(s.text for s in segs).strip()
        text = await self.loop.run_in_executor(None, run)
        return "" if JUNK.match(text) else text

    async def transcribe(self, audio):
        if config.STT_ENGINE == "openai":
            text = await self._remote(audio)
            return "" if JUNK.match(text) else text

        def run():
            segs, _ = self.whisper.transcribe(audio.astype(np.float32) / 32768.0, language="en",
                                              beam_size=1, vad_filter=True,
                                              initial_prompt=config.VOCAB)
            return " ".join(s.text for s in segs).strip()
        text = await self.loop.run_in_executor(None, run)
        return "" if JUNK.match(text) else text

    @staticmethod
    async def _remote(audio):
        """Any OpenAI-compatible /audio/transcriptions endpoint (OpenAI, Groq, a local whisper server...)."""
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes(audio.tobytes())
        form = aiohttp.FormData()
        form.add_field("file", buf.getvalue(), filename="speech.wav", content_type="audio/wav")
        for k, v in (("model", config.STT_MODEL), ("language", "en"), ("prompt", config.VOCAB)):
            form.add_field(k, v)
        headers = {"Authorization": f"Bearer {config.STT_API_KEY}"} if config.STT_API_KEY else {}
        try:
            async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=60)) as s, \
                    s.post(config.STT_BASE_URL.rstrip("/") + "/audio/transcriptions", data=form, headers=headers) as r:
                if r.status != 200:
                    raise RuntimeError(f"{r.status}: {(await r.text())[:300]}")
                return (await r.json())["text"].strip()
        except Exception:
            log.exception("speech to text failed")
            return ""
