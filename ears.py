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
NEAR_MISS = 0.05        # wake scores above this get logged, so a tester can see how close "hey jarvis" came


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
        self.oww = Model(wakeword_models=[os.path.join(models_dir, "hey_jarvis_v0.1.onnx")],
                         inference_framework="onnx")
        self.vad = VAD()
        self.last_score = 0.0
        self.mic_name = "?"
        if config.STT_ENGINE == "whisper":
            self.whisper = WhisperModel(config.WHISPER_MODEL, device="cpu", compute_type="int8")
            self.fast = WhisperModel("base.en", device="cpu", compute_type="int8")   # rough live text while you talk
        self.peak = self.peak_level = 0.0
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
        log.info("mic open: %s, native %.0f Hz, recording %d Hz mono; wake threshold %.2f",
                 describe(self.stream.device), sd.query_devices(self.stream.device)["default_samplerate"], RATE,
                 config.WAKE_THRESHOLD)

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
        if score >= NEAR_MISS:           # one log line per attempt: its best score and how loud the mic was
            self.peak = max(self.peak, score)
            self.peak_level = max(self.peak_level, float(np.sqrt(np.mean(chunk.astype(np.float32) ** 2))) / 32768)
        if score >= config.WAKE_THRESHOLD or (self.peak and score < NEAR_MISS):
            log.info("wake score %.2f (threshold %.2f): %s; mic level %d%%, %d audio blocks dropped so far",
                     self.peak, config.WAKE_THRESHOLD, "woke" if score >= config.WAKE_THRESHOLD else "missed",
                     min(100, self.peak_level * 800), self.dropped)
            self.peak = self.peak_level = 0.0
        if score >= config.WAKE_THRESHOLD:
            self.reset_wake()
            return True
        return False

    def reset_wake(self):
        self.oww.reset()

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
