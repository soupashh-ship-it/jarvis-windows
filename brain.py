"""The brain: any chat model behind an OpenAI-compatible endpoint, a small tool loop, and a spoken permission gate."""
import asyncio
import base64
import collections
import json
import logging
import os
import re
import shutil
import subprocess
import string
import tempfile
import time
import uuid

import aiohttp

import config
import events
import pctools
import winapi
from mouth import SentenceSplitter

log = logging.getLogger("brain")


def persona():
    """persona.md with your name, honorific and city filled in."""
    with open(config.PERSONA_FILE, encoding="utf-8") as f:
        return string.Template(f.read()).safe_substitute(
            USER_NAME=config.USER_NAME, HONORIFIC=config.HONORIFIC, CITY=config.CITY,
            LINES_FILE=os.path.join(config.JARVIS_DIR, "lines.md"))


# ---------- the model ----------

def ids_of(calls):
    return [c["id"] for c in calls]


_TEXT_TOOL = re.compile(r"```(?:json)?\s*(\{.*?\})\s*```", re.S)


def _text_tool_calls(said):
    """Some OpenAI-compatible proxies (e.g. Antigravity bridges) return tool calls as text
    JSON (```json {"name": ..., "arguments": {...}}``` or {"tool_calls": [...]}) instead of
    structured delta.tool_calls. Convert those so the agent loop can run them."""
    calls = []

    def add(name, args):
        if not isinstance(name, str) or not name:
            return
        try:
            argstr = json.dumps(args) if isinstance(args, dict) else str(args or "{}")
        except Exception:
            argstr = "{}"
        calls.append({"id": "call_" + uuid.uuid4().hex[:12], "type": "function",
                      "function": {"name": name, "arguments": argstr}})

    cleaned = said
    for m in _TEXT_TOOL.finditer(said):
        try:
            obj = json.loads(m.group(1))
        except ValueError:
            continue
        items = obj.get("tool_calls") if isinstance(obj, dict) and isinstance(obj.get("tool_calls"), list) else [obj]
        before = len(calls)
        for it in items:
            if not isinstance(it, dict):
                continue
            fn = it.get("function") if isinstance(it.get("function"), dict) else it
            add(fn.get("name"), fn.get("arguments", {}))
        if len(calls) > before:
            cleaned = cleaned.replace(m.group(0), " ")
    return calls, re.sub(r"\s+", " ", cleaned).strip()


async def chat(messages, tools=None, on_text=None, model=None):
    """One streamed chat completion. Returns the assistant message: its text plus any tool calls."""
    body = {"model": model or config.LLM_MODEL, "messages": messages, "stream": True}
    if tools:
        body["tools"] = tools
    headers = {"Authorization": f"Bearer {config.LLM_API_KEY}"} if config.LLM_API_KEY else {}
    said, calls = "", []
    timeout = aiohttp.ClientTimeout(sock_connect=15, sock_read=300)
    url = config.LLM_BASE_URL.rstrip("/") + "/chat/completions"
    async with aiohttp.ClientSession(timeout=timeout) as s:
        for attempt in range(3):
            r = await s.post(url, json=body, headers=headers)
            if r.status == 503 and attempt < 2:
                await r.read()
                retry_after = r.headers.get("Retry-After")
                try:
                    delay = min(5.0, max(0.5, float(retry_after))) if retry_after else 1.0 + attempt
                except ValueError:
                    delay = 1.0 + attempt
                r.release()
                log.warning("model returned HTTP 503; retrying attempt %d/3 in %.1fs", attempt + 2, delay)
                await asyncio.sleep(delay)
                continue
            async with r:
                if r.status != 200:
                    raise RuntimeError(f"model error {r.status}: {(await r.text())[:500]}")
                async for line in r.content:
                    line = line.decode("utf-8", "replace").strip()
                    if not line.startswith("data:") or line[5:].strip() == "[DONE]":
                        continue
                    chunk = json.loads(line[5:])
                    if chunk.get("error"):
                        raise RuntimeError(f"model error: {chunk['error']}")
                    delta = ((chunk.get("choices") or [{}])[0]).get("delta") or {}
                    if delta.get("content"):
                        said += delta["content"]
                        if on_text:
                            on_text(delta["content"])
                    for tc in delta.get("tool_calls") or []:
                        f, i = tc.get("function") or {}, tc.get("index")
                        if i is None:   # Gemini leaves the index out (and may repeat or blank the id): a name starts a new call
                            ids = ids_of(calls)
                            i = (len(calls) if f.get("name") or not calls else ids.index(tc["id"]) if tc.get("id") in ids
                                 else len(calls) if tc.get("id") else len(calls) - 1)
                        while len(calls) <= i:
                            calls.append({"id": "", "type": "function", "function": {"name": "", "arguments": ""}})
                        c = calls[i]
                        if not c["id"]:                     # every call needs its own id for its tool message
                            c["id"] = tc["id"] if tc.get("id") and tc["id"] not in ids_of(calls) else "call_" + uuid.uuid4().hex[:12]
                        c["function"]["name"] = c["function"]["name"] or f.get("name") or ""
                        c["function"]["arguments"] += f.get("arguments") or ""
                        # anything else rides along unchanged: Gemini 3 rejects the next request unless its
                        # extra_content.google.thought_signature comes back on the call
                        c.update({k: v for k, v in tc.items() if k not in ("index", "id", "type", "function")})
            break
    if not calls and said:                       # proxy sent tools as text JSON: convert them
        tcalls, said = _text_tool_calls(said)
        if tcalls:
            log.info("text tool calls: %s", [c["function"]["name"] for c in tcalls])
            calls = tcalls
    msg = {"role": "assistant", "content": said or ""}  # "" not None: some proxies reject null content; empty replies are dropped by Agent
    if calls:
        msg["tool_calls"] = calls
    return msg


