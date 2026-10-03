"""Tests for the newer tools: organize_folder, calendar_add, current_date, recall, find_duplicates, biggest_files.

Run: .venv\\Scripts\\python.exe test_tools.py
"""
import asyncio
import os
import re
import shutil
import subprocess
import tempfile

import pctools
import config

TMP = tempfile.mkdtemp(prefix="jarvis-tools-")

def check(cond, msg):
    assert cond, msg
    print(f"ok: {msg}")

def unwrap(result):
    return result["content"][0]["text"] if isinstance(result, dict) else str(result)

def main():
    os.chdir(os.path.dirname(os.path.abspath(__file__)))

    # organize_folder: nested layout into type folders, second run no-op
    root = os.path.join(TMP, "tidy")
    os.makedirs(os.path.join(root, "sub"), exist_ok=True)
    for f in ("a.png", "b.pdf", os.path.join("sub", "c.mp3"), os.path.join("sub", "d.zip")):
        open(os.path.join(root, f), "wb").close()
    out = unwrap(asyncio.run(pctools.organize_folder({"path": root, "dry_run": True})))
    check("Would move" in out, "organize_folder previews before touching anything")
    out = unwrap(asyncio.run(pctools.organize_folder({"path": root, "dry_run": False})))
    check(os.path.isfile(os.path.join(root, "Images", "a.png")), "image landed in Images/")
    check(os.path.isfile(os.path.join(root, "Documents", "b.pdf")), "pdf landed in Documents/")
    check(os.path.isfile(os.path.join(root, "Audio", "c.mp3")), "nested mp3 landed in Audio/")
    out = unwrap(asyncio.run(pctools.organize_folder({"path": root, "dry_run": True})))
    check("already tidy" in out, "second organize_folder run is a no-op")

    # find_duplicates / biggest_files on the tidy tree
    async def du():
        dup = unwrap(await pctools.find_duplicates({"folder": root}))
        big = unwrap(await pctools.biggest_files({"folder": root, "count": 3}))
        return dup, big
    dup, big = asyncio.run(du())
    check(dup, "find_duplicates answers without error")

    # calendar_add writes a valid .ics
    _orig_startfile = getattr(os, "startfile", None)
    os.startfile = lambda p: None      # don't pop the Windows calendar during tests
    import sys
    sys.modules["outlook"] = None      # `import outlook` then fails, so tests never launch real Outlook
    try:
        out = unwrap(asyncio.run(pctools.calendar_add({"title": "Test Event", "start": "2026-10-04 15:30", "duration_minutes": 30})))
    finally:
        sys.modules.pop("outlook", None)
        if _orig_startfile is None:
            del os.startfile
        else:
            os.startfile = _orig_startfile
    ics = os.path.join(os.path.expanduser("~"), "calendar", "Test_Event.ics")
    check("Test Event" in out and os.path.isfile(ics), "calendar_add creates an .ics")
    body = open(ics, encoding="utf-8", errors="replace").read()
    check("SUMMARY:Test Event" in body and "DTSTART:20261004T153000" in body, "ics contains the summary and start")

    # the times people actually say
    import datetime
    check(pctools._resolve_start("2026-10-04 15:30") == datetime.datetime(2026, 10, 4, 15, 30), "ISO-ish start parses")
    check(pctools._resolve_start("4 October 2026 09:00") == datetime.datetime(2026, 10, 4, 9, 0), "spelled-out date parses")
    t = pctools._resolve_start("tomorrow 15:30")
    check(t is not None and t.hour == 15 and t.minute == 30 and (t.date() - datetime.date.today()).days == 1,
          "'tomorrow 15:30' resolves to tomorrow at 15:30")
    t = pctools._resolve_start("friday 18:00")
    check(t is not None and t.weekday() == 4 and t.hour == 18, "'friday 18:00' lands on a Friday")
    check(pctools._resolve_start("sometime soon") is None, "nonsense times are refused, not guessed")

    # the Outlook command we build has to be valid PowerShell, quotes and all
    ps = ("$ol = New-Object -ComObject Outlook.Application; $ns = $ol.GetNamespace('MAPI'); "
          "$cal = $ns.GetDefaultFolder(9); $item = $cal.Items.Add(0); $item.Subject = 'Bob''s review'; "
          "$item.Start = Get-Date '2026-10-04 15:30'; $item.Save(); Write-Output 'ok'")
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command",
                        f"$null = [scriptblock]::Create(@'\n{ps}\n'@); Write-Output 'syntax-ok'"],
                       text=True, capture_output=True, timeout=60)
    check("syntax-ok" in r.stdout, "the Outlook PowerShell we generate parses cleanly")

    # current_date mentions a weekday
    out = unwrap(asyncio.run(pctools.current_date({})))
    check(any(w in out for w in ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")),
          "current_date includes the weekday")

    # recall ranks by TF-IDF across facts AND the notes it wrote in earlier sessions
    import tempfile as tf
    fake = os.path.join(tf.gettempdir(), "jarvis-facts-test.md")
    fake_notes = os.path.join(tf.gettempdir(), "jarvis-notes-test.md")
    with open(fake, "w", encoding="utf-8") as f:
        f.write("- Jarvis uses distil-small.en for STT (2026-10-03)\n- Pepper is the user's codename (2026-10-03)\n")
    with open(fake_notes, "w", encoding="utf-8") as f:
        f.write("# Session notes\nJarvis migrated the widget to a flat slab HUD.\nThe dashboard port is 8765.\n")
    old, old_notes = pctools._FACTS_FILE, config.NOTES_FILE
    try:
        pctools._FACTS_FILE = fake
        config.NOTES_FILE = fake_notes
        out = unwrap(asyncio.run(pctools.recall({"query": "what stt model"})))
        check("distil-small.en" in out and out.startswith("[fact]"), "recall finds the STT fact, labelled as a fact")
        out = unwrap(asyncio.run(pctools.recall({"query": "dashboard port"})))
        check("[notes]" in out and "8765" in out, "recall also searches earlier-session notes")
        out = unwrap(asyncio.run(pctools.forget({"query": "codename"})))
        check("Forgot 1" in out, "forget removes a stale fact")
        out = unwrap(asyncio.run(pctools.recall({"query": "codename"})))
        check("codename" not in out, "the forgotten fact is really gone")
    finally:
        pctools._FACTS_FILE, config.NOTES_FILE = old, old_notes
        os.remove(fake)
        os.remove(fake_notes)

    # self-written tools: write one, hot-load it into a live agent, then delete it again
    plugin = os.path.join(os.path.dirname(os.path.abspath(__file__)), "custom_tools.py")
    have_plugin = os.path.exists(plugin)
    original = open(plugin, encoding="utf-8").read() if have_plugin else ""
    snippet = ('\nfrom pctools import tool, text\n\n'
               '@tool("hot_probe", "Test tool written at runtime.", {})\n'
               'async def hot_probe(args):\n    return text("hot ok: " + args.get("who", "?"))\n')
    try:
        with open(plugin, "a", encoding="utf-8") as f:
            f.write(snippet)
        out = unwrap(asyncio.run(pctools.reload_tools({})))
        check("hot_probe" in out, "reload_tools picks up a tool written into custom_tools.py")
        check("hot_probe" in pctools.TOOLS, "the new tool is callable straight away")
        out = unwrap(asyncio.run(pctools.TOOLS["hot_probe"]({"who": "tester"})))
        check("hot ok: tester" in out, "the hot-loaded tool runs")
        out = unwrap(asyncio.run(pctools.reload_tools({})))     # deleting the tool takes it away again
        with open(plugin, "w", encoding="utf-8") as f:
            f.write(original)
        out = unwrap(asyncio.run(pctools.reload_tools({})))
        check("hot_probe" not in pctools.TOOLS, "a tool deleted from the file stops being offered")
    finally:
        if have_plugin:
            with open(plugin, "w", encoding="utf-8") as f:
                f.write(original)
        pctools.load_plugins(reload=True)

    shutil.rmtree(TMP, ignore_errors=True)
    print("\nAll tool tests passed.")

if __name__ == "__main__":
    main()
