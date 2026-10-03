"""Jarvis's hands on the desktop: apps, media, volume, screenshots, mouse, keyboard, PowerShell and files.

Every tool is an async function registered with @tool; brain.py hands their specs to the model.
"""
import asyncio
import base64
import contextvars
import io
import json
import logging
import os
import re
import subprocess
import sys
import threading
import time

import mss
import psutil
from PIL import Image

import config
import events
import winapi

TOOLS = {}                 # name -> async function, with .spec for the model
TYPES = {str: "string", int: "integer", bool: "boolean"}
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)          # Jarvis runs windowless; so must its commands
PS_UTF8 = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
DESKTOP = {"open_app", "focus_window", "click_at", "move_mouse", "scroll", "type_text", "press_keys", "ui_click"}  # one agent at a time
MUTATES = {"open_app", "focus_window", "click_at", "move_mouse", "scroll", "type_text", "press_keys", "ui_click",
           "run_command", "write_file", "organize_folder", "calendar_add", "set_timer", "cancel_timer",
           "volume", "app_volume", "media", "open_path", "forget"}                     # tools that change the world
NOTIFY = None              # (message) -> None, speak a message later; set by jarvis.py


def tool(name, description, params):
    """Register an async tool; params maps argument names to Python types (all required), or is a full JSON schema."""
    if params.get("type") != "object":
        params = {"type": "object", "properties": {k: {"type": TYPES[t]} for k, t in params.items()}, "required": list(params)}
    if not params["properties"]:     # Gemini rejects an object with no properties, so no-argument tools get an optional one
        params = {"type": "object", "properties": {"note": {"type": "string", "description": "optional, ignored"}}}

    def register(fn):
        fn.spec = {"type": "function", "function": {"name": name, "description": description, "parameters": params}}
        TOOLS[name] = fn
        return fn
    return register


def text(s):
    return {"content": [{"type": "text", "text": s}]}


def powershell(command):
    return ["powershell", "-NoProfile", "-NonInteractive", "-Command", PS_UTF8 + command]


# ---------- apps ----------

_apps = {"at": 0, "list": []}


def start_apps():
    """Everything in the Start menu, Store apps included, from PowerShell's Get-StartApps."""
    # ponytail: cached 5 minutes, so an app installed a moment ago may not be found yet
    if time.time() - _apps["at"] > 300:
        out = subprocess.run(powershell("Get-StartApps | ConvertTo-Json -Compress"), capture_output=True,
                             encoding="utf-8", errors="replace", timeout=30, creationflags=NO_WINDOW).stdout
        found = json.loads(out or "[]")
        _apps.update(at=time.time(), list=[{"id": a["AppID"], "name": a["Name"]}
                                           for a in ([found] if isinstance(found, dict) else found)])
    return _apps["list"]


ALIASES = {"files": "file explorer", "file manager": "file explorer", "explorer": "file explorer",
           "browser": "microsoft edge", "edge": "microsoft edge", "chrome": "google chrome", "text editor": "notepad"}


def find_app(query, apps=None):
    q = query.lower().strip()
    q = ALIASES.get(q, q)
    best, best_score = None, 0
    for a in start_apps() if apps is None else apps:
        name = a["name"].lower()
        score = 100 if name == q else 80 if name.startswith(q) else 60 if q in name else 40 if q in a["id"].lower() else 0
        if score > best_score or (score == best_score and best and len(name) < len(best["name"])):
            best, best_score = a, score
    return best


def window_names(app):
    """What its window is likely called: the app name, and the program name for classic desktop apps."""
    exe = os.path.basename(app["id"])
    return [app["name"]] + ([exe[:-4]] if exe.lower().endswith(".exe") else [])


@tool("open_app", "Open an application by name and bring it to the front, e.g. 'spotify', 'discord', "
      "'obs', 'steam', 'files'. If it is already running, its window is focused instead.", {"name": str})
async def open_app(args):
    await asyncio.to_thread(start_apps)      # PowerShell takes seconds; off the event loop so Jarvis keeps listening
    app = find_app(args["name"])
    for q in [args["name"]] + (window_names(app) if app else []):
        w = winapi.focus(q)
        if w:
            return text(f"{w['title']} was already open; brought it to the front." if w["active"] else
                        f"{w['title']} is already open, but Windows wouldn't bring it to the front.")
    if not app:
        return text(f"No installed app matches '{args['name']}'. Try list_apps.")
    subprocess.Popen(["explorer.exe", "shell:AppsFolder\\" + app["id"]])
    for _ in range(30):
        await asyncio.sleep(0.5)
        for q in window_names(app):
            w = winapi.focus(q)
            if w:
                return text(f"Launched {app['name']}; its window '{w['title']}' is in front.")
    return text(f"Launched {app['name']} but no window showed up within 15s; it may still be loading.")


@tool("list_apps", "List installed apps whose name contains the query (empty query = all).", {"query": str})
async def list_apps(args):
    q = args.get("query", "").lower()
    apps = await asyncio.to_thread(start_apps)
    return text(", ".join(sorted(a["name"] for a in apps if q in a["name"].lower())) or "none")


@tool("open_path", "Open a file, folder or URL with its default app.", {"target": str})
async def open_path(args):
    target = args.get("target") or args.get("path") or args.get("url") or args.get("file") or ""
    if not str(target).strip():
        return text("Tell me what to open (a file, a folder, or a URL).")
    try:
        os.startfile(os.path.expanduser(str(target)))
    except OSError as e:
        return text(f"Couldn't open {target}: {e}")
    return text(f"Opened {target}.")


# ---------- media and volume ----------

MEDIA_KEYS = {"play_pause": "playpause", "play": "playpause", "pause": "playpause", "next": "nexttrack",
              "previous": "prevtrack", "stop": "mediastop"}


@tool("media", "Control whatever is playing music/video (Spotify, YouTube in a browser, etc) with the media keys. "
      "action: play_pause, play, pause, next, previous, stop.", {"action": str})
async def media(args):
    action = args["action"].lower()
    if action not in MEDIA_KEYS:
        return text("Unknown action. Windows doesn't report what's playing here; players usually show the track "
                    "in their window title, see list_windows." if action == "status" else f"Unknown action {action}.")
    winapi.press([MEDIA_KEYS[action]])
    return text(f"Sent the {action.replace('_', ' ')} media key (play and pause both toggle).")


@tool("volume", "System output volume. action: get, set (level 0-100), up, down, mute, unmute. "
      "Pass level 0 when not setting.", {"action": str, "level": int})
