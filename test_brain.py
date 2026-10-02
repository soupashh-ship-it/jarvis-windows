"""Run: .venv\\Scripts\\python test_brain.py   (no model, microphone or Windows needed)

Checks the agent loop against a fake OpenAI-compatible server (streamed text, a tool call split across chunks,
a gated call the user refuses, and a Gemini-style stream: no index, repeated ids, parallel calls in one chunk, a
thought signature that must come back, an empty reply), the step limit per tool call, one agent at a time on the
desktop, per-agent screenshots, the safety rules and their spoken questions, yes/no parsing, worker permission
routing, hotkey parsing, app matching and the report's redaction.
"""
import asyncio
import json
import re

from aiohttp import web

import brain
import config
import events
import pctools
import winapi
import worker
from mouth import SentenceSplitter

real_emit = events.emit
events.emit = lambda *a, **k: None                      # keep the real event log clean


def sse(*chunks):
    return "".join(f"data: {json.dumps({'choices': [{'delta': c}]})}\n\n" for c in chunks) + "data: [DONE]\n\n"


def call(i, cid=None, name=None, args=""):
    return {"tool_calls": [{"index": i, **({"id": cid} if cid else {}), "function": {
        **({"name": name} if name else {}), "arguments": args}}]}


REPLIES = [   # what the fake model says, request by request
    sse({"content": "Checking"}, {"content": " now. "}, call(0, "c1", "run_command", '{"command": "echo hi", '),
        call(0, args='"description": "Say hi", "timeout": 5}')),
    sse(call(0, "c2", "run_command", '{"command": "Remove-Item C:\\\\x", "description": "Delete x", "timeout": 5}')),
    sse({"content": "All done, sir."}),
]
GEMINI = [    # recorded shape of Gemini's OpenAI-compatible stream: whole calls in one chunk, no index, id "0" twice
    sse({"role": "assistant", "tool_calls": [
        {"id": "0", "type": "function", "extra_content": {"google": {"thought_signature": "sigA"}},
         "function": {"name": "run_command", "arguments": '{"command": "echo a", "description": "a", "timeout": 5}'}},
        {"id": "0", "type": "function",
         "function": {"name": "run_command", "arguments": '{"command": "echo b", "description": "b", "timeout": 5}'}}]}),
    sse({"content": ""}),
    sse({"content": "Both done."}),
]
seen = []


async def completions(request):
    seen.append(await request.json())
    return web.Response(text=REPLIES[len(seen) - 1], content_type="text/event-stream")


async def main():
    app = web.Application()
    app.router.add_post("/v1/chat/completions", completions)
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, "127.0.0.1", 0)
    await site.start()
    config.LLM_BASE_URL = f"http://127.0.0.1:{site._server.sockets[0].getsockname()[1]}/v1"

    asked, spoken = [], []

    async def say_no(question):
        asked.append(question)
        return False
    b = brain.Brain(confirm=say_no, on_sentence=spoken.append)
    b.agent = brain.Agent("test persona", b.gate, list(pctools.TOOLS.values()), on_text=b._text)
    b.session_id = "test"
    await b.ask("[Mon 28 Sep, 07:42] say hi")
    first = list(seen)

    global REPLIES
    REPLIES = GEMINI
    seen.clear()
    g = brain.Agent("test persona", b.gate, list(pctools.TOOLS.values()))
    assert await g.run("run a and b") == ""                         # Gemini sometimes answers with nothing
    assert await g.run("and?") == "Both done."
    await runner.cleanup()
    calls = seen[1]["messages"][2]["tool_calls"]
    assert [json.loads(c["function"]["arguments"])["command"] for c in calls] == ["echo a", "echo b"], calls
    assert calls[0]["extra_content"] == {"google": {"thought_signature": "sigA"}} and "extra_content" not in calls[1]
    assert len({c["id"] for c in calls}) == 2, calls                 # each call gets its own id...
    assert [m["tool_call_id"] for m in seen[1]["messages"] if m["role"] == "tool"] == [c["id"] for c in calls]
    assert [m["content"] for m in seen[1]["messages"] if m["role"] == "tool"] == ["a", "b"]
    assert not [m for m in seen[2]["messages"] if m["role"] == "assistant" and not m.get("content")
                and not m.get("tool_calls")]                         # ...and the empty reply is never sent back
    seen[:] = first

    assert spoken == ["Checking now.", "All done, sir."], spoken
    assert asked == ["Shall I run Remove-Item C:\\x (delete x)?"], asked   # echo ran unasked, Remove-Item was asked
    tool_msgs = [m for m in seen[1]["messages"] if m["role"] == "tool"]
    assert tool_msgs == [{"role": "tool", "tool_call_id": "c1", "content": "hi"}], tool_msgs
    assert json.loads(seen[1]["messages"][2]["tool_calls"][0]["function"]["arguments"])["command"] == "echo hi"
    assert "said no" in seen[2]["messages"][-1]["content"]
    assert {t["function"]["name"] for t in seen[0]["tools"]} >= {"screenshot", "run_command", "start_worker"}


