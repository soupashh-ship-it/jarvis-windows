"""Talk to a running Jarvis without your voice.

  python jarvisctl.py listen            same as saying "hey jarvis"
  python jarvisctl.py stop              shut up / cancel what you're doing
  python jarvisctl.py say open spotify  type a command instead of speaking it
  python jarvisctl.py yes | no          answer a confirmation question
  python jarvisctl.py speak hello       just say something out loud (voice test)
  python jarvisctl.py status
  python jarvisctl.py report            zip the logs for a bug report (works even if Jarvis isn't running)
"""
import json
import os
import sys
import urllib.request

DIR = os.path.dirname(os.path.abspath(__file__))
TOKEN_FILE = os.path.join(DIR, "logs", "ctl.token")


def redact(text):
    """API keys, tokens and passwords out: whole settings whose name says so, and anything shaped like a key."""
    import re
    text = re.sub(r"(?im)^(\s*\w*(KEY|TOKEN|SECRET|PASSWORD|PASSWD|AUTH)\w*\s*=\s*).+$", r"\1'<redacted>'", text)
    text = re.sub(r"\b(sk-[\w-]{8,}|sk-ant-[\w-]{8,}|sk-or-[\w-]{8,}|gsk_\w{8,}|AIza[\w-]{20,}|xai-\w{8,}|"
                  r"hf_\w{8,}|gh[pousr]_\w{20,}|ya29\.[\w.-]{20,})", "<redacted>", text)
    return re.sub(r"(?i)(bearer\s+|api[_-]?key[\"'=:\s]+)[\w.-]{8,}", r"\1<redacted>", text)


def desktop():
    if os.name == "nt":                          # the real Desktop, even when OneDrive has moved it
        import ctypes
        buf = ctypes.create_unicode_buffer(260)
        if ctypes.windll.shell32.SHGetFolderPathW(None, 0x10, None, 0, buf) == 0 and os.path.isdir(buf.value):
            return buf.value
    d = os.path.join(os.path.expanduser("~"), "Desktop")
    return d if os.path.isdir(d) else os.path.expanduser("~")


def report():
    """jarvis-report-<date>.zip on the Desktop: logs, versions, packages, microphones and the config, secrets removed.
    Nothing is uploaded anywhere; you send the file yourself. Screenshots, notes.md and the dashboard token stay out."""
    import platform
    import subprocess
    import time
    import zipfile

    def run(*cmd):
        try:
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=60, cwd=DIR,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            return r.stdout + r.stderr
        except Exception as e:
            return f"{cmd[0]} failed: {e}"

    def tail(path, n=2_000_000):
        with open(path, "rb") as f:
            f.seek(max(0, os.path.getsize(path) - n))
            return f.read().decode("utf-8", "replace")

    logs = os.path.join(DIR, "logs")
    files = {name: tail(os.path.join(logs, name)) for name in
             ["jarvis.log"] + [f"jarvis.log.{i}" for i in (1, 2, 3)] + ["console.log", "overlay.log", "events.jsonl"]
             if os.path.exists(os.path.join(logs, name))}
    lines = [ln for name in sorted(files, reverse=True) if name.startswith("jarvis.log")
             for ln in files[name].splitlines()
             if any(k in ln for k in ("wake score", "mic open", "microphones:", "pure silence", "hotkey"))]
    files["wake-and-mic.txt"] = "\n".join(lines) or "(no wake or mic lines logged yet)"
    files["system.txt"] = (f"Python {sys.version}\n{platform.platform()}\nWindows {platform.win32_ver()}\n"
                           f"Jarvis folder: {DIR}\nGit: {run('git', 'log', '-1', '--oneline').strip()}\n\n"
                           f"Microphones (python -m sounddevice):\n{run(sys.executable, '-m', 'sounddevice')}")
    files["pip-freeze.txt"] = run(sys.executable, "-m", "pip", "freeze")
    for name in ("config_local.py", "config.py"):
        if os.path.exists(os.path.join(DIR, name)):
            with open(os.path.join(DIR, name), encoding="utf-8", errors="replace") as f:
                files[name] = f.read()
    out = os.path.join(desktop(), f"jarvis-report-{time.strftime('%Y-%m-%d-%H%M')}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in files.items():
            z.writestr(name, redact(text))
    print(f"Saved {out}\nDrag it into Discord (or attach it to a GitHub issue). Nothing was uploaded.")
    if os.name == "nt":
        subprocess.Popen(["explorer", "/select,", out])
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
        sys.exit(__doc__.strip())
    cmd, rest = sys.argv[1], " ".join(sys.argv[2:])
    if cmd == "report":
        report()
        sys.exit()
    if cmd in ("say", "speak") and not rest:
        sys.exit(f"jarvisctl {cmd} needs some text")
    try:
        with open(TOKEN_FILE) as f:
            token = f.read().strip()
        req = urllib.request.Request("http://127.0.0.1:8765/api/cmd", json.dumps({"cmd": cmd, "text": rest}).encode(),
                                     {"Content-Type": "application/json", "X-Jarvis-Token": token})
        with urllib.request.urlopen(req, timeout=10) as r:
            print(json.load(r)["reply"])
    except OSError:
        sys.exit("Jarvis isn't running.")