async def volume(args):
    from pycaw.pycaw import AudioUtilities          # Windows only
    ev, action = AudioUtilities.GetSpeakers().EndpointVolume, args["action"].lower()
    if action == "set":
        ev.SetMasterVolumeLevelScalar(max(0, min(100, args.get("level", 50))) / 100, None)
    elif action in ("up", "down"):
        level = ev.GetMasterVolumeLevelScalar() + (0.1 if action == "up" else -0.1)
        ev.SetMasterVolumeLevelScalar(max(0.0, min(1.0, level)), None)
    elif action in ("mute", "unmute"):
        ev.SetMute(action == "mute", None)
    return text(f"Volume {round(ev.GetMasterVolumeLevelScalar() * 100)}%{', muted' if ev.GetMute() else ''}.")


@tool("app_volume", "Set the output volume of one app, e.g. 'discord', 'spotify' (0-100). Use the task manager's "
      "name if unsure. action: set, get. level only for set.", {"app": str, "action": str, "level": int})
async def app_volume(args):
    from pycaw.pycaw import AudioUtilities
    app = args["app"].lower().rstrip(".exe")
    action = args.get("action", "get").lower()
    for s in AudioUtilities.GetAllSessions():
        name = (s.Process.name() if s.Process else "") or ""
        if app in name.lower():
            vol = s.SimpleAudioVolume
            if action == "set":
                vol.SetMasterVolume(max(0, min(100, args.get("level", 50))) / 100, None)
            return text(f"{name} volume {round(vol.GetMasterVolume() * 100)}%.")
    return text(f"No audio session matches '{args['app']}'. Is it playing sound right now?")


# ---------- screen text (OCR) ----------

@tool("read_screen_text", "Read the text that's on the screen right now (OCR): an error message, a code, a label. "
      "target: 'window' (active window), 'left' or 'right' (one monitor) or 'both'. Returns the detected lines.",
      {"target": str})
async def read_screen_text(args):
    import winocr                      # pip install winocr: Windows' built-in OCR engine

    with mss.mss() as sct:
        monitors = sorted(sct.monitors[1:], key=lambda m: m["left"])
        region = sct.monitors[0]
        target = args.get("target", "both")
        if target in ("left", "right"):
            region = monitors[0] if target == "left" else monitors[-1]
        elif target == "window":
            w = winapi.active_window()
            if w and w["w"] > 0 and w["h"] > 0 and not w["minimized"]:
                region = {"left": w["x"], "top": w["y"], "width": w["w"], "height": w["h"]}
        shot = sct.grab(region)
    img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")

    def run():
        out = winocr.recognize_pil_sync(img)
        return [l["text"] for l in out.get("lines", []) if l.get("text", "").strip()]

    lines = await asyncio.to_thread(run)
    if not lines:
        return text("No readable text found on that part of the screen.")
    joined = "\n".join(lines)
    return text(joined[:4000] + ("\n(truncated)" if len(joined) > 4000 else ""))


# ---------- file tidying ----------

