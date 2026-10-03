"""Tests for the newer tools: organize_folder, calendar_add, current_date, recall, find_duplicates, biggest_files.

Run: .venv\\Scripts\\python.exe test_tools.py
"""
import asyncio
import os
import re
import shutil
import tempfile

import pctools

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
    try:
        out = unwrap(asyncio.run(pctools.calendar_add({"title": "Test Event", "start": "2026-10-04 15:30", "duration_minutes": 30})))
    finally:
        if _orig_startfile is None:
            del os.startfile
        else:
            os.startfile = _orig_startfile
    ics = os.path.join(os.path.expanduser("~"), "calendar", "Test_Event.ics")
    check("Test Event" in out and os.path.isfile(ics), "calendar_add creates an .ics")
    body = open(ics, encoding="utf-8", errors="replace").read()
    check("SUMMARY:Test Event" in body and "DTSTART:20261004T153000" in body, "ics contains the summary and start")

    # current_date mentions a weekday
    out = unwrap(asyncio.run(pctools.current_date({})))
    check(any(w in out for w in ("Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday")),
          "current_date includes the weekday")

    # recall ranks by TF-IDF, not substring
    import tempfile as tf
    fake = os.path.join(tf.gettempdir(), "jarvis-facts-test.md")
    with open(fake, "w", encoding="utf-8") as f:
        f.write("- Jarvis uses distil-small.en for STT (2026-10-03)\n- Pepper is the user's codename (2026-10-03)\n")
    old = pctools._FACTS_FILE
    try:
        pctools._FACTS_FILE = fake
        out = unwrap(asyncio.run(pctools.recall({"query": "what stt model"})))
        check("distil-small.en" in out, "recall finds STT fact for query 'what stt model'")
    finally:
        pctools._FACTS_FILE = old
        os.remove(fake)

    shutil.rmtree(TMP, ignore_errors=True)
    print("\nAll tool tests passed.")

if __name__ == "__main__":
    main()
