"""JARVIS: say "hey jarvis", talk, get an answer out loud.

Run:        install.ps1 sets it to start at logon (or: .venv\\Scripts\\python jarvis.py)
Poke:       python jarvisctl.py listen | stop | say "open spotify" | status
Dashboard:  http://127.0.0.1:8765
"""
import asyncio
import collections
import json
import logging
import logging.handlers
import os
import re
import signal
import sys
import time
from datetime import datetime, timedelta

CONSOLE = sys.stderr is not None
if not CONSOLE:   # pythonw (the logon task) has no console: keep library output and stray tracebacks in a file
    _console = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", "console.log")
    os.makedirs(os.path.dirname(_console), exist_ok=True)
    _mode = "a" if os.path.exists(_console) and os.path.getsize(_console) < 5e6 else "w"
    sys.stdout = sys.stderr = open(_console, _mode, encoding="utf-8", buffering=1)

import numpy as np  # noqa: E402

import config
import dashboard
import events
import winapi
import worker
from brain import Brain, approved, read_notes, read_state, summarize, write_state
from ears import Ears, FRAME_S, RATE
from mouth import Mouth

IDLE, LISTEN, BUSY = "idle", "listening", "busy"
NEW_SESSION = re.compile(r"^\W*(please\W+)?(start\W+)?(a\W+)?(new|fresh)\W+(session|chat|conversation|start)\W*(please)?\W*$"
                         r"|^\W*fresh start\W*$", re.I)
WAKE_PHRASE = re.compile(r"^\s*hey[\s,.;:-]+jarvis\b[\s,.:;!?-]*(.*)$", re.I)
IDLE_WAKE_END_BLOCKS = 8                    # tolerate the pause between "Hey" and "Jarvis"
IDLE_WAKE_PREROLL_BLOCKS = 8                # retain 640 ms before VAD opens, including soft consonants
IDLE_WAKE_MIN_RMS = 32                       # lower than the OpenWakeWord gate for ordinary speech
IDLE_WAKE_VAD_THRESHOLD = 0.15
IDLE_WAKE_MAX_BLOCKS = int(6 / FRAME_S)     # bound local fallback transcription to a short clip


def time_tag():
    """[Mon 28 Sep, 07:42, first today] so the brain can greet, welcome back and notice late nights."""
    now, st = datetime.now(), read_state()
    day = (now - timedelta(hours=4)).date().isoformat()     # a late night still counts as the day before
    gap = time.time() - st.get("last_heard", time.time())
    extra = ", first today" if st.get("last_heard_day") != day else (
        f", back after about {round(gap / 3600)} hours" if gap > 3 * 3600 else "")
    write_state(last_heard_day=day, last_heard=time.time())
    return f"[{now:%a %d %b, %H:%M}{extra}]"


log = logging.getLogger("jarvis")


STEP_WORDS = {"screenshot": "Looking at the screen", "click_at": "Clicking", "move_mouse": "Moving the mouse",
              "scroll": "Scrolling", "type_text": "Typing", "press_keys": "Pressing keys", "open_app": "Opening an app",
              "focus_window": "Switching windows", "list_windows": "Checking windows", "media": "Media controls",
              "volume": "Volume", "run_command": "Command", "write_file": "Writing", "start_worker": "Starting a worker",
              "annotate": "Pointing on screen"}


def step_words(ev):
    """'Command: Check the weather' rather than 'Bash'."""
    short = ev["name"]
    name = STEP_WORDS.get(short, short.replace("_", " ").capitalize())
    try:
        inp = json.loads(ev.get("input") or "{}") or {}
    except ValueError:
        inp = {}
    detail = inp.get("description") or inp.get("query") or inp.get("url") or inp.get("name") or ""
    if short == "write_file" and inp.get("path"):
        detail = os.path.basename(inp["path"])
    if short in ("screenshot", "click_at", "move_mouse", "scroll", "annotate"):
        detail = ""
    return f"{name}: {detail}" if detail else name