class _JSONTextStreamer:
    """Stream the top-level JSON `text` string as it is generated, decoding JSON escapes."""

    def __init__(self, callback):
        self.callback = callback
        self.raw = ""
        self.pos = 0
        self.value_start = None
        self.escaped = False
        self.unicode_digits = None
        self.pending_high_surrogate = None
        self.text = ""
        self.done = False

    def _emit_char(self, char, out):
        code = ord(char)
        if self.pending_high_surrogate is not None:
            high = ord(self.pending_high_surrogate)
            if 0xDC00 <= code <= 0xDFFF:
                out.append(chr(0x10000 + ((high - 0xD800) << 10) + code - 0xDC00))
                self.pending_high_surrogate = None
                return
            out.append(self.pending_high_surrogate)
            self.pending_high_surrogate = None
        if 0xD800 <= code <= 0xDBFF:
            self.pending_high_surrogate = char
        else:
            out.append(char)

    def feed(self, delta):
        if self.done or not delta:
            return
        self.raw += delta
        if self.value_start is None:
            start = len(self.raw) - len(self.raw.lstrip())
            if not self.raw[start:].startswith("{"):
                return
            match = re.search(r'"text"\s*:\s*"', self.raw[start + 1:])
            if not match:
                return
            self.value_start = start + 1 + match.end()
            self.pos = self.value_start

        out = []
        while self.pos < len(self.raw):
            char = self.raw[self.pos]
            self.pos += 1
            if self.unicode_digits is not None:
                self.unicode_digits += char
                if len(self.unicode_digits) == 4:
                    try:
                        self._emit_char(chr(int(self.unicode_digits, 16)), out)
                    except ValueError:
                        pass
                    self.unicode_digits = None
                continue
            if self.escaped:
                self.escaped = False
                if char == "u":
                    self.unicode_digits = ""
                else:
                    self._emit_char({"\"": "\"", "\\": "\\", "/": "/", "b": "\b",
                                     "f": "\f", "n": "\n", "r": "\r", "t": "\t"}.get(char, char), out)
                continue
            if char == "\\":
                self.escaped = True
            elif char == '"':
                self.done = True
                if self.pending_high_surrogate is not None:
                    out.append(self.pending_high_surrogate)
                    self.pending_high_surrogate = None
                break
            else:
                self._emit_char(char, out)
        if out:
            piece = "".join(out)
            self.text += piece
            self.callback(piece)


