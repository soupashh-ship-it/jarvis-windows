"""Run: .venv\\Scripts\\python test_brain.py   (no model, microphone or Windows needed)

Checks the agent loop against a fake OpenAI-compatible server (streamed text, a tool call split across chunks,
a gated call the user refuses), the safety rules, worker permission routing, hotkey parsing and app matching.
"""
import asyncio
import json

from aiohttp import web

import brain
import config
import events
import pctools
import winapi
import worker
from mouth import SentenceSplitter

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
    await runner.cleanup()

    assert spoken == ["Checking now.", "All done, sir."], spoken
    assert asked == ["Shall I delete x?"], asked                     # echo ran unasked, Remove-Item was put to the user
    tool_msgs = [m for m in seen[1]["messages"] if m["role"] == "tool"]
    assert tool_msgs == [{"role": "tool", "tool_call_id": "c1", "content": "hi"}], tool_msgs
    assert json.loads(seen[1]["messages"][2]["tool_calls"][0]["function"]["arguments"])["command"] == "echo hi"
    assert "said no" in seen[2]["messages"][-1]["content"]
    assert {t["function"]["name"] for t in seen[0]["tools"]} >= {"screenshot", "run_command", "start_worker"}


asyncio.run(main())

# the gate: allowed vs asked out loud
for cmd in ("Get-ChildItem ~\\Documents | Format-Table", "Get-Process | Sort-Object CPU", "curl.exe -s wttr.in/London",
            "Invoke-RestMethod https://example.com", "dir 2>&1", "Select-String -Path *.md -Pattern rm-old"):
    assert brain.policy("run_command", {"command": cmd}) == "allow", cmd
for cmd in ("Remove-Item foo.txt", "Get-ChildItem | rm", "echo x > notes.txt", "Stop-Process -Name notepad",
            "git push", "Invoke-RestMethod https://x.io -Method Post -Body $b", "curl.exe -X POST https://x.io",
            "Set-Content a.txt hi", "Start-Process cmd -Verb RunAs", "reg add HKCU\\x", "winget install foo"):
    assert brain.policy("run_command", {"command": cmd}) == "confirm", cmd
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
assert asked == ["Sir, the report worker would like to delete x. Shall I allow it?"] and w.state == "running"
assert [worker.outcome(r, f) for r, f in (("Saved to notes.md", False), ("NEED USER: 2x or 4x?", False), ("", True))] \
    == ["done", "waiting", "failed"]

# hotkeys, app matching, reasoning text never spoken
assert winapi.parse_hotkey("win+shift+j") == (8 | 4, ord("J")) and winapi.parse_hotkey("ctrl+alt+f12") == (3, 0x7B)
apps = [{"id": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "name": "Calculator"},
        {"id": "Chrome", "name": "Google Chrome"}, {"id": "Microsoft.Windows.Explorer", "name": "File Explorer"}]
assert pctools.find_app("calculator", apps)["name"] == "Calculator"
assert pctools.find_app("chrome", apps)["name"] == "Google Chrome"
assert pctools.find_app("files", apps)["name"] == "File Explorer"
s = SentenceSplitter()
assert s.feed("<think>Hmm. The user wants") + s.feed(" X.</think>Very good, sir. ") + s.flush() == ["Very good, sir."]
print("ok")