_KINDS = {"Images": {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico"},
          "Videos": {".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v"},
          "Audio": {".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac"},
          "Documents": {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf", ".odt"},
          "Archives": {".zip", ".rar", ".7z", ".tar", ".gz", ".bz2"},
          "Code": {".py", ".js", ".ts", ".html", ".css", ".json", ".yaml", ".yml", ".xml", ".cs", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".sh", ".ps1", ".sql"},
          "Apps": {".exe", ".msi", ".lnk", ".bat", ".cmd"}}


def _category(name):
    ext = os.path.splitext(name)[1].lower()
    return next((k for k, exts in _KINDS.items() if ext in exts), None)


@tool("organize_folder", "Tidy a folder: move its files into subfolders by type (Images, Documents, Archives, "
      "Code, Apps...). path: which folder ('Downloads', 'Desktop', or a full folder). dry_run: true to only preview "
      "what would move, false to actually move.", {"path": str, "dry_run": bool})
async def organize_folder(args):
    target = args.get("path", "Downloads")
    target = os.path.expanduser(os.path.join(config.HOME, target) if target in ("Downloads", "Desktop", "Documents") else target)
    if not os.path.isdir(target):
        return text(f"No folder at {target}.")
    plan, seen = [], set()
    for dirpath, dirnames, filenames in os.walk(target):
        for f in sorted(filenames):
            if f.startswith("."):
                continue
            src = os.path.join(dirpath, f)
            if not os.path.isfile(src) or src in seen:
                continue
            kind = _category(f) or "Other"
            dst_dir = os.path.join(target, kind)
            if os.path.abspath(dirpath) == os.path.abspath(dst_dir):
                continue              # already filed in its own category folder
            seen.add(src)
            dst = os.path.join(dst_dir, f)
            n = 2
            while os.path.exists(dst) and os.path.abspath(dst) != os.path.abspath(src):
                stem, ext = os.path.splitext(f)
                dst = os.path.join(dst_dir, f"{stem} ({n}){ext}")
                n += 1
            plan.append((src, dst_dir, dst))
    if not plan:
        return text(f"{target} is already tidy — no loose files.")
    lines = [f"Would move {len(plan)} file(s) in {target}:"]
    for src, dst_dir, dst in plan[:25]:
        lines.append(f"  {os.path.basename(src)} -> {os.path.basename(dst_dir)}\\")
    if len(plan) > 25:
        lines.append(f"  ...and {len(plan) - 25} more")
    if args.get("dry_run", True):
        return text("\n".join(lines) + "\nSay 'do it' (dry_run=false) to apply.")
    import shutil
    for src, dst_dir, dst in plan:
        os.makedirs(dst_dir, exist_ok=True)
        shutil.move(src, dst)
    return text("\n".join(lines[:5]) + f"\nDone — moved {len(plan)} file(s)." if len(lines) > 5 else "\n".join(lines))


@tool("find_duplicates", "Find files with the same content inside a folder (exact copies, any name). Returns "
      "groups with full paths. folder: 'Downloads', 'Desktop', or a full path.", {"folder": str})
async def find_duplicates(args):
    def run():
        import hashlib
        root = args.get("folder", "Downloads")
        root = os.path.expanduser(os.path.join(config.HOME, root) if root in ("Downloads", "Desktop", "Documents") else root)
        by_size = {}
        for dirpath, dirnames, filenames in os.walk(root):
            for f in filenames:
                p = os.path.join(dirpath, f)
                try:
                    by_size.setdefault(os.path.getsize(p), []).append(p)
                except OSError:
                    pass
        groups = {}
        for size, paths in by_size.items():
            if len(paths) < 2 or size == 0:
                continue
            for p in paths:
                h = hashlib.sha256()
                try:
                    with open(p, "rb") as f:
                        for chunk in iter(lambda: f.read(1 << 20), b""):
                            h.update(chunk)
                    groups.setdefault((size, h.hexdigest()), []).append(p)
                except OSError:
                    pass
        return [ps for ps in groups.values() if len(ps) > 1], root
    groups, root = await asyncio.to_thread(run)
    if not groups:
        return text(f"No exact duplicates found in {root}.")
    out = [f"{len(groups)} duplicate group(s) in {root}:"]
    for ps in groups[:10]:
        out.append(f"  {len(ps)}x, {os.path.getsize(ps[0]) // 1024} KB:")
        out += [f"    {p}" for p in ps[:4]]
    return text("\n".join(out))


@tool("biggest_files", "List the biggest files in a folder. folder: 'Downloads', 'Desktop', 'Documents', or a full "
      "path. count: how many to list (default 10).", {"folder": str, "count": int})
async def biggest_files(args):
    def run():
        root = args.get("folder", "Downloads")
        root = os.path.expanduser(os.path.join(config.HOME, root) if root in ("Downloads", "Desktop", "Documents") else root)
        out = []
        for dirpath, dirnames, filenames in os.walk(root):
            for f in filenames:
                p = os.path.join(dirpath, f)
                try:
                    out.append((os.path.getsize(p), p))
                except OSError:
                    pass
        return sorted(out, reverse=True)[:max(1, min(50, int(args.get("count", 10))))]
    rows = await asyncio.to_thread(run)
    return text("\n".join(f"{s / 1e6:.1f} MB  {p}" for s, p in rows) if rows else "Nothing found.")


# ---------- timers ----------

_TIMERS = {}                      # label -> asyncio.Task


@tool("set_timer", "Start a countdown timer. When it finishes Jarvis tells you. seconds: how long. label: what for.",
      {"seconds": int, "label": str})
async def set_timer(args):
    seconds = max(1, min(86400, int(args["seconds"])))
    label = args.get("label", "").strip() or "timer"
    old = _TIMERS.pop(label, None)
    if old:
        old.cancel()

    async def wait():
        try:
            await asyncio.sleep(seconds)
            events.emit("timer", label=label, seconds=seconds)
            if NOTIFY:
                NOTIFY(f"[timer, not from {config.USER_NAME}] The {label} timer ({seconds} s) has finished. Tell the user it's done.")
        except asyncio.CancelledError:
            pass
        finally:
            _TIMERS.pop(label, None)

    _TIMERS[label] = asyncio.create_task(wait())
    return text(f"Timer '{label}' set for {seconds} seconds.")


@tool("list_timers", "Show the countdown timers that are still running.", {})
async def list_timers(args):
    if not _TIMERS:
        return text("No timers running.")
    return text("Running timers: " + ", ".join(_TIMERS))


@tool("cancel_timer", "Cancel a running timer by its label.", {"label": str})
async def cancel_timer(args):
    t = _TIMERS.pop(args.get("label", ""), None)
    if t is None:
        return text(f"No timer named '{args.get('label')}'.")
    t.cancel()
    return text(f"Cancelled '{args['label']}'.")


# ---------- UI-Automation clicking ----------

@tool("ui_click", "Click a button/link/menu item by its visible text inside the window that is currently in front, "
      "using Windows UI Automation instead of pixel coordinates. Only that focused window is searched, so it will "
      "never touch another app. text: the label, e.g. 'Send', 'Save', 'Download'.",
      {"text": str})
async def ui_click(args):
    needle = args["text"].strip().lower()
    if not needle:
        return text("Tell me what to click (the button's visible text).")
    def run():
        from pywinauto import Desktop
        active = winapi.active_window() or {}
        if not active.get("title"):
            return "NO_WINDOW"
        try:
            root = Desktop(backend="uia").window(title_re=f".*{re.escape(active['title'][:40])}.*")
            root.wait("exists ready", timeout=2)
        except Exception:
            return "NO_WINDOW"
        best = None
        for el in root.descendants():
            try:
                ctype = el.element_info.control_type
            except Exception:
                continue
            if ctype not in ("Button", "MenuItem", "Hyperlink", "TabItem", "TreeItem", "ListItem", "Text"):
                continue
            try:
                if needle in (el.window_text() or "").lower() and el.is_visible() and el.is_enabled():
                    # prefer an exact label match, else first substring hit
                    if (el.window_text() or "").strip().lower() == needle:
                        el.click_input()
                        return el.window_text()
                    if best is None:
                        best = el
            except Exception:
                continue
        if best is not None:
            best.click_input()
            return best.window_text()
        return None
    try:
        hit = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"UI click failed: {e}")
    if hit == "NO_WINDOW":
        return text("I couldn't tell which window is in front. Bring it to the front and tell me again.")
    return text(f"Clicked '{hit}'." if hit else f"No clickable control matching '{args['text']}' in the front window.")


# ---------- browser automation ----------

@tool("web_browse", "Drive a real browser: open a URL (or search the web when given query instead), then optionally "
      "click a link/button by its text. Returns the page title and a trimmed text of the page.",
      {"url": str, "query": str, "click": str})
async def web_browse(args):
    def run():
        from playwright.sync_api import sync_playwright
        import urllib.parse as up
        url = args.get("url", "").strip()
        query = args.get("query", "").strip()
        click = args.get("click", "").strip()
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            try:
                page = browser.new_page()
                target = url or ("https://www.bing.com/search?q=" + up.quote_plus(query))
                page.goto(target, timeout=30000, wait_until="domcontentloaded")
                if click:
                    page.get_by_text(click, exact=False).first.click(timeout=8000)
                    page.wait_for_load_state("domcontentloaded", timeout=15000)
                title = page.title()
                body = page.inner_text("body")[:3000]
                return f"{title}\n{page.url}\n\n{body}"
            finally:
                browser.close()
    try:
        out = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Browser failed: {e}")
    return text(out or "(empty page)")


@tool("current_date", "What today is (day, date, time, local). Use it before answering anything time-sensitive: news, "
      "weather, 'latest', 'current'.", {})
async def current_date(args):
    import datetime
    now = datetime.datetime.now().astimezone()
    return text(now.strftime("%A, %d %B %Y, %H:%M %Z%z").replace("  ", " "))


# ---------- quick system info ----------

@tool("system_info", "Read this PC's vitals: CPU load, memory, disk free, battery.", {})
async def system_info(args):
    def run():
        cpu = psutil.cpu_percent(interval=0.6)
        mem = psutil.virtual_memory()
        disk = psutil.disk_usage(config.HOME[:3] if os.name == "nt" else "/")
        battery = psutil.sensors_battery()
        parts = [f"CPU {cpu}%", f"RAM {round(mem.used / mem.total * 100)}% ({round(mem.available / 1e9, 1)} GB free of {round(mem.total / 1e9)} GB)",
                 f"Disk {round(disk.free / 1e9)} GB free on {os.path.splitdrive(config.HOME)[0] or 'C:'}"]
        if battery:
            parts.append(f"Battery {round(battery.percent)}%{', on power' if battery.power_plugged else ''}")
        return parts
    parts = await asyncio.to_thread(run)
    return text(". ".join(parts) + ".")


@tool("weather_now", "Current weather for Ash's city (config CITY), from wttr.in.", {})
async def weather_now(args):
    import urllib.request
    def run():
        url = f"https://wttr.in/{config.CITY}?format=j1"
        req = urllib.request.Request(url, headers={"User-Agent": "curl"})
        data = json.loads(urllib.request.urlopen(req, timeout=20).read())
        cur = data["current_condition"][0]
        loc = data["nearest_area"][0]["areaName"][0]["value"]
        line = (f"{loc}: {cur['weatherDesc'][0]['value']}, {cur['temp_C']}\u00b0C, feels like {cur['FeelsLikeC']}\u00b0C, "
                f"wind {cur['windspeedKmph']} km/h, humidity {cur['humidity']}%.")
        days = data.get("weather", [])
        if len(days) >= 2:
            tmr = days[1]
            tmr_desc = (tmr.get("hourly") or [{}])[4].get("weatherDesc", [{}])[0].get("value", "?")
            line += f" Tomorrow: {tmr_desc}, {tmr.get('mintempC')} to {tmr.get('maxtempC')}\u00b0C."
        return line
    try:
        return text(await asyncio.to_thread(run))
    except Exception as e:
        return text(f"Couldn't get the weather: {e}")


@tool("clipboard", "The clipboard: action 'read' to see what you copied, 'write' to put text on it. text only for write.",
      {"action": str, "text": str})
async def clipboard(args):
    action = args.get("action", "read").lower()
    def run():
        if action == "write":
            proc = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command",
                                   "[Console]::InputEncoding=[Text.Encoding]::UTF8; Set-Clipboard -Value ([Console]::In.ReadToEnd())"],
                                  input=args.get("text", ""), text=True, encoding="utf-8", capture_output=True, timeout=20,
                                  creationflags=NO_WINDOW)
            return "" if proc.returncode == 0 else (proc.stderr or "clipboard write failed")
        return subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", "Get-Clipboard"],
                              text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=20,
                              creationflags=NO_WINDOW).stdout
    try:
        out = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Clipboard failed: {e}")
    if action == "write":
        return text("Clipboard updated." if not out else f"Couldn't copy: {out}")
    return text(out.strip() if out.strip() else "The clipboard is empty (or holds no text).")