class AntigravitySession:
    """A persistent official Antigravity CLI session using the signed-in subscription."""

    RESPONSE_RULES = """You are Jarvis's model backend. Jarvis, not this CLI, owns all user-facing actions and tool execution.
Never use Antigravity tools to operate the user's computer, browse, run commands, or edit files. Return exactly one JSON object with:
- text: the words Jarvis should say (a string; empty when requesting a tool)
- tool_calls: an array of {name, arguments} objects, using only the Jarvis tools supplied in the request; arguments must be an object
Use an empty tool_calls array when you can answer. Do not use markdown fences or add text outside the JSON object.
If a Jarvis message includes a screenshot path, inspect only that image with the read-only view_file tool, then return the JSON object. Do not inspect any other files."""

    def __init__(self, model=None):
        self.model = model or config.LLM_MODEL
        self.proc = None
        self.workdir = None
        self._queue = asyncio.Queue()
        self._ready = asyncio.Event()
        self._start_lock = asyncio.Lock()
        self._turn_lock = asyncio.Lock()
        self._reader_task = None
        self._stderr_task = None
        self._stderr_tail = collections.deque(maxlen=30)
        self._startup_error = None
        self._messages_sent = 0
        self._turns = 0
        self._prompt_chars = 0

    @staticmethod
    def _executable():
        configured = getattr(config, "ANTIGRAVITY_CLI", "") or os.environ.get("JARVIS_ANTIGRAVITY_CLI", "")
        candidates = [configured, shutil.which("agy")]
        local_app_data = os.environ.get("LOCALAPPDATA")
        if local_app_data:
            candidates.append(os.path.join(local_app_data, "agy", "bin", "agy.exe"))
        for path in candidates:
            if path and (os.path.isfile(path) if os.path.dirname(path) else shutil.which(path)):
                return path
        raise RuntimeError("Antigravity CLI (agy) was not found. Install it and sign in with your Antigravity account.")

    def _prepare_workspace(self):
        self.workdir = tempfile.mkdtemp(prefix="jarvis-antigravity-")
        agent_dir = os.path.join(self.workdir, ".agents", "agents", "jarvis-model")
        os.makedirs(agent_dir, exist_ok=True)
        agent = """---
name: jarvis-model
description: Jarvis voice assistant response model
tools:
  - view_file
mainAgent: true
subagent: false
commandExecutionPolicy: off
---
Produce structured response text for Jarvis. Do not take actions with CLI tools. Only inspect screenshot paths explicitly supplied by Jarvis, using view_file.
"""
        with open(os.path.join(agent_dir, "agent.md"), "w", encoding="utf-8", newline="\n") as f:
            f.write(agent)

    async def start(self):
        async with self._start_lock:
            if self.proc and self.proc.returncode is None:
                if not self._ready.is_set():
                    await asyncio.wait_for(self._ready.wait(), 60)
                if self._startup_error:
                    raise self._startup_error
                return

            self._queue = asyncio.Queue()
            self._ready = asyncio.Event()
            self._startup_error = None
            self._stderr_tail.clear()
            self._messages_sent = self._turns = self._prompt_chars = 0
            self._prepare_workspace()
            args = [self._executable(), "--input-format", "stream-json", "--output-format", "stream-json",
                    "--model", self.model, "--agent", "jarvis-model"]
            create_kwargs = {}
            if os.name == "nt":
                create_kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
            env = os.environ.copy()
            env.pop("JARVIS_LLM_API_KEY", None)
            try:
                self.proc = await asyncio.create_subprocess_exec(
                    *args, cwd=self.workdir, stdin=asyncio.subprocess.PIPE,
                    stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, env=env, **create_kwargs)
            except Exception:
                shutil.rmtree(self.workdir, ignore_errors=True)
                self.workdir = None
                raise
            self._reader_task = asyncio.create_task(self._read_stdout())
            self._stderr_task = asyncio.create_task(self._read_stderr())
            try:
                await asyncio.wait_for(self._ready.wait(), 60)
            except asyncio.TimeoutError as e:
                await self._terminate()
                raise RuntimeError("Antigravity CLI did not become ready within 60 seconds.") from e
            if self._startup_error:
                error = self._startup_error
                await self._terminate()
                raise error
            log.info("Antigravity CLI ready with %s", self.model)

    async def _read_stdout(self):
        try:
            while self.proc and self.proc.stdout:
                line = await self.proc.stdout.readline()
                if not line:
                    break
                try:
                    event = json.loads(line.decode("utf-8", "replace"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    log.warning("Antigravity CLI emitted a non-JSON output line")
                    continue
                if event.get("event") == "init":
                    self._ready.set()
                await self._queue.put(event)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            self._startup_error = RuntimeError(f"Antigravity CLI output failed: {e}")
            log.exception("Antigravity CLI output reader failed")
        finally:
            if not self._ready.is_set():
                self._startup_error = self._startup_error or RuntimeError("Antigravity CLI exited before it became ready.")
                self._ready.set()
            await self._queue.put(None)

    async def _read_stderr(self):
        try:
            while self.proc and self.proc.stderr:
                line = await self.proc.stderr.readline()
                if not line:
                    break
                self._stderr_tail.append(line.decode("utf-8", "replace").rstrip())
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("Antigravity CLI stderr reader failed")

    def _image_path(self, url):
        match = re.match(r"^data:(image/[\w.+-]+);base64,(.*)$", url, re.S)
        if not match:
            return "[image omitted: unsupported screenshot format]"
        mime, encoded = match.groups()
        ext = {"image/jpeg": ".jpg", "image/png": ".png", "image/webp": ".webp"}.get(mime, ".img")
        try:
            data = base64.b64decode(encoded, validate=True)
        except (ValueError, base64.binascii.Error):
            return "[image omitted: invalid image data]"
        path = os.path.join(self.workdir, "jarvis-screen-" + uuid.uuid4().hex + ext)
        with open(path, "wb") as f:
            f.write(data)
        return path

    def _message_for_cli(self, message):
        item = {"role": message.get("role", "user")}
        content = message.get("content", "")
        if isinstance(content, list):
            parts = []
            for block in content:
                if block.get("type") == "text":
                    parts.append(block.get("text", ""))
                elif block.get("type") == "image_url":
                    url = (block.get("image_url") or {}).get("url", "")
                    parts.append("SCREENSHOT PATH (view_file only): " + self._image_path(url))
            item["content"] = "\n".join(parts)
        else:
            item["content"] = content
        if message.get("tool_call_id"):
            item["tool_call_id"] = message["tool_call_id"]
        if message.get("tool_calls"):
            item["tool_calls"] = message["tool_calls"]
        return item

    def _prompt(self, messages, tools):
        if not self._messages_sent:
            normalized = [self._message_for_cli(m) for m in messages]
            payload = {"messages": normalized, "jarvis_tools": tools or []}
            header = "Handle this Jarvis request. The first message with role system is Jarvis's instruction.\n"
        else:
            normalized = [self._message_for_cli(m) for m in messages[self._messages_sent:]]
            payload = {"new_messages": normalized}
            header = "Continue the same Jarvis conversation using these new messages. Previous turns and tool specs remain in context.\n"
        return self.RESPONSE_RULES + "\n\n" + header + json.dumps(payload, ensure_ascii=False)

    async def _read_result(self, on_text=None):
        streamer = _JSONTextStreamer(on_text) if on_text else None
        while True:
            event = await self._queue.get()
            if event is None:
                details = "\n".join(self._stderr_tail)
                suffix = f" Antigravity CLI said: {details[-1200:]}" if details else ""
                raise RuntimeError("Antigravity CLI stopped before returning a response." + suffix)
            if event.get("event") == "step_update" and streamer:
                step = event.get("step_update") or {}
                if step.get("step_type") == "agent_response":
                    streamer.feed(step.get("text_delta", ""))
                continue
            if event.get("event") != "result":
                continue
            result = event.get("result") or {}
            if result.get("status") != "SUCCESS":
                reason = result.get("error") or result.get("response") or "unknown model error"
                details = "\n".join(self._stderr_tail)
                if details:
                    reason += " " + details[-1200:]
                raise RuntimeError(f"Antigravity subscription request failed: {reason[:1800]}")
            return result, streamer.text if streamer else ""

    @staticmethod
    def _decode_response(result):
        response = result.get("response", "") or ""
        structured = result.get("structured_output")
        obj = structured if isinstance(structured, dict) else None
        if obj is None:
            decoder = json.JSONDecoder()
            for match in re.finditer(r"\{", response):
                try:
                    candidate, _ = decoder.raw_decode(response[match.start():])
                except ValueError:
                    continue
                if isinstance(candidate, dict) and ("text" in candidate or "tool_calls" in candidate):
                    obj = candidate
                    break
        if obj is None:
            msg_text = response.strip()
            calls, msg_text = _text_tool_calls(msg_text)
            return {"role": "assistant", "content": msg_text, **({"tool_calls": calls} if calls else {})}

        said = obj.get("text", obj.get("content", ""))
        if not isinstance(said, str):
            said = str(said or "")
        calls = []
        for item in obj.get("tool_calls", obj.get("calls", [])) or []:
            if not isinstance(item, dict):
                continue
            function = item.get("function") if isinstance(item.get("function"), dict) else item
            name = function.get("name")
            args = function.get("arguments", {})
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except ValueError:
                    continue
            if isinstance(name, str) and name and isinstance(args, dict):
                calls.append({"id": "call_" + uuid.uuid4().hex[:12], "type": "function",
                              "function": {"name": name, "arguments": json.dumps(args, ensure_ascii=False)}})
        msg = {"role": "assistant", "content": said}
        if calls:
            msg["tool_calls"] = calls
        return msg

    async def chat(self, messages, tools=None, on_text=None):
        async with self._turn_lock:
            if self._turns >= 40 or self._prompt_chars > 100_000:
                await self._terminate()
            await self.start()
            prompt = self._prompt(messages, tools)
            try:
                self.proc.stdin.write((json.dumps({"event": "user", "message": {"content": prompt}},
                                                 ensure_ascii=False) + "\n").encode("utf-8"))
                await self.proc.stdin.drain()
                self._messages_sent = len(messages)
                result, streamed_text = await self._read_result(on_text)
            except asyncio.CancelledError:
                await self._terminate()
                raise
            except Exception:
                await self._terminate()
                raise
            self._turns += 1
            self._prompt_chars += len(prompt)
            msg = self._decode_response(result)
            if on_text and msg.get("content"):
                if not streamed_text:
                    on_text(msg["content"])
                elif msg["content"].startswith(streamed_text):
                    remaining = msg["content"][len(streamed_text):]
                    if remaining:
                        on_text(remaining)
            return msg

    async def _terminate(self):
        proc = self.proc
        self.proc = None
        if proc and proc.returncode is None:
            try:
                if proc.stdin and not proc.stdin.is_closing():
                    proc.stdin.close()
                await asyncio.wait_for(proc.wait(), 4)
            except (asyncio.TimeoutError, Exception):
                try:
                    proc.kill()
                    await asyncio.wait_for(proc.wait(), 3)
                except Exception:
                    pass
        for task in (self._reader_task, self._stderr_task):
            if task and not task.done():
                task.cancel()
        for task in (self._reader_task, self._stderr_task):
            if task:
                try:
                    await task
                except (asyncio.CancelledError, Exception):
                    pass
        if self.workdir:
            shutil.rmtree(self.workdir, ignore_errors=True)
        self.workdir = None
        self._queue = asyncio.Queue()
        self._ready = asyncio.Event()
        self._reader_task = self._stderr_task = None
        self._startup_error = None
        self._messages_sent = self._turns = self._prompt_chars = 0

    async def close(self):
        async with self._turn_lock:
            async with self._start_lock:
                await self._terminate()

    async def warm(self):
        """Start the CLI process without spending subscription quota on a warm-up prompt."""
        try:
            await self.start()
        except Exception:
            log.exception("could not start the Antigravity CLI session")


def preview(content, n=300):
    """Short text version of a tool result for the dashboard."""
    return " ".join(c["text"] if c["type"] == "text" else "[image]" for c in content)[:n]


DESK = {"owner": None}     # the agent driving the mouse and keyboard; one at a time


class Agent:
    """A conversation with the model and the loop that runs its tool calls. Jarvis is one; each worker is another."""
    MAX_MESSAGES = 200

    def __init__(self, system, permit, tools, on_text=None, model=None, sub=False, name="Jarvis"):
        self.system, self.permit, self.on_text, self.model, self.sub = system, permit, on_text, model, sub
        self.name = name
        self.tools = {f.spec["function"]["name"]: f for f in tools}
        self.messages = []
        self.steps = 0
        self.antigravity = AntigravitySession(model) if config.LLM_PROVIDER == "antigravity" else None
        self.shot = {"x": 0, "y": 0, "scale": 1.0, "agent": name, "sub": sub}   # its own last screenshot's mapping

    def who(self):
        return f"the {self.name} worker" if self.sub else "Jarvis"

    async def close(self):
        if self.antigravity:
            await self.antigravity.close()

    async def warm(self):
        if self.antigravity:
            await self.antigravity.warm()

    async def complete(self, messages):
        if self.antigravity:
            return await self.antigravity.chat(messages, [f.spec for f in self.tools.values()], self.on_text)
        return await chat(messages, [f.spec for f in self.tools.values()], self.on_text, self.model)

    async def run(self, prompt):
        """One request: model, tools, model... until it answers without a tool. Returns its last words."""
        pctools.SHOT.set(self.shot)       # this task's clicks map through this agent's screenshot, not another's
        try:
            return await self._run(prompt)
        finally:
            if DESK["owner"] is self:
                DESK["owner"] = None

    async def _run(self, prompt):
        self.messages.append({"role": "user", "content": prompt})
        self.steps = 0
        while self.steps < config.MAX_STEPS:
            self.trim()
            msg = await self.complete([{"role": "system", "content": self.system}] + self.messages)
            if self.on_text:
                self.on_text("\n")                  # end of a message: speak whatever is left
            if not msg.get("tool_calls"):
                if msg["content"]:                  # an empty reply isn't kept: Gemini rejects empty messages
                    self.messages.append(msg)
                return msg["content"] or ""
            self.messages.append(msg)
            images, answered = [], set()
            try:
                for call in msg["tool_calls"]:
                    if self.steps >= config.MAX_STEPS:
                        break
                    content = await self.call(call)
                    self.messages.append({"role": "tool", "tool_call_id": call["id"], "content": " ".join(
                        c["text"] for c in content if c["type"] == "text") or "done"})
                    answered.add(call["id"])
                    images += [c for c in content if c["type"] == "image"]
            finally:                                # interrupted or over the limit: every tool call still needs an answer
                why = "the step limit was reached" if self.steps >= config.MAX_STEPS else "the user interrupted"
                self.messages += [{"role": "tool", "tool_call_id": c["id"], "content": f"Not run: {why}."}
                                  for c in msg["tool_calls"] if c["id"] not in answered]
            if images and config.LLM_VISION:        # most APIs only take images from the user, so hand them over
                self.messages.append({"role": "user", "content": [{"type": "text", "text": "The screen from that step:"}] + [
                    {"type": "image_url", "image_url": {"url": f"data:{i['mimeType']};base64,{i['data']}"}} for i in images]})
        return "I've reached my step limit for this request."

    async def call(self, call):
        name, self.steps = call["function"]["name"], self.steps + 1
        try:
            args = json.loads(call["function"]["arguments"] or "{}")
        except ValueError:
            args = None
        log.info("tool: %s %s", name, call["function"]["arguments"][:200])
        events.emit("tool_use", tool_id=call["id"], name=name, input=json.dumps(args)[:1500], sub=self.sub,
                    agent=self.name)
        error = True
        if not isinstance(args, dict):
            result = pctools.text(f"The arguments for {name} weren't valid JSON. Try again.")
        elif name not in self.tools:
            result = pctools.text(f"There is no tool called {name}.")
        elif name in pctools.DESKTOP and DESK["owner"] not in (None, self):
            # ponytail: the driver keeps the desktop for its whole request or job; upgrade path is an idle timeout
            result = pctools.text(f"Not run: {DESK['owner'].who()} is using the mouse and keyboard right now. "
                                  "Wait until it has finished, or tell the user it is busy.")
        elif not await self.permit(name, args):
            result = pctools.text("The user said no (or didn't answer). Don't retry it; say so briefly.")
        else:
            if name in pctools.DESKTOP:
                DESK["owner"] = self
            try:
                result, error = await self.tools[name](args), False
            except Exception as e:
                log.exception("tool %s failed", name)
                result = pctools.text(f"{name} failed: {e!r}")
        events.emit("tool_result", tool_id=call["id"], error=error, preview=preview(result["content"]))
        return result["content"]

    def trim(self):
        """Screenshots are big: keep only the newest. Very long sessions lose their oldest requests."""
        shots = [m for m in self.messages if isinstance(m["content"], list)]
        for m in shots[:-1]:
            m["content"] = "(an older screenshot, removed to save space)"
        # ponytail: drops whole old requests past MAX_MESSAGES; upgrade path is summarising them into the notes
        if len(self.messages) > self.MAX_MESSAGES:
            start = len(self.messages) - self.MAX_MESSAGES
            cut = next((i for i in range(start, len(self.messages)) if self.messages[i]["role"] == "user"
                        and isinstance(self.messages[i]["content"], str)), None)
            if cut:
                del self.messages[:cut]


# ---------- the permission gate ----------

# Only count a word as a command where a command goes (start, after ; & | ( { $( or `), so a folder called
# "rm-old" or a search for "kill" doesn't trip the gate.
_CMD = r"(?:^|[;&|({\n`]|\$\(|\b(?:sudo|xargs|then|do|else|exec|nohup|env)\s)\s*"
RISKY_COMMAND = re.compile(
    _CMD + r"(remove-item|ri|rm|rmdir|rd|del|erase|move-item|mi|mv|move|rename-item|rni|ren|copy-item|cpi|cp|copy|"
    r"set-content|sc|sc\.exe|add-content|ac|clear-content|clc|out-file|tee-object|tee|clear-item|clear-recyclebin|"
    r"stop-process|spps|kill|taskkill|stop-computer|restart-computer|shutdown|logoff|"
    r"stop-service|restart-service|set-service|new-service|remove-service|"
    r"schtasks|register-scheduledtask|unregister-scheduledtask|set-scheduledtask|"
    r"reg|reg\.exe|set-itemproperty|sp|new-itemproperty|remove-itemproperty|rp|set-executionpolicy|"
    r"set-acl|icacls|takeown|cipher|format|format-volume|diskpart|bcdedit|netsh|runas|sudo|"
    r"invoke-expression|iex|winget|choco|scoop|msiexec|uninstall-\S+|install-\S+|disable-\S+|enable-\S+|"
    r"new-item|ni|expand-archive|compress-archive|start-bitstransfer|bitsadmin|certutil|start-process|saps|start|"
    r"cmd|cmd\.exe|powershell|powershell\.exe|pwsh|pwsh\.exe|wsl|bash|"
    r"send-mailmessage|ssh|scp|rsync|chmod|chown|dd|truncate|crontab|systemctl)(?=[\s;|)}]|$)"
    r"|" + _CMD + r"git\s+(push|reset|clean|checkout\s+--)"
    r"|-verb\s+runas"
    r"|\b(invoke-webrequest|invoke-restmethod|iwr|irm|curl|wget)\b[^|;\n]*\s-(method\s+['\"]?(post|put|patch|delete)|body|infile|outf\w*)\b"
    r"|\bcurl(\.exe)?\b[^|;\n]*(\s-X\s*['\"]?(POST|PUT|PATCH|DELETE)|\s-[a-z]*(?-i:[oOdFT])|\s--(output|remote-name|data|json|form|upload-file))"
    r"|\[(system\.)?io\.(file|directory)\]::(write|append|delete|move|copy|create|replace|open)"
    r"|\.(downloadfile|uploadfile|uploadstring|uploaddata|uploadvalues)\b"
    r"|(?<![-=2])>(?!&)(?!\s*\$null)", re.I | re.M)


def _norm(p):
    return os.path.normcase(os.path.realpath(p))


_DOCS = {os.path.join(config.HOME, "Documents"), winapi.known_folder(0x05, os.path.join(config.HOME, "Documents"))}
SENSITIVE_PATHS = [os.path.join(config.HOME, p) for p in (".ssh", ".gitconfig")] + [
    winapi.known_folder(0x07, os.path.join(config.HOME, r"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")),
    config.JARVIS_DIR] + [os.path.join(d, p) for d in _DOCS for p in ("WindowsPowerShell", "PowerShell")]
# the PowerShell profiles live in the real Documents folder, which OneDrive often moves; Jarvis's own folder holds its rules
RUNNABLE = {".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi",
            ".msc", ".scr", ".cpl", ".pif", ".lnk", ".url", ".reg", ".appref-ms"}


def policy(name, data):
    """'allow', or 'confirm' (ask out loud first)."""
    if name == "press_keys":
        return "confirm" if re.search(r"\b(enter|return)\b", data.get("keys", ""), re.I) else "allow"
    if name == "run_command":
        return "confirm" if RISKY_COMMAND.search(data.get("command", "")) else "allow"
    if name == "open_path":                         # opening a program or script runs it; a web link just opens the browser
        target = data.get("target", "")
        web = re.match(r"(?i)(https?|mailto):", target)
        return "confirm" if not web and os.path.splitext(target)[1].lower() in RUNNABLE else "allow"
    if name == "write_file":
        path = _norm(os.path.join(config.HOME, os.path.expanduser(data.get("path", ""))))
        roots = [_norm(r) for r in [config.HOME, config.RUNTIME_DIR] + config.WRITE_OK_DIRS]
        outside = not any(path.startswith(r + os.sep) for r in roots)
        sensitive = any(path == _norm(p) or path.startswith(_norm(p) + os.sep) for p in SENSITIVE_PATHS)
        return "confirm" if outside or sensitive else "allow"
    return "allow" if name in pctools.TOOLS else "confirm"


def describe(name, data):
    """What is being approved, from the real arguments; the model's own description only rides along."""
    if name == "run_command":
        cmd, desc = re.sub(r"\s*\n\s*", "; ", data.get("command", "").strip()), data.get("description", "").strip()
        m = RISKY_COMMAND.search(cmd)
        start = m.start() if m and m.end() > 160 else 0            # long: show the part that made it risky
        shown = ("..." if start else "") + cmd[start:start + 160] + ("..." if len(cmd) > start + 160 else "")
        return f"run {shown}" + (f" ({desc[0].lower() + desc[1:]})" if desc else "")
    if name == "write_file":
        path = os.path.normpath(os.path.join(config.HOME, os.path.expanduser(data.get("path", ""))))
        return f"{'overwrite' if os.path.exists(path) else 'create'} the file {path}"
    hints = [str(v) for k, v in data.items() if isinstance(v, str) and 0 < len(v) < 80
             and k in ("name", "title", "query", "text", "keys", "target")][:2]
    return name.replace("_", " ") + (": " + ", ".join(hints) if hints else "")


_AFFIRM = (r"yes|yeah|yep|yup|sure|ok|okay|go ahead|go for it|do it|do so|confirm|confirmed|affirmative|absolutely|"
           r"certainly|of course|please do|proceed|send it|allow it")
_FILLER = r"please|sir|ma'am|maam|boss|jarvis|" + re.escape(" ".join(re.findall(r"[a-z']+", config.HONORIFIC.lower())))


def approved(answer):
    """True only when the whole answer is a plain yes ("Yes.", "Yeah, go ahead, sir."). Anything else is a no:
    "I'm not sure", "not okay", "I can't confirm that", "yes, but not that one", silence."""
    words = " ".join(re.findall(r"[a-z']+", (answer or "").lower()))
    return bool(re.fullmatch(rf"(?:(?:{_FILLER}) )*(?:{_AFFIRM})(?: (?:{_AFFIRM}|{_FILLER}))*", words))


# ---------- notes between sessions ----------

NOTES_TEMPLATE = """# Jarvis notes

Last updated: never

## Open threads

## Recent

## Working with {name}
"""

NOTES_PROMPT = """You keep the running notes file for JARVIS, {name}'s voice assistant. A session just ended; you get the current file and that session's conversation. Rewrite the file so the next session knows what happened. Keep this structure:

# Jarvis notes
Last updated: <YYYY-MM-DD HH:MM>

## Open threads
Things still in progress, follow-ups promised, reminders with their times, anything {name} is waiting on. Remove threads that are finished.

## Recent
One line per thing {name} asked, newest first: "YYYY-MM-DD HH:MM  what they asked. What was done and how it turned out." Keep the last 40 lines. Fold older days into one line per day.

## Working with {name}
Short, lasting lessons about how they like Jarvis to work and quirks of their apps (what worked, what failed, what they corrected). Only add a lesson that will still matter next week.

Rules: only facts from this session or already in the file; nothing invented. Under 150 lines. No em dashes. Reply with the complete new file and nothing else."""


def read_state():
    try:
        with open(config.STATE_FILE, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def write_state(**changes):
    st = read_state() | changes
    with open(config.STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(st, f)


def read_notes():
    try:
        with open(config.NOTES_FILE, encoding="utf-8") as f:
            return f.read()
    except FileNotFoundError:
        return ""


async def summarize(messages):
    """Fold a finished session into the notes file."""
    talk = "\n".join(f"{m['role']}: {m['content']}" for m in messages
                     if m["role"] in ("user", "assistant") and isinstance(m.get("content"), str) and m["content"])
    if not talk:
        return
    events.emit("notes", status="updating")
    prompt = [{"role": "system", "content": NOTES_PROMPT.format(name=config.USER_NAME)},
              {"role": "user", "content": "Current notes file:\n\n" + (read_notes() or NOTES_TEMPLATE.format(
                  name=config.USER_NAME)) + "\n\nThe session that just ended:\n\n" + talk[-60000:]}]
    if config.LLM_PROVIDER == "antigravity":
        provider = AntigravitySession(config.LLM_MODEL)
        try:
            msg = await provider.chat(prompt)
        finally:
            await provider.close()
    else:
        msg = await chat(prompt)
    notes = re.sub(r"(?s)<think>.*?</think>", "", msg["content"] or "").strip().strip("`").strip()
    if not notes.startswith("# Jarvis notes"):         # never overwrite the notes with something else
        raise RuntimeError(f"the model returned something that isn't the notes file: {notes[:120]!r}")
    with open(config.NOTES_FILE, "w", encoding="utf-8") as f:
        f.write(notes + "\n")
    events.emit("notes", status="updated")
    log.info("notes updated")


# ---------- Jarvis's own conversation ----------

class Brain:
    """Jarvis's session: streams sentences to the voice as they arrive, asks before risky steps."""

    def __init__(self, confirm, on_sentence):
        self.confirm = confirm              # async (question) -> bool, asks the user out loud
        self.on_sentence = on_sentence
        self.splitter = SentenceSplitter()
        self.agent = None
        self.job = None
        self.session_id = None
        self.session_started = time.time()
        self.session_turns = 0
        self.info = {"model": config.LLM_MODEL, "tools": len(pctools.TOOLS)}
        self.context = {}

    def start(self):
        notes = read_notes().strip()
        system = persona()
        if notes:
            system += ("\n# Your notes from earlier Jarvis sessions\n"
                       f"Each Jarvis session starts fresh. This is what earlier sessions left you ({config.NOTES_FILE}).\n\n"
                       + notes)
        previous = self.agent
        self.agent = Agent(system, self.gate, list(pctools.TOOLS.values()), on_text=self._text)
        self.splitter = SentenceSplitter()
        if previous:
            asyncio.create_task(previous.close())
        if self.agent.antigravity:
            asyncio.create_task(self.agent.warm())
        self.session_id, self.session_started, self.session_turns = uuid.uuid4().hex[:8], time.time(), 0
        provider = "Antigravity subscription" if config.LLM_PROVIDER == "antigravity" else config.LLM_BASE_URL
        events.emit("brain", status="connected", model=config.LLM_MODEL, provider=provider)
        log.info("new session %s", self.session_id)

    def _text(self, delta):
        for s in self.splitter.feed(delta):
            self.on_sentence(s)

    async def gate(self, name, data):
        verdict = policy(name, data)
        log.info("gate %s -> %s", name, verdict)
        if verdict == "allow":
            return True
        events.emit("gate", tool=name, verdict=verdict, reason="")
        question = f"Shall I {describe(name, data)}?"
        if name == "press_keys":
            w = winapi.active_window()
            question = f"Shall I press Enter in {w['title'] if w else 'the current window'}?"
        return await self.confirm(question)

    async def interrupt(self):
        if self.job and not self.job.done():
            self.job.cancel()

    async def close(self):
        if self.agent:
            await self.agent.close()

    async def ask(self, prompt):
        t = time.time()
        self.job = asyncio.create_task(self.agent.run(prompt))
        await asyncio.wait({self.job})
        if self.job.cancelled():
            self.splitter = SentenceSplitter()   # drop the half sentence he was cut off in
        else:
            for s in self.splitter.flush():
                self.on_sentence(s)
        error = None if self.job.cancelled() else self.job.exception()
        self.session_turns += 1
        self.context = {"messages": len(self.agent.messages)}
        events.emit("turn_end", duration_s=round(time.time() - t, 2), error=bool(error), steps=self.agent.steps,
                    session_id=self.session_id)
        events.emit("context", **self.context)
        if error:
            raise error