asyncio.run(main())

# MAX_STEPS counts tool calls, not model replies; the desktop has one driver at a time; screenshots are per agent
ran = []


async def click(args):
    ran.append(args)
    return pctools.text("ok")
click.spec = {"type": "function", "function": {"name": "click_at", "parameters": {}}}


async def allow(name, data):
    return True
five = [{"id": f"c{i}", "type": "function", "function": {"name": "click_at", "arguments": "{}"}} for i in range(5)]


async def fake_chat(messages, tools=None, on_text=None, model=None):
    return {"role": "assistant", "content": None, "tool_calls": five}
real_chat, brain.chat, config.MAX_STEPS = brain.chat, fake_chat, 3
a = brain.Agent("t", allow, [click])
assert asyncio.run(a.run("go")) == "I've reached my step limit for this request." and len(ran) == 3
assert [m["content"] for m in a.messages if m["role"] == "tool"][3:] == ["Not run: the step limit was reached."] * 2
assert brain.DESK["owner"] is None                                # let go when its request ended
brain.DESK["owner"] = a                                           # Jarvis is mid-request on the desktop...
b = brain.Agent("t", allow, [click], sub=True, name="report")
assert asyncio.run(b.call(five[0]))[0]["text"].startswith("Not run: Jarvis is using the mouse") and len(ran) == 3
brain.DESK["owner"], brain.chat, config.MAX_STEPS = None, real_chat, 30
a.shot.update(x=100, scale=2.0)


async def where(agent):
    pctools.SHOT.set(agent.shot)
    await asyncio.sleep(0)
    return pctools.to_desktop(10, 10)


async def both():
    return await asyncio.gather(where(a), where(b))
assert asyncio.run(both()) == [(120.0, 20.0), (10.0, 10.0)]

# spoken yes/no: only a plain yes approves
for ans in ("yes", "Yes.", "Yeah, go ahead.", "Okay, do it, sir.", "yes please", "Please do.", "Sure."):
    assert brain.approved(ans), ans
for ans in ("", "no", "I'm not sure.", "Not okay.", "I cannot confirm that.", "Yes, but not that one.", "sure, after lunch",
            "Don't.", "Sir?", "Wait, yes", "no, go ahead"):
    assert not brain.approved(ans), ans

# the gate: allowed vs asked out loud
for cmd in ("Get-ChildItem ~\\Documents | Format-Table", "Get-Process | Sort-Object CPU", "curl.exe -s wttr.in/London",
            "Invoke-RestMethod https://example.com", "dir 2>&1", "Select-String -Path *.md -Pattern rm-old",
            "curl.exe -fsSL https://example.com | Select-Object -First 5", "Start-Sleep 2", "Get-Content a.txt -Encoding UTF8"):
    assert brain.policy("run_command", {"command": cmd}) == "allow", cmd
for cmd in ("Remove-Item foo.txt", "Get-ChildItem | rm", "echo x > notes.txt", "Stop-Process -Name notepad",
            "git push", "Invoke-RestMethod https://x.io -Method Post -Body $b", "curl.exe -X POST https://x.io",
            "Set-Content a.txt hi", "Start-Process cmd -Verb RunAs", "reg add HKCU\\x", "winget install foo",
            "Invoke-WebRequest https://x.io/a.exe -OutFile a.exe", "curl.exe -sLo a.exe https://x.io", "curl -dfoo https://x.io",
            "curl.exe --json '{}' https://x.io", "New-Item C:\\Windows\\x.txt -Value hi", "[IO.File]::WriteAllText('x', 'y')",
            "cmd /c del x", "powershell -enc AAAA", "Start-Process setup.exe", "(New-Object Net.WebClient).DownloadFile($u, $f)"):
    assert brain.policy("run_command", {"command": cmd}) == "confirm", cmd