class Jarvis:
    def __init__(self):
        self.loop = asyncio.get_running_loop()
        self.ears = Ears(self.loop)
        self.mouth = Mouth(self.loop)
        self.brain = Brain(confirm=self.confirm, on_sentence=self.speak)
        self._state = IDLE
        self.recorder = None
        self.listen_target = None     # None = a command for the brain, a Future = a yes/no answer
        self.pending_confirm = None
        self.pending_question = None
        self.processing = False
        self.inbox = asyncio.Queue()
        self.turn = None              # what's being worked on right now
        self.started = time.time()
        self.level = 0.0
        self.wake_score = 0.0
        self.zero_blocks = 0              # audio that is exactly silent: a blocked or muted mic, not a quiet room
        self.told_mic_silent = False
        self.brain_lock = asyncio.Lock()   # held while asking, and while swapping sessions
        self.confirm_lock = asyncio.Lock()     # one spoken yes/no at a time (Jarvis and workers share it)
        worker.confirm = self.confirm
        worker.notify = lambda message: self.inbox.put_nowait(("worker", message))
        self.last_activity = time.time()
        self.resetting = False
        self.listen_id = 0
        self.talk_frames = collections.deque(maxlen=15)
        self.talk_run = 0
        self.partial_busy = False
        self.wake_preroll = collections.deque(maxlen=IDLE_WAKE_PREROLL_BLOCKS)
        self.wake_clip = []
        self.wake_quiet_blocks = 0
        self.wake_fallback_busy = False
        self.mouth.on_sentence_start = self.sentence_started
        self.live = {"you": "", "said": "", "step": "", "self_started": False}
        events.listen(self.track_live)

    @property
    def state(self):
        return self._state

    @state.setter
    def state(self, value):
        if value != self._state:
            self._state = value
            events.emit("state", state=value)

    async def run(self):
        events.load()
        self.brain.start()
        self.mouth.start()
        self.ears.start()
        await dashboard.start(self)
        if os.name == "nt":
            keys = {config.HOTKEY_LISTEN: "listen", config.HOTKEY_STOP: "stop"}
            winapi.listen_hotkeys(self.loop, {k: lambda c=c: self.loop.create_task(self.command(c))
                                              for k, c in keys.items() if k})
        events.emit("started", mic=self.ears.mic_name, voice=config.VOICE, whisper=config.WHISPER_MODEL,
                    model=config.LLM_MODEL, dashboard=f"http://127.0.0.1:{dashboard.PORT}")
        self.mouth.chime("wake")
        self.mouth.say(f"Online and ready, {config.HONORIFIC}.")
        log.info("ready; open the dashboard with: python jarvisctl.py dashboard")
        await asyncio.gather(self.audio_loop(), self.inbox_loop(), self.idle_watch())

    # ---------- sessions and notes ----------

    async def new_session(self, reason):
        async with self.brain_lock:
            self.resetting = True
            events.emit("session_reset", reason=reason, turns=self.brain.session_turns)
            try:
                await self.save_notes()
                self.brain.start()
            finally:
                self.resetting = False
        self.last_activity = time.time()

    async def save_notes(self):
        if not self.brain.session_turns:
            return
        try:
            await asyncio.wait_for(summarize(self.brain.agent.messages), 240)
        except Exception as e:
            log.exception("summary failed")
            events.emit("error", where="notes", error=str(e))

    def background_jobs(self):
        return bool(worker.as_tasks()) or any(p["role"] == "job" for p in dashboard.processes())

    async def idle_watch(self):
        while True:
            await asyncio.sleep(60)
            idle_for = time.time() - self.last_activity
            if (self.brain.session_turns and idle_for > config.SESSION_IDLE_RESET_MIN * 60 and self.state == IDLE
                    and not self.processing and not self.mouth.busy and self.inbox.empty()
                    and not self.background_jobs()):
                log.info("idle %.0f min, starting a fresh session", idle_for / 60)
                await self.new_session("idle")

    # ---------- listening ----------

    def begin_listen(self, target, wait, prefix=None):
        self.listen_id += 1
        self.recorder = self.ears.recorder(wait)
        if prefix:                                 # he'd already started talking
            self.recorder.speaking = True
            self.recorder.frames = list(prefix)
        self.listen_target = target
        self.state = LISTEN
        log.info("listening%s", " for yes/no" if target else "")

    async def wake(self, how="wake word", command=None):
        if how == "button":
            self.ears.finish_wake_attempt("manual trigger")
            self.clear_idle_wake_audio()
        events.emit("wake", how=how, score=round(self.wake_score, 2))
        if self.pending_confirm and not self.pending_confirm.done():
            self.pending_confirm.set_result("")
        if self.processing or self.mouth.busy:
            log.info("barge-in")
            events.emit("barge_in")
            self.mouth.stop()
            if self.processing:
                await self.brain.interrupt()
        self.mouth.chime("wake")
        if command:
            self.state = BUSY
            self.inbox.put_nowait(("voice", command))
        else:
            self.begin_listen(None, config.WAIT_FOR_SPEECH_S)

    def clear_idle_wake_audio(self):
        self.wake_preroll.clear()
        self.wake_clip.clear()
        self.wake_quiet_blocks = 0

    def collect_idle_wake_audio(self, chunk, rms, vad_score):
        """Buffer idle speech briefly for a local STT fallback when the acoustic wake model misses."""
        if config.STT_ENGINE != "whisper":
            return
        if self.wake_fallback_busy:
            self.wake_preroll.clear()
            return
        speech = rms >= IDLE_WAKE_MIN_RMS and vad_score >= IDLE_WAKE_VAD_THRESHOLD
        if speech:
            if not self.wake_clip:
                self.wake_clip = list(self.wake_preroll)
            self.wake_clip.append(chunk.copy())
            self.wake_quiet_blocks = 0
            if len(self.wake_clip) >= IDLE_WAKE_MAX_BLOCKS:
                self.finish_idle_wake_audio()
            return
        if not self.wake_clip:
            self.wake_preroll.append(chunk.copy())
            return
        self.wake_clip.append(chunk.copy())
        self.wake_quiet_blocks += 1
        if self.wake_quiet_blocks >= IDLE_WAKE_END_BLOCKS:
            self.finish_idle_wake_audio()
        self.wake_preroll.append(chunk.copy())

    def finish_idle_wake_audio(self):
        if len(self.wake_clip) < 5:
            self.clear_idle_wake_audio()
            return
        audio = np.concatenate(self.wake_clip)
        self.clear_idle_wake_audio()
        self.wake_fallback_busy = True
        self.loop.create_task(self.check_idle_wake_phrase(audio))

    async def check_idle_wake_phrase(self, audio):
        try:
            text = await self.ears.partial(audio)
            match = WAKE_PHRASE.match(text or "")
            if not match:
                log.info("local wake fallback found no Hey Jarvis phrase in %.2f s of speech",
                         len(audio) / RATE)
                return
            if self.state != IDLE or self.processing or self.mouth.busy:
                return
            command = match.group(1).strip()
            log.info("local speech fallback recognized Hey Jarvis%s",
                     " followed by a command" if command else "")
            await self.wake(how="local speech fallback", command=command or None)
        except Exception:
            log.exception("local wake phrase fallback failed")
        finally:
            self.wake_fallback_busy = False

    async def audio_loop(self):
        while True:
            chunk = await self.ears.q.get()
            rms = float(np.sqrt(np.mean(chunk.astype(np.float32) ** 2)))
            self.level = max(self.level * 0.6, rms / 32768)
            self.zero_blocks = 0 if chunk.any() else self.zero_blocks + 1
            if self.zero_blocks == 63:                 # 5 s of digital silence
                self.mic_silent()
            if self.state != LISTEN:
                woke = self.ears.heard_wake_word(chunk)
                self.wake_score = self.ears.last_score      # before wake(), so its event has this frame's score
                if woke:
                    self.clear_idle_wake_audio()
                    await self.wake()
                    continue
                if self.state == IDLE and not self.processing and not self.mouth.busy:
                    vad_score = self.ears.vad.predict(chunk, frame_size=640)
                    self.collect_idle_wake_audio(chunk, rms, vad_score)
                else:
                    self.clear_idle_wake_audio()
                if config.BARGE_IN_BY_VOICE and self.mouth.busy:
                    await self.check_talk_over(chunk)
                else:
                    self.talk_frames.clear()
                    self.talk_run = 0
                continue
            result = self.recorder.feed(chunk)
            if result is None:
                if self.recorder.speaking and not self.partial_busy and len(self.recorder.frames) % 8 == 0:
                    self.partial_busy = True     # claimed here: a backlog of queued audio runs through without yielding
                    self.loop.create_task(self.live_partial(self.listen_id, list(self.recorder.frames)))
                continue
            target, self.listen_target = self.listen_target, None
            self.ears.reset_wake()
            self.state = BUSY if (self.processing or target) else IDLE
            if isinstance(result, str):          # nobody spoke
                log.info("heard nothing")
                events.emit("heard", text="", confirm=bool(target))
                if target and not target.done():
                    target.set_result("")
                continue
            self.state = BUSY
            self.loop.create_task(self.handle_audio(result, target, self.listen_id))

    def mic_silent(self):
        msg = (f"The microphone ({self.ears.mic_name}) is sending pure silence: Windows is blocking it or it is muted. "
               "Check Settings > Privacy & security > Microphone (both switches on) and the mic's mute.")
        log.warning(msg)
        events.emit("error", where="mic", error=msg)
        if not self.told_mic_silent:
            self.told_mic_silent = True
            self.mouth.say(f"My microphone is giving me pure silence, {config.HONORIFIC}. "
                           "Windows may be blocking it; the log has the details.")

    async def live_partial(self, listen_id, frames):
        try:
            text = await self.ears.partial(np.concatenate(frames))
            if text and listen_id == self.listen_id and self.state == LISTEN:
                events.emit("partial", text=text, listen_id=listen_id)
        finally:
            self.partial_busy = False

    async def check_talk_over(self, chunk):
        """The user talking while Jarvis is speaking: stop talking and listen, keeping what they've said so far."""
        self.talk_frames.append(chunk)
        p = self.ears.vad.predict(chunk, frame_size=640)
        self.talk_run = self.talk_run + 1 if p > 0.6 else 0
        if self.talk_run < 4:                      # about a third of a second of real speech
            return
        prefix = list(self.talk_frames)
        self.talk_frames.clear()
        self.talk_run = 0
        answering = self.pending_confirm is not None and not self.pending_confirm.done()
        log.info("talked over (%s)", "answering the question" if answering else "barge-in")
        events.emit("barge_in", how="voice")
        self.mouth.stop()
        if answering:                              # he answered before the question finished
            self.begin_listen(self.pending_confirm, config.CONFIRM_WAIT_S, prefix)
            return
        if self.processing:
            await self.brain.interrupt()
        self.begin_listen(None, config.WAIT_FOR_SPEECH_S, prefix)

    async def handle_audio(self, audio, target, listen_id):
        t = time.time()
        try:
            text = await self.ears.transcribe(audio)
        except Exception as e:
            log.exception("speech to text failed")
            events.emit("error", where="speech to text", error=str(e))
            text = ""
        if listen_id != self.listen_id:          # Stop, or a new "hey jarvis", came while this was transcribing
            log.info("dropped what was heard before that: %s", text or "(nothing)")
            if target and not target.done():
                target.set_result("")
            if self.state == BUSY and not self.processing:
                self.state = IDLE
            return
        log.info("heard: %s", text or "(nothing)")
        events.emit("heard", text=text, confirm=bool(target), listen_id=self.listen_id, seconds=round(len(audio) / 16000, 1),
                    stt_s=round(time.time() - t, 2))
        if target:
            if not target.done():
                target.set_result(text)
        elif text:
            self.inbox.put_nowait(("voice", text))
        else:
            self.mouth.chime("error")
            self.state = BUSY if self.processing else IDLE

    # ---------- thinking and talking ----------

    async def inbox_loop(self):
        while True:
            source, text = await self.inbox.get()
            self.last_activity = time.time()
            if NEW_SESSION.match(text):
                self.processing, self.state = True, BUSY
                self.speak(f"Very good, {config.HONORIFIC}. Filing my notes and starting a fresh session.")
                await self.new_session("asked")
                self.speak("Fresh session ready. Awaiting instructions.")
                await self.mouth.wait_done()
                self.processing, self.state = False, IDLE
                continue
            self.processing, self.state = True, BUSY
            self.turn = {"source": source, "text": text, "started": time.time(), "first_speech": None}
            events.emit("turn_start", source=source, text=text)
            prompt = text if source == "worker" else time_tag() + " " + (text if source == "voice" else f"[typed] {text}")
            print(f"\nYou: {text}", flush=True)
            try:
                async with self.brain_lock:
                    await self.brain.ask(prompt)
            except Exception as e:
                log.exception("brain failed")
                events.emit("error", where="brain", error=str(e))
                if str(e).startswith("model error 503:"):
                    self.mouth.say(f"The model service is busy right now, {config.HONORIFIC}. Please try again in a moment.")
                else:
                    self.mouth.say(f"I'm afraid something went wrong on my end, {config.HONORIFIC}.")
            await self.mouth.wait_done()
            self.processing, self.turn = False, None
            self.last_activity = time.time()
            if self.state == BUSY:               # not interrupted by a new "hey jarvis"
                if source == "voice" and self.inbox.empty():
                    self.begin_listen(None, config.FOLLOW_UP_S)
                else:
                    self.state = IDLE

    def speak(self, sentence):
        print(f"Jarvis: {sentence}", flush=True)
        events.emit("say", text=sentence)
        self.mouth.say(sentence)

    def sentence_started(self, text):
        """A sentence has started playing: first_speech is when you actually hear him, not when the text arrived."""
        events.emit("speaking_now", text=text)
        if self.turn and self.turn["first_speech"] is None:
            self.turn["first_speech"] = time.time()
            events.emit("first_speech", after_s=round(time.time() - self.turn["started"], 2))

    async def confirm(self, question):
        async with self.confirm_lock:
            return await self._confirm(question)

    async def _confirm(self, question):
        await self.mouth.wait_done()
        # the future exists before he hears the question, so a "yes" said over it still counts
        fut = self.pending_confirm = self.loop.create_future()
        self.pending_question = question
        answer = ""
        try:
            self.speak(question)
            events.emit("confirm_ask", question=question)
            await self.mouth.wait_done()
            already = self.state == LISTEN and self.listen_target is fut
            if not fut.done() and not already:
                self.mouth.chime("wake")
                self.begin_listen(fut, config.CONFIRM_WAIT_S)
            try:
                answer = await asyncio.wait_for(fut, config.CONFIRM_WAIT_S + 30)
            except asyncio.TimeoutError:
                pass
        finally:                              # also when Stop cancels the request mid-question: no ghost question
            if self.pending_confirm is fut:
                self.pending_confirm = self.pending_question = None
            if self.state == LISTEN and self.listen_target is fut:
                self.listen_target, self.state = None, BUSY if self.processing else IDLE
            elif self.state == BUSY and not self.processing:
                self.state = IDLE             # the question came from a background job
        ok = approved(answer)
        log.info("confirm %r -> %s", answer, ok)
        events.emit("confirm_answer", question=question, answer=answer, approved=ok)
        return ok

    # ---------- commands (jarvisctl and the dashboard) ----------

    async def command(self, cmd, text=""):
        if cmd == "say":
            if not text.strip():
                return "nothing to send"
            self.inbox.put_nowait(("typed", text.strip()))
        elif cmd == "listen":
            await self.wake(how="button")
        elif cmd == "stop":
            self.mouth.stop()
            self.listen_id += 1                   # anything still being transcribed is dropped too
            if self.pending_confirm and not self.pending_confirm.done():
                self.pending_confirm.set_result("no")
            if self.processing:
                await self.brain.interrupt()
            if self.state == LISTEN:
                self.listen_target, self.state = None, BUSY if self.processing else IDLE
            events.emit("stopped_by_user")
        elif cmd in ("yes", "no"):
            if not (self.pending_confirm and not self.pending_confirm.done()):
                return "nothing is waiting for a yes/no"
            self.pending_confirm.set_result(cmd)
            if self.state == LISTEN and self.listen_target is self.pending_confirm:
                self.listen_target, self.state = None, BUSY
                self.ears.reset_wake()
        elif cmd == "new":
            if self.processing:
                return "busy right now; try again when he's finished"
            self.inbox.put_nowait(("typed", "new session"))
        elif cmd == "speak":
            self.mouth.say(text)
        elif cmd == "status":
            return json.dumps({"state": self.state, "processing": self.processing, "speaking": self.mouth.busy,
                               "queued": self.inbox.qsize(), "waiting_for_yes_no": bool(self.pending_confirm)})
        else:
            return f"unknown command {cmd}"
        return "ok"

    def activity(self):
        if self.pending_question:
            return "waiting"
        if self.state == LISTEN:
            return "listening"
        if self.mouth.busy:
            return "speaking"
        if self.processing:
            return "thinking"
        return "idle"

    def meter(self):
        return {"level": round(min(1.0, self.level * 8), 3), "wake": round(self.wake_score, 2),
                "activity": self.activity()}

    def track_live(self, ev):
        """Keep the few things the widget shows, updated from the event stream."""
        k, live = ev["kind"], self.live
        if k == "wake":
            live.update(you="", step="")
        elif k == "partial":
            live["you"] = ev["text"]
        elif k == "heard":
            live["you"] = ev["text"] or "(didn't catch that)"
        elif k == "turn_start":
            live.update(said="", step="", self_started=ev.get("source") == "worker")
            if ev.get("source") != "worker":
                live["you"] = ev.get("text", "")
        elif k == "tool_use" and not ev.get("sub"):
            live["step"] = step_words(ev)
        elif k == "speaking_now":
            live["said"] = ev["text"]
        elif k == "confirm_answer":
            live["you"] = ev.get("answer") or "(no answer)"
        elif k in ("barge_in", "stopped_by_user"):
            live["said"] = ""

    def live_view(self):
        return {"activity": self.activity(), "mic": round(min(1.0, self.level * 8), 3),
                "out": round(min(1.0, self.mouth.out_level * 5), 3), "question": self.pending_question or "",
                "tasks": [{"task_id": t["task_id"], "description": t.get("description", ""), "started": t["started"]}
                          for t in worker.as_tasks()], "now": time.time(), **self.live}

    def pulse(self):
        return {"kind": "pulse", "activity": self.activity(), "mic": round(min(1.0, self.level * 8), 3),
                "out": round(min(1.0, self.mouth.out_level * 5), 3)}

    def snapshot(self):
        return {"activity": self.activity(), "state": self.state, "turn": self.turn,
                "question": self.pending_question, "queued": self.inbox.qsize(),
                "tasks": worker.as_tasks(), "brain": self.brain.info, "context": self.brain.context,
                "session_id": self.brain.session_id, "started": self.started, "now": time.time(),
                "session": {"started": self.brain.session_started, "turns": self.brain.session_turns,
                            "reset_after_min": config.SESSION_IDLE_RESET_MIN, "last_activity": self.last_activity,
                            "resetting": self.resetting},
                "notes": {"text": read_notes(), "updated": os.path.getmtime(config.NOTES_FILE)
                          if os.path.exists(config.NOTES_FILE) else None},
                "config": {"model": config.LLM_MODEL,
                           "provider": "Antigravity subscription" if config.LLM_PROVIDER == "antigravity" else config.LLM_BASE_URL,
                           "voice": config.VOICE,
                           "whisper": config.WHISPER_MODEL if config.STT_ENGINE == "whisper" else config.STT_MODEL,
                           "wake_threshold": config.WAKE_THRESHOLD,
                           "mic": self.ears.mic_name}}



