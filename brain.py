"""The brain: any chat model behind an OpenAI-compatible endpoint, a small tool loop, and a spoken permission gate."""
import asyncio
import json
import logging
import os
import re
import string
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


async def chat(messages, tools=None, on_text=None, model=None):
    """One streamed chat completion. Returns the assistant message: its text plus any tool calls."""
    body = {"model": model or config.LLM_MODEL, "messages": messages, "stream": True}
    if tools:
        body["tools"] = tools
    headers = {"Authorization": f"Bearer {config.LLM_API_KEY}"} if config.LLM_API_KEY else {}
    said, calls = "", []
    timeout = aiohttp.ClientTimeout(sock_connect=15, sock_read=300)
    async with aiohttp.ClientSession(timeout=timeout) as s, \
            s.post(config.LLM_BASE_URL.rstrip("/") + "/chat/completions", json=body, headers=headers) as r:
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
    msg = {"role": "assistant", "content": said or (None if calls else "")}
    if calls:
        msg["tool_calls"] = calls
    return msg


def preview(content, n=300):
    """Short text version of a tool result for the dashboard."""
    return " ".join(c["text"] if c["type"] == "text" else "[image]" for c in content)[:n]


class Agent:
    """A conversation with the model and the loop that runs its tool calls. Jarvis is one; each worker is another."""
    MAX_MESSAGES = 200

    def __init__(self, system, permit, tools, on_text=None, model=None, sub=False):
        self.system, self.permit, self.on_text, self.model, self.sub = system, permit, on_text, model, sub
        self.tools = {f.spec["function"]["name"]: f for f in tools}
        self.messages = []
        self.steps = 0

    async def run(self, prompt):
        """One request: model, tools, model... until it answers without a tool. Returns its last words."""
        self.messages.append({"role": "user", "content": prompt})
        self.steps = 0
        for _ in range(config.MAX_STEPS):
            self.trim()
            msg = await chat([{"role": "system", "content": self.system}] + self.messages,
                             [f.spec for f in self.tools.values()], self.on_text, self.model)
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
                    content = await self.call(call)
                    self.messages.append({"role": "tool", "tool_call_id": call["id"], "content": " ".join(
                        c["text"] for c in content if c["type"] == "text") or "done"})
                    answered.add(call["id"])
                    images += [c for c in content if c["type"] == "image"]
            finally:                                # interrupted: every tool call still needs an answer
                self.messages += [{"role": "tool", "tool_call_id": c["id"], "content": "Not run: the user interrupted."}
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
        events.emit("tool_use", tool_id=call["id"], name=name, input=json.dumps(args)[:1500], sub=self.sub)
        error = True
        if not isinstance(args, dict):
            result = pctools.text(f"The arguments for {name} weren't valid JSON. Try again.")
        elif name not in self.tools:
            result = pctools.text(f"There is no tool called {name}.")
        elif not await self.permit(name, args):
            result = pctools.text("The user said no (or didn't answer). Don't retry it; say so briefly.")
        else:
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
    r"send-mailmessage|ssh|scp|rsync|chmod|chown|dd|truncate|crontab|systemctl)(?=[\s;|)}]|$)"
    r"|" + _CMD + r"git\s+(push|reset|clean|checkout\s+--)"
    r"|-verb\s+runas"
    r"|\b(invoke-webrequest|invoke-restmethod|iwr|irm|curl)\b.*\s-(method\s+['\"]?(post|put|patch|delete)|body|infile)\b"
    r"|\bcurl(\.exe)?\b.*(\s-X\s*['\"]?(POST|PUT|PATCH|DELETE)|\s(-d|--data\S*|-F|--form|-T|--upload-file)\s)"
    r"|(?<![-=2])>(?!&)(?!\s*\$null)", re.I | re.M)


def _norm(p):
    return os.path.normcase(os.path.realpath(p))


SENSITIVE_PATHS = [os.path.join(config.HOME, p) for p in (
    ".ssh", ".gitconfig", r"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
    r"Documents\WindowsPowerShell", r"Documents\PowerShell")] + [config.JARVIS_DIR]   # incl. its own safety rules


def policy(name, data):
    """'allow', or 'confirm' (ask out loud first)."""
    if name == "press_keys":
        return "confirm" if re.search(r"\b(enter|return)\b", data.get("keys", ""), re.I) else "allow"
    if name == "run_command":
        return "confirm" if RISKY_COMMAND.search(data.get("command", "")) else "allow"
    if name == "write_file":
        path = _norm(os.path.join(config.HOME, os.path.expanduser(data.get("path", ""))))
        roots = [_norm(r) for r in [config.HOME, config.RUNTIME_DIR] + config.WRITE_OK_DIRS]
        outside = not any(path.startswith(r + os.sep) for r in roots)
        sensitive = any(path == _norm(p) or path.startswith(_norm(p) + os.sep) for p in SENSITIVE_PATHS)
        return "confirm" if outside or sensitive else "allow"
    return "allow" if name in pctools.TOOLS else "confirm"


def describe(name, data):
    if name == "run_command":
        desc = data.get("description")
        return desc[0].lower() + desc[1:] if desc else f"run this command: {data.get('command', '')[:120]}"
    if name == "write_file":
        return f"write the file {os.path.basename(data.get('path', ''))}"
    hints = [str(v) for k, v in data.items() if isinstance(v, str) and 0 < len(v) < 80
             and k in ("name", "title", "query", "text", "keys", "target")][:2]
    return name.replace("_", " ") + (": " + ", ".join(hints) if hints else "")


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
    msg = await chat([{"role": "system", "content": NOTES_PROMPT.format(name=config.USER_NAME)},
                      {"role": "user", "content": "Current notes file:\n\n" + (read_notes() or NOTES_TEMPLATE.format(
                          name=config.USER_NAME)) + "\n\nThe session that just ended:\n\n" + talk[-60000:]}])
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
        self.agent = Agent(system, self.gate, list(pctools.TOOLS.values()), on_text=self._text)
        self.session_id, self.session_started, self.session_turns = uuid.uuid4().hex[:8], time.time(), 0
        events.emit("brain", status="connected", model=config.LLM_MODEL, provider=config.LLM_BASE_URL)
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