assert brain.policy("open_path", {"target": "C:\\x\\setup.exe"}) == "confirm"
assert brain.policy("open_path", {"target": "https://example.com"}) == brain.policy("open_path", {"target": "a.txt"}) == "allow"
# the question comes from the real arguments: a long command shows its risky part, a file its full path
q = brain.describe("run_command", {"command": "Get-ChildItem " + "x" * 300 + "; Remove-Item C:\\y", "description": "List"})
assert "Remove-Item C:\\y" in q and q.endswith("(list)"), q
assert brain.describe("write_file", {"path": "new-dir-for-test/a.md"}).startswith("create the file ")
assert brain.policy("write_file", {"path": "notes/todo.md"}) == "allow"
assert brain.policy("write_file", {"path": "/etc/passwd"}) == "confirm"
assert brain.policy("write_file", {"path": config.JARVIS_DIR + "/brain.py"}) == "confirm"   # its own safety rules
assert brain.policy("press_keys", {"keys": "ctrl+enter"}) == "confirm"
assert brain.policy("press_keys", {"keys": "ctrl+t"}) == "allow"
assert brain.policy("something_else", {}) == "confirm"

# workers ask through the same gate, and report how they finished
asked = []


async def say_yes(q):
    asked.append(q)
    return True
worker.confirm = say_yes
w = worker.Worker.__new__(worker.Worker)
w.name, w.state, w.last = "report", "running", ""
assert asyncio.run(w.permit("run_command", {"command": "Get-Date", "description": "x"})) and not asked
assert asyncio.run(w.permit("run_command", {"command": "del x", "description": "Delete x"}))
assert asked == ["Sir, the report worker would like to run del x (delete x). Shall I allow it?"] and w.state == "running"
assert [worker.outcome(r, f) for r, f in (("Saved to notes.md", False), ("NEED USER: 2x or 4x?", False), ("", True))] \
    == ["done", "waiting", "failed"]

# Gemini rejects tools whose parameters have no properties
assert all(t.spec["function"]["parameters"]["properties"] for t in pctools.TOOLS.values())

# the bug report keeps settings but drops secrets
import jarvisctl  # noqa: E402
r = jarvisctl.redact('LLM_API_KEY = "AIzaSyD-abcdefghijklmnopqrstu"\nLLM_MODEL = "gemini-3-flash"\n'
                     'x Authorization: Bearer abcdef123456789 y\nkey sk-or-v1-abcdef0123456789 and gsk_abcdef0123456789')
assert "abcdef" not in r and "AIza" not in r and 'LLM_MODEL = "gemini-3-flash"' in r, r
r = jarvisctl.redact('{"password": "hunter22", "keys": "ctrl+t"} \\"api_token\\": \\"tok123\\" https://bob:s3cret@x.io/y '
                     'and myOwnSecretValue', known=["myOwnSecretValue"])
assert not {"hunter22", "tok123", "s3cret", "myOwnSecretValue"} & set(re.findall(r"\w+", r)) and "ctrl+t" in r, r
assert "pa55word" not in jarvisctl.private_events(json.dumps(
    {"kind": "tool_use", "name": "type_text", "input": json.dumps({"text": "pa55word"})}))

# a log file that can't be written never stops Jarvis (or the Stop button)
events.EVENTS_FILE = "/nonexistent-dir/events.jsonl"
real_emit("test")

# hotkeys, app matching, reasoning text never spoken
assert winapi.parse_hotkey("win+shift+j") == (8 | 4, ord("J")) and winapi.parse_hotkey("ctrl+alt+f12") == (3, 0x7B)
assert winapi.parse_hotkey("ctrl+control+j") == (2, ord("J"))
apps = [{"id": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "name": "Calculator"},
        {"id": "Chrome", "name": "Google Chrome"}, {"id": "Microsoft.Windows.Explorer", "name": "File Explorer"}]
assert pctools.find_app("calculator", apps)["name"] == "Calculator"
assert pctools.find_app("chrome", apps)["name"] == "Google Chrome"
assert pctools.find_app("files", apps)["name"] == "File Explorer"
s = SentenceSplitter()
assert s.feed("<think>Hmm. The user wants") + s.feed(" X.</think>Very good, sir. ") + s.flush() == ["Very good, sir."]
s.feed("Here:\n```python\nprint(1)")                              # a reply that ends inside a code block...
s.flush()
assert s.feed("Done, sir. ") == ["Done, sir."]                    # ...doesn't mute the next one
print("ok")