async def main():
    os.makedirs(os.path.dirname(config.LOG_FILE), exist_ok=True)
    handlers = [logging.handlers.RotatingFileHandler(config.LOG_FILE, maxBytes=2_000_000, backupCount=3, encoding="utf-8")]
    handlers += [logging.StreamHandler()] if CONSOLE else []
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s: %(message)s", handlers=handlers)
    jarvis = Jarvis()
    loop, stop = asyncio.get_running_loop(), asyncio.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:            # Windows
            signal.signal(sig, lambda *_: loop.call_soon_threadsafe(stop.set))
    task = asyncio.create_task(jarvis.run())
    await asyncio.wait([task, asyncio.create_task(stop.wait())], return_when=asyncio.FIRST_COMPLETED)
    crashed = task.done() and not task.cancelled() and task.exception()
    if crashed:
        log.error("crashed", exc_info=crashed)
    events.emit("stopping")
    task.cancel()
    jarvis.ears.stream.close()
    jarvis.mouth.stream.close()
    await jarvis.brain.interrupt()
    await worker.stop_all()
    await jarvis.save_notes()
    await jarvis.brain.close()
    return bool(crashed)


if __name__ == "__main__":
    sys.exit(1 if asyncio.run(main()) else 0)     # a crash exits non-zero, so Task Scheduler sees a failure
