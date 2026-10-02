"""Talk to a running Jarvis without your voice.

  python jarvisctl.py dashboard         open the dashboard in your browser
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
import re
import sys
import urllib.error
import urllib.request

DIR = os.path.dirname(os.path.abspath(__file__))
TOKEN_FILE = os.path.join(DIR, "logs", "ctl.token")


SECRET_NAME = r"\w*(?:key(?!s\b)|token|secret|password|passwd|pwd|auth)\w*"


def redact(text, known=()):
    """API keys, tokens and passwords out: whole settings whose name says so, "password": "..." style fields,
    passwords in URLs, the known secret values themselves wherever they turn up, and anything shaped like a key."""
    for value in sorted(known, key=len, reverse=True):
        text = text.replace(value, "<redacted>")
    text = re.sub(r"(?im)^(\s*\w*(KEY|TOKEN|SECRET|PASSWORD|PASSWD|AUTH)\w*\s*=\s*).+$", r"\1'<redacted>'", text)
    text = re.sub(rf"(?i)({SECRET_NAME}\\?[\"']?\s*[:=]\s*\\?[\"'])[^\"'\\\n]+", r"\1<redacted>", text)
    text = re.sub(r"(?i)\b([a-z][\w+.-]*://)[^/\s:@'\"]+:[^/\s@'\"]+@", r"\1<redacted>@", text)
    text = re.sub(r"\b(sk-[\w-]{8,}|sk-ant-[\w-]{8,}|sk-or-[\w-]{8,}|gsk_\w{8,}|AIza[\w-]{20,}|xai-\w{8,}|"
                  r"hf_\w{8,}|gh[pousr]_\w{20,}|ya29\.[\w.-]{20,})", "<redacted>", text)
    return re.sub(r"(?i)(bearer\s+|api[_-]?key[\"'=:\s]+)[\w.-]{8,}", r"\1<redacted>", text)


def desktop():
    import winapi
    d = winapi.known_folder(0x10, os.path.join(os.path.expanduser("~"), "Desktop"))   # even when OneDrive moved it
    return d if os.path.isdir(d) else os.path.expanduser("~")


def known_secrets():
    """The values of every setting named like a secret (API keys, passwords), and the dashboard key."""
    values = []
    try:
        import config
        values = [v for k, v in vars(config).items() if re.search(r"KEY|TOKEN|SECRET|PASSWORD|PASSWD|AUTH", k)
                  and not k.startswith("HOTKEY") and isinstance(v, str) and len(v) >= 6]
    except Exception:
        pass
    try:
        with open(TOKEN_FILE) as f:
            values.append(f.read().strip())
    except OSError:
        pass
    return [v for v in values if v]


def private_events(text):
    """events.jsonl without what Jarvis typed or wrote into files (passwords, messages): only that it did."""
    out = []
    for line in text.splitlines():
        try:
            ev = json.loads(line)
        except ValueError:
            out.append(line)
            continue
        if ev.get("kind") == "tool_use" and ev.get("name") in ("type_text", "write_file"):
            try:
                args = json.loads(ev.get("input") or "{}")
                args.update({k: "(left out of the report)" for k in ("text", "content") if k in args})
                ev["input"] = json.dumps(args)
            except ValueError:
                ev["input"] = "(left out of the report)"
        out.append(json.dumps(ev))
    return "\n".join(out)


def report():
    """jarvis-report-<date>.zip on the Desktop: logs, versions, packages, microphones and the config, secrets removed.
    Nothing is uploaded anywhere; you send the file yourself. Screenshots, notes.md, the dashboard token and anything
    Jarvis typed or wrote into a file stay out."""
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
    if "events.jsonl" in files:
        files["events.jsonl"] = private_events(files["events.jsonl"])
    known = known_secrets()
    out = os.path.join(desktop(), f"jarvis-report-{time.strftime('%Y-%m-%d-%H%M')}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in files.items():
            z.writestr(name, redact(text, known))
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
    except OSError:
        sys.exit("Jarvis hasn't been started yet (there is no logs\\ctl.token).")
    if cmd == "dashboard":
        import webbrowser
        webbrowser.open(f"http://127.0.0.1:8765/?token={token}")
        sys.exit()
    try:
        req = urllib.request.Request("http://127.0.0.1:8765/api/cmd", json.dumps({"cmd": cmd, "text": rest}).encode(),
                                     {"Content-Type": "application/json", "X-Jarvis-Token": token})
        with urllib.request.urlopen(req, timeout=10) as r:
            print(json.load(r)["reply"])
    except urllib.error.HTTPError as e:              # it is running, but said no
        sys.exit(f"Jarvis refused that ({e.code}): {e.read().decode('utf-8', 'replace')[:300]}")
    except OSError:
        sys.exit("Jarvis isn't running.")