# ---------- calendar & email (classic Outlook via COM, .ics fallback) ----------

_OL_PRELUDE = ("$ErrorActionPreference='Stop'; $ol = New-Object -ComObject Outlook.Application; "
               "$ns = $ol.GetNamespace('MAPI'); ")


def _parse_start(raw):
    import datetime
    raw = (raw or "").strip()
    for fmt in ("%Y-%m-%d %H:%M", "%Y-%m-%dT%H:%M", "%d %B %Y %H:%M", "%d/%m/%Y %H:%M", "%d %b %Y %H:%M"):
        try:
            return datetime.datetime.strptime(raw, fmt)
        except ValueError:
            continue
    try:
        return datetime.datetime.fromisoformat(raw.replace("Z", ""))
    except Exception:
        return None


def _resolve_start(raw):
    """'2026-10-04 15:30', '4 October 2026 09:00', 'tomorrow 15:30', 'today 23:15', 'friday 18:00' -> datetime."""
    import datetime
    exact = _parse_start(raw)
    if exact:
        return exact
    now = datetime.datetime.now()
    when = re.sub(r"[^\w: ]", " ", (raw or "").lower()).strip()
    parts = when.split()
    days = ("monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday")
    day = None
    if parts and parts[0] == "today":
        day = 0
    elif parts and parts[0] == "tomorrow":
        day = 1
    elif parts and parts[0] in days:
        day = (days.index(parts[0]) - now.weekday()) % 7 or 7
    clock = next((p for p in parts if ":" in p and p.replace(":", "").isdigit()), "")
    if day is None or not clock:
        return None
    hh, mm = clock.split(":")
    return (now + datetime.timedelta(days=day)).replace(hour=int(hh) % 24, minute=int(mm) % 60,
                                                        second=0, microsecond=0)


@tool("calendar_add", "Put an event in the user's Outlook calendar (classic Outlook is started if needed). Falls "
      "back to writing an .ics in ~/calendar and opening it when Outlook isn't available. "
      "title: event name. start: 'YYYY-MM-DD HH:MM' or 'tomorrow 15:30'. duration_minutes: default 30. "
      "location: optional.", {"title": str, "start": str, "duration_minutes": int, "location": str})
async def calendar_add(args):
    import datetime
    import re as _re
    raw = args["start"].strip()
    start = _resolve_start(raw)
    if start is None:
        return text(f"Couldn't read the start time '{args['start']}' — use 'YYYY-MM-DD HH:MM' or 'tomorrow 15:30'.")
    minutes = max(5, min(1440, int(args.get("duration_minutes", 30))))
    end = start + datetime.timedelta(minutes=minutes)
    title = args["title"].strip()
    location = (args.get("location") or "").strip()
    when_txt = f"{start:%A %d %B, %H:%M} for {minutes} minutes"
    # 1) the real calendar, when classic Outlook answers
    def via_outlook():
        import outlook
        ps = (_OL_PRELUDE
              + "$cal = $ns.GetDefaultFolder(9); $item = $cal.Items.Add(0); "
              + "$item.Subject = '" + title.replace("'", "''") + "'; "
              + f"$item.Start = Get-Date '{start:%Y-%m-%d %H:%M}'; "
              + f"$item.End = Get-Date '{end:%Y-%m-%d %H:%M}'; ")
        if location:
            ps += "$item.Location = '" + location.replace("'", "''") + "'; "
        ps += ("$item.Save(); "
               + "$s = $item.Start.ToString('ddd dd MMM HH:mm'); "
               + "Write-Output \"$s|$($item.Subject)\"")
        return outlook.run(ps)[0]
    try:
        out = await asyncio.to_thread(via_outlook)
        note = " (I had to start Outlook)" if "|" in out else ""
        return text(f"Added '{title}' to your Outlook calendar{note}: {when_txt}.")
    except Exception as e:                   # no Outlook: the .ics import path still gets the event in
        note = f"Outlook isn't available ({e}), so I wrote an .ics and opened it to import."
    os.makedirs(os.path.join(config.HOME, "calendar"), exist_ok=True)
    fname = _re.sub(r"[^\w -]", "", title).strip().replace(" ", "_") + ".ics"
    path = os.path.join(config.HOME, "calendar", fname)
    stamp = lambda d: d.strftime("%Y%m%dT%H%M%S")
    body = (f"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:{int(time.time())}@jarvis\r\n"
            f"DTSTAMP:{stamp(datetime.datetime.utcnow())}\r\nDTSTART:{stamp(start)}\r\nDTEND:{stamp(end)}\r\n"
            f"SUMMARY:{title}\r\nLOCATION:{location}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(body)
    try:
        os.startfile(path)
    except OSError:
        pass
    return text(f"Event '{title}' {when_txt}; {note} Saved to {path}.")


@tool("list_events", "Show the user's upcoming Outlook calendar events (classic Outlook; the new Store app is not "
      "supported). count: how many (default 5). days: how far ahead (default 14).", {"count": int, "days": int})
async def list_events(args):
    n = max(1, min(30, int(args.get("count", 5))))
    days = max(1, min(365, int(args.get("days", 14))))
    def run():
        import outlook
        ps = (_OL_PRELUDE
              + "$cal = $ns.GetDefaultFolder(9); "
              + f"$cal.Items | Where-Object {{ $_.Start -ge (Get-Date) -and $_.Start -le (Get-Date).AddDays({days}) }} "
              + "| Sort-Object Start | Select-Object -First " + str(n)
              + " | ForEach-Object { \"$($_.Start.ToString('ddd dd MMM HH:mm')) | $($_.Subject)\" }")
        return outlook.run(ps)[0]
    try:
        out = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Couldn't read the calendar: {e}")
    return text(out[:2500] if out else f"No events in the next {days} days.")


@tool("draft_email", "Save a new email as a draft in Outlook (classic Outlook) without sending it. "
      "to: recipient. subject: subject line. body: the message.", {"to": str, "subject": str, "body": str})
async def draft_email(args):
    def run():
        import outlook
        q = lambda s: "'" + str(s).replace("'", "''") + "'"
        ps = (_OL_PRELUDE
              + "$item = $ns.GetDefaultFolder(6).Items.Add(0); "
              + "$item.To = " + q(args["to"]) + "; "
              + "$item.Subject = " + q(args["subject"]) + "; "
              + "$item.Body = " + q(args["body"]) + "; "
              + "$item.Save(); Write-Output 'saved'")
        return outlook.run(ps)[0]
    try:
        await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Couldn't save the draft: {e}")
    return text(f"Draft saved to {args['to']}: \"{args['subject']}\". It's waiting in your Drafts — I didn't send it.")


@tool("list_emails", "Show recent emails from Outlook. Classic Outlook for Windows is started if needed; the new "
      "Store app is not supported. Returns subject, sender, received time. count: how many (default 5).", {"count": int})
async def list_emails(args):
    n = max(1, min(20, int(args.get("count", 5))))
    def run():
        import outlook
        ps = (_OL_PRELUDE
              + "$inbox = $ns.GetDefaultFolder(6); $items = $inbox.Items | Sort-Object ReceivedTime -Descending | "
              + "Select-Object -First " + str(n)
              + " | ForEach-Object { \"$($_.ReceivedTime.ToString('dd MMM HH:mm')) | $($_.SenderName) | $($_.Subject)\" }")
        return outlook.run(ps)
    try:
        out, started = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Couldn't read Outlook: {e}")
    note = "I had to start Outlook for that. " if started else ""
    return text((note + out)[:3000] if out else f"{note}No emails found.")


@tool("self_check", "Run the project's built-in checks (agent loop, safety rules, yes/no parsing, workers, time "
      "tags) and report the result. Run it if you suspect something's off.", {})
async def self_check(args):
    def run():
        out = []
        for script in ("test_brain.py", "test_time_tag.py", "test_tools.py"):
            r = subprocess.run([sys.executable, os.path.join(config.JARVIS_DIR, script)],
                               capture_output=True, text=True, timeout=180, cwd=config.JARVIS_DIR,
                               creationflags=NO_WINDOW)
            last = (r.stdout.strip().splitlines() or [""])[-1]
            out.append(f"{script}: exit {r.returncode} — {last}")
        return out
    out = await asyncio.to_thread(run)
    return text("\n".join(out))


# ---------- everyday info ----------

@tool("find_file", "Find a file by name (or part of its name/extension) in your home, Desktop, Documents and "
      "Downloads. Returns full paths, best 15 matches.", {"name": str})
async def find_file(args):
    def run():
        roots = [os.path.expanduser(p) for p in ("~", "~\\Desktop", "~\\Documents", "~\\Downloads")]
        skip = {".git", "node_modules", "AppData", "__pycache__", ".venv", "windows", "logs"}
        hits, needle = [], args["name"].lower()
        for root in dict.fromkeys(roots):
            for dirpath, dirnames, filenames in os.walk(root):
                dirnames[:] = [d for d in dirnames if d not in skip and not d.startswith(".")]
                if dirpath.count(os.sep) - root.count(os.sep) > 4:
                    dirnames[:] = []
                    continue
                for f in filenames:
                    if needle in f.lower():
                        hits.append(os.path.join(dirpath, f))
                        if len(hits) >= 15:
                            return hits
        return hits
    hits = await asyncio.to_thread(run)
    return text("\n".join(hits) if hits else f"Nothing found matching '{args['name']}'.")


@tool("web_search", "Search the web and get the top results (title, a short snippet, the link). Use it for "
      "facts, news, 'how do I...' questions.", {"query": str})
async def web_search(args):
    import html
    import urllib.parse
    import urllib.request

    def run():
        import base64, html, urllib.parse as up, urllib.request
        url = "https://www.bing.com/search?q=" + up.quote_plus(args["query"])
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        page = urllib.request.urlopen(req, timeout=20).read().decode("utf-8", "replace")
        out = []
        for m in re.finditer(r'<li class="b_algo"[^>]*>(.*?)(?=<li class="b_algo"|</ol>)', page, re.S):
            block = m.group(1)
            t = re.search(r'<h2[^>]*><a[^>]*href="([^"]+)"[^>]*>(.*?)</a>', block, re.S)
            sn = re.search(r'<p[^>]*>(.*?)</p>', block, re.S)
            if not t:
                continue
            link, title = t.group(1), html.unescape(re.sub("<.*?>", "", t.group(2)))
            if "bing.com/ck/a" in link:                          # Bing wraps results in a redirect; unwrap it
                u = up.parse_qs(up.urlparse(link.replace("&amp;", "&")).query).get("u", [""])[0]
                if u.startswith("a1"):
                    try:
                        link = base64.urlsafe_b64decode(u[2:] + "=" * (-len(u[2:]) % 4)).decode()
                    except Exception:
                        pass
            snip = html.unescape(re.sub("<.*?>", "", sn.group(1))) if sn else ""
            out.append(f"{title.strip()}\n{snip.strip()}\n{link}")
        return out
    try:
        out = await asyncio.to_thread(run)
    except Exception as e:
        return text(f"Search failed: {e}")
    return text("\n\n".join(out) if out else "No results.")


_FACTS_FILE = os.path.join(config.JARVIS_DIR, "facts.md")


@tool("remember", "Write down a durable fact about the user or their PC (name spelling, a project, a "
      "preference). It is kept forever and shown to every future session.", {"fact": str})
async def remember(args):
    stamp = time.strftime("%Y-%m-%d %H:%M")
    with open(_FACTS_FILE, "a", encoding="utf-8") as f:
        f.write(f"- {args['fact'].strip()} ({stamp})\n")
    return text(f"Noted: {args['fact'].strip()}")


@tool("forget", "Remove a durable fact Jarvis was told earlier, when it is out of date or was wrong. "
      "query: a distinctive part of the fact to delete.", {"query": str})
async def forget(args):
    needle = args["query"].strip().lower()
    if not needle:
        return text("Tell me which fact to forget (quote part of it).")
    try:
        with open(_FACTS_FILE, encoding="utf-8") as f:
            lines = f.read().splitlines()
    except FileNotFoundError:
        return text("Nothing remembered yet.")
    keep = [l for l in lines if needle not in l.lower()]
    gone = len(lines) - len(keep)
    if not gone:
        return text(f"No remembered fact contains '{args['query']}'.")
    with open(_FACTS_FILE, "w", encoding="utf-8") as f:
        f.write("\n".join(keep) + ("\n" if keep else ""))
    return text(f"Forgot {gone} line(s) containing '{args['query']}'. The rest are still there.")


def _memory_lines():
    """Durable facts first, then the cross-session notes, each line tagged with where it came from."""
    out = []
    try:
        with open(_FACTS_FILE, encoding="utf-8") as f:
            out += [("fact", l.strip()) for l in f.read().splitlines() if l.strip().startswith("-")]
    except (FileNotFoundError, OSError):
        pass
    try:
        with open(getattr(config, "NOTES_FILE", ""), encoding="utf-8") as f:
            for l in f.read().splitlines():
                l = l.strip().lstrip("#- ").strip()
                if len(l) > 2 and not l.lower().startswith(("jarvis", "written", "session", "-", "notes")):
                    out.append(("notes", l))
    except (FileNotFoundError, OSError):
        pass
    return out


@tool("recall", "Search everything Jarvis knows: durable facts (remember) and the notes it wrote in earlier "
      "sessions, best matches first, each labelled with its source. Optional query filters/reranks.", {"query": str})
async def recall(args):
    rows = _memory_lines()
    query = args.get("query", "").lower().strip()
    if not query:
        return text("\n".join(f"[{src}] {l}" for src, l in rows) if rows else "No matching facts.")
    # rank by a tiny TF-IDF cosine, so "what's my project name" finds a fact about the project
    import math
    def tokens(s):
        return set(re.findall(r"[a-z0-9']+", s.lower()))
    docs, idf = [], {}
    for _, l in rows:
        ts = tokens(l)
        docs.append((l, ts))
        for t in ts:
            idf[t] = idf.get(t, 0) + 1
    q = tokens(query)
    scored = []
    for i, (l, ts) in enumerate(docs):
        if not q & ts:
            continue
        score = sum(1 / math.log(1 + idf[t]) for t in (q & ts)) / math.sqrt(len(ts) + 1)
        if rows[i][0] == "fact":
            score *= 1.15                            # a stated fact outranks a passing note
        scored.append((score, i, l))
    scored.sort(reverse=True)
    return text("\n".join(f"[{rows[i][0]}] {l}" for _, i, l in scored[:8]) if scored else "No matching facts.")


# ---------- windows, screen, mouse, keyboard ----------

@tool("list_windows", "List open windows: title, app, position/size in desktop pixels, which one is active.", {})
async def list_windows(args):
    return text("\n".join(f"{'* ' if w['active'] else ''}{w['title']} [{w['app']}] at {w['x']},{w['y']} "
                          f"{w['w']}x{w['h']}{' (minimised)' if w['minimized'] else ''}" for w in winapi.windows()))


@tool("focus_window", "Bring a window to the front and give it keyboard focus. query matches the window title "
      "or app name, e.g. 'discord', 'outlook', 'Notepad'.", {"query": str})
async def focus_window(args):
    w = winapi.focus(args["query"])
    if not w:
        return text(f"No open window matches '{args['query']}'. Try list_windows.")
    if not w["active"]:
        return text(f"Found '{w['title']}' but Windows wouldn't bring it to the front, so it does NOT have focus. "
                    "Don't type yet: click on it in a screenshot, or ask the user to.")
    return text(f"Focused '{w['title']}' at {w['x']},{w['y']} {w['w']}x{w['h']}.")


LAST_SHOT = {"x": 0, "y": 0, "scale": 1.0}
SHOT = contextvars.ContextVar("shot", default=LAST_SHOT)    # each agent sets its own (brain.Agent.run)


@tool("screenshot", "Look at the screen. target: 'window' (the active window, sharpest), 'left' or 'right' "
      "(one monitor, full detail) or 'both' (every monitor, lower resolution). click_at, move_mouse and scroll "
      "take pixel coordinates inside the most recent screenshot image.", {"target": str})
async def screenshot(args):
    target = args.get("target", "both")
    with mss.mss() as sct:
        monitors = sorted(sct.monitors[1:], key=lambda m: m["left"])
        region, label = sct.monitors[0], "every monitor"
        if target in ("left", "right"):
            region, label = monitors[0] if target == "left" else monitors[-1], f"the {target} monitor"
        elif target == "window":
            w = winapi.active_window()
            if w and w["w"] > 0 and w["h"] > 0 and not w["minimized"]:
                region = {"left": w["x"], "top": w["y"], "width": w["w"], "height": w["h"]}
                label = f"the active window '{w['title']}'"
        shot = sct.grab(region)
    img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
    scale = max(1.0, img.width / 1920, img.height / 1080)
    if scale > 1:
        img = img.resize((round(img.width / scale), round(img.height / scale)))
    SHOT.get().update(x=region["left"], y=region["top"], scale=scale)
    buf = io.BytesIO()
    img.save(buf, "JPEG", quality=80)
    save_shot(buf.getvalue(), label)
    note = f"Screenshot of {label}, {img.width}x{img.height}. Use these image pixel coordinates with click_at."
    return {"content": [{"type": "text", "text": note},
                        {"type": "image", "data": base64.b64encode(buf.getvalue()).decode(), "mimeType": "image/jpeg"}]}


SHOTS_DIR = os.path.join(config.JARVIS_DIR, "logs", "shots")


def save_shot(jpeg, label):
    """Keep what Jarvis saw, for the dashboard. Only the newest 300 are kept."""
    os.makedirs(SHOTS_DIR, exist_ok=True)
    name = time.strftime("%Y%m%d-%H%M%S") + f"-{int(time.time() * 1000) % 1000:03d}.jpg"
    with open(os.path.join(SHOTS_DIR, name), "wb") as f:
        f.write(jpeg)
    events.emit("screenshot", file=name, what=label, sub=SHOT.get().get("sub", False), agent=SHOT.get().get("agent"))
    for old in sorted(os.listdir(SHOTS_DIR))[:-300]:
        os.remove(os.path.join(SHOTS_DIR, old))


def to_desktop(x, y):
    shot = SHOT.get()
    return shot["x"] + x * shot["scale"], shot["y"] + y * shot["scale"]


@tool("click_at", "Click at a point in the most recent screenshot image (pixel coordinates in that image). "
      "button: left, right or middle. clicks: 1 or 2 (double-click).", {"x": int, "y": int, "button": str, "clicks": int})
async def click_at(args):
    x, y = to_desktop(args["x"], args["y"])
    if not winapi.move(x, y):
        return text("Couldn't get the mouse to that spot.")
    button = args.get("button", "left") if args.get("button") in ("left", "right", "middle") else "left"
    clicks = max(1, min(3, args.get("clicks", 1)))
    winapi.click(button, clicks)
    return text(f"Clicked {button} x{clicks} at desktop {round(x)},{round(y)}.")


@tool("move_mouse", "Move the mouse to a point in the most recent screenshot image (e.g. to hover).", {"x": int, "y": int})
async def move_mouse(args):
    return text("Moved." if winapi.move(*to_desktop(args["x"], args["y"])) else "Couldn't get the mouse to that spot.")


@tool("scroll", "Scroll the mouse wheel over a point in the most recent screenshot image. direction: up or down. "
      "amount: notches, 1-20.", {"x": int, "y": int, "direction": str, "amount": int})
async def scroll(args):
    winapi.move(*to_desktop(args["x"], args["y"]))
    n = max(1, min(20, args.get("amount", 3)))
    winapi.wheel(-n if args["direction"] == "down" else n)
    return text(f"Scrolled {args['direction']} {n}.")


@tool("type_text", "Type text into whatever window has focus, as if on the keyboard. Newlines are NOT allowed; "
      "use press_keys 'enter' separately.", {"text": str})
async def type_text(args):
    stop = threading.Event()
    try:
        await asyncio.to_thread(winapi.type_text, args["text"].replace("\n", " "), stop)
    except asyncio.CancelledError:
        stop.set()                           # the thread itself can't be cancelled; this ends it at the next character
        raise
    return text("Typed.")


@tool("press_keys", "Press a key combination in the focused window, e.g. 'ctrl+t', 'alt+tab', 'enter', "
      "'win', 'ctrl+shift+esc'.", {"keys": str})
async def press_keys(args):
    names = [k.strip().lower() for k in args["keys"].split("+") if k.strip()]
    missing = [n for n in names if n not in winapi.VK]
    if missing or not names:
        return text(f"Unknown key(s): {', '.join(missing) or args['keys']}.")
    winapi.press(names)
    return text(f"Pressed {args['keys']}.")


@tool("notify", "Show a desktop notification (for things worth seeing, like a link or a number to copy).",
      {"title": str, "body": str})
async def notify(args):
    from plyer import notification             # Windows balloon; it blocks while shown, so it gets its own thread
    threading.Thread(target=notification.notify, daemon=True, kwargs={
        "title": args["title"][:63], "message": args["body"][:255], "app_name": "Jarvis", "timeout": 10}).start()
    return text("Shown.")


# ---------- show me where ----------

OVERLAY = {"proc": None}
OVERLAY_LOG = os.path.join(config.JARVIS_DIR, "logs", "overlay.log")
SHAPE = {"type": "object", "required": ["type", "x", "y"], "properties": {
    "type": {"type": "string", "enum": ["ring", "arrow", "rect"]},
    "x": {"type": "number"}, "y": {"type": "number"}, "radius": {"type": "number"},
    "from_x": {"type": "number"}, "from_y": {"type": "number"}, "w": {"type": "number"}, "h": {"type": "number"},
    "label": {"type": "string"}, "step": {"type": "integer"}}}


def shapes_to_desktop(shapes):
    """Screenshot-image coordinates -> desktop pixels, the same mapping click_at uses; sizes scale with it."""
    out = []
    for s in shapes:
        s = dict(s)
        for kx, ky in (("x", "y"), ("from_x", "from_y")):
            if kx in s and ky in s:
                s[kx], s[ky] = to_desktop(float(s[kx]), float(s[ky]))
        for k in ("radius", "w", "h"):
            if k in s:
                s[k] = float(s[k]) * SHOT.get()["scale"]
        out.append(s)
    return out


def stop_overlay():
    if OVERLAY["proc"] and OVERLAY["proc"].poll() is None:
        OVERLAY["proc"].terminate()


@tool("annotate", "Draw on the user's screen to SHOW them where something is (blue rings, arrows, boxes and labels; "
      "click-through, clears itself). Coordinates are pixels in the most recent screenshot image, exactly like "
      "click_at. Each shape: type 'ring' (x, y = centre, radius), 'arrow' (from_x, from_y -> x, y; the head is at x, y, "
      "the target) or 'rect' (x, y = top-left, w, h); optional label (2-4 words; add '\\n' and a short detail line if "
      "useful) and step (1, 2, 3 for a sequence). duration: seconds before it clears, default 8. A new annotate "
      "replaces the old drawing.",
      {"type": "object", "required": ["shapes"], "properties": {
          "shapes": {"type": "array", "items": SHAPE}, "duration": {"type": "number"}}})
async def annotate(args):
    shapes = args.get("shapes") or []
    if not shapes:
        return text("Nothing to draw.")
    try:
        desktop = shapes_to_desktop(shapes)
    except (KeyError, TypeError, ValueError) as e:
        return text(f"Bad shape: {e}.")
    duration = max(1.0, min(120.0, float(args.get("duration") or 8)))
    stop_overlay()
    with open(OVERLAY_LOG, "w") as log:
        OVERLAY["proc"] = subprocess.Popen([sys.executable, os.path.join(config.JARVIS_DIR, "overlay.py"),
                                            json.dumps({"shapes": desktop, "duration": duration})],
                                           stdout=log, stderr=log, creationflags=NO_WINDOW)
    await asyncio.sleep(0.6)                 # a bad shape or a broken Qt shows up as an instant exit
    if OVERLAY["proc"].poll() not in (None, 0):
        with open(OVERLAY_LOG) as f:
            return text("The overlay failed: " + f.read()[-400:])
    return text(f"Drawing {len(shapes)} shape(s) on screen for {duration:g}s.")


@tool("clear_annotations", "Remove whatever annotate drew on the screen, right now.", {})
async def clear_annotations(args):
    stop_overlay()
    return text("Cleared.")


# ---------- commands and files ----------

def kill_tree(proc):
    """Stop a command and everything it started (Windows doesn't take the children down with their parent)."""
    # ponytail: misses anything that detached and re-parented before this ran; upgrade path is a Windows Job Object
    try:
        kids = psutil.Process(proc.pid).children(recursive=True)
    except psutil.Error:
        kids = []
    for p in kids + [proc]:
        try:
            p.kill()
        except (psutil.Error, ProcessLookupError):
            pass


async def read_tail(stream, keep=32000):
    """Everything the command prints, keeping only the last `keep` bytes in memory."""
    buf = bytearray()
    while chunk := await stream.read(65536):
        buf += chunk
        del buf[:-keep]
    return bytes(buf)


@tool("run_command", "Run a PowerShell command on this PC and get its output (last 8000 characters). For files and "
      "folders, system info, web requests (Invoke-RestMethod, curl.exe) and anything without its own tool. "
      "description: a few plain words on what it does (read out if the user is asked to approve it). "
      "timeout: seconds, 1-600.", {"command": str, "description": str, "timeout": int})
async def run_command(args):
    command = args.get("command") or args.get("cmd") or args.get("script") or args.get("command_line") or ""
    if not str(command).strip():
        return text("Tell me what to run: one PowerShell command, plus a short description.")
    shell = powershell(command) if os.name == "nt" else ["bash", "-c", command]
    proc = await asyncio.create_subprocess_exec(*shell, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT,
                                                stdin=asyncio.subprocess.DEVNULL, cwd=config.HOME, creationflags=NO_WINDOW)
    try:
        out = await asyncio.wait_for(read_tail(proc.stdout), max(1, min(600, args.get("timeout") or 60)))
        await proc.wait()
    except (asyncio.TimeoutError, asyncio.CancelledError) as e:
        kill_tree(proc)
        try:
            await asyncio.wait_for(proc.wait(), 5)
        except (asyncio.TimeoutError, asyncio.CancelledError):
            pass
        if isinstance(e, asyncio.CancelledError):
            raise
        return text("Timed out and stopped. For long jobs, start a worker.")
    out = out.decode("utf-8", "replace").strip()
    return text((out[-8000:] or "(no output)") + (f"\n(exit code {proc.returncode})" if proc.returncode else ""))


@tool("write_file", "Create or overwrite a UTF-8 text file. Use it for anything too long to say: notes, drafts, code, "
      "research. Relative paths are in the user's home folder.", {"path": str, "content": str})
async def write_file(args):
    path = os.path.join(config.HOME, os.path.expanduser(args["path"]))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(args["content"])
        return text(f"Wrote {len(args['content'])} characters to {path}.")


# ---------- self-written tools ----------

PLUGIN_FILES = ("custom_tools",)           # modules Jarvis may write its own tools into

log = logging.getLogger("jarvis.pctools")


_BUILTIN_TOOLS = set()                  # filled in below, once the built-ins are all registered


_PLUGIN_TOOLS = set()                     # names the plugins registered last time, so deletions can be noticed


def load_plugins(reload=False):
    """Import the plugin modules so their @tool functions register. Returns the names now loaded."""
    import importlib
    loaded = []
    for mod_name in PLUGIN_FILES:
        try:
            if reload and mod_name in sys.modules:
                del sys.modules[mod_name]      # reload() reuses the old namespace, so deleted tools would linger
            mod = importlib.import_module(mod_name)
            loaded += [n for n, f in vars(mod).items()
                       if callable(f) and getattr(f, "spec", None) and f.spec["function"]["name"] in TOOLS]
        except Exception as e:
            log.warning("plugin %s failed to load: %s", mod_name, e)
    global _PLUGIN_TOOLS
    if reload:                             # a tool deleted from the file goes away instead of lingering
        for gone in _PLUGIN_TOOLS - set(loaded):
            TOOLS.pop(gone, None)
    _PLUGIN_TOOLS = set(loaded) - _BUILTIN_TOOLS
    return sorted(set(loaded))


try:                                     # Jarvis can extend itself: custom_tools.py is picked up at startup
    load_plugins()
except ImportError:
    pass


@tool("list_custom_tools", "List the tools Jarvis has written for itself in custom_tools.py.", {})
async def list_custom_tools(args):
    names = load_plugins(reload=True)
    return text(", ".join(names) if names else "No custom tools yet.")


@tool("reload_tools", "Re-read custom_tools.py so a tool Jarvis just wrote becomes available without a restart. "
      "Use it after appending a new tool, then call the tool by name.", {})
async def reload_tools(args):
    before = set(TOOLS) - _BUILTIN_TOOLS
    names = load_plugins(reload=True)
    added = sorted(set(TOOLS) - _BUILTIN_TOOLS - before)
    return text(f"{len(names)} custom tool(s) loaded{': ' + ', '.join(added) if added else ''}. "
                f"Call them by name now; no restart needed.")


_BUILTIN_TOOLS = set(TOOLS)              # what shipped with Jarvis, before any plugin is read
try:                                     # Jarvis can extend itself: custom_tools.py is picked up at startup
    load_plugins()
except ImportError:
    pass
