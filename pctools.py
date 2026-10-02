"""Jarvis's hands on the desktop: apps, media, volume, screenshots, mouse, keyboard, PowerShell and files.

Every tool is an async function registered with @tool; brain.py hands their specs to the model.
"""
import asyncio
import base64
import io
import json
import os
import subprocess
import sys
import threading
import time

import mss
from PIL import Image

import config
import events
import winapi

TOOLS = {}                 # name -> async function, with .spec for the model
TYPES = {str: "string", int: "integer", bool: "boolean"}
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)          # Jarvis runs windowless; so must its commands
PS_UTF8 = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "


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
            return text(f"{w['title']} was already open; brought it to the front.")
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
    try:
        os.startfile(os.path.expanduser(args["target"]))
    except OSError as e:
        return text(f"Couldn't open {args['target']}: {e}")
    return text(f"Opened {args['target']}.")


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


# ---------- windows, screen, mouse, keyboard ----------

@tool("list_windows", "List open windows: title, app, position/size in desktop pixels, which one is active.", {})
async def list_windows(args):
    return text("\n".join(f"{'* ' if w['active'] else ''}{w['title']} [{w['app']}] at {w['x']},{w['y']} "
                          f"{w['w']}x{w['h']}{' (minimised)' if w['minimized'] else ''}" for w in winapi.windows()))


@tool("focus_window", "Bring a window to the front and give it keyboard focus. query matches the window title "
      "or app name, e.g. 'discord', 'outlook', 'Notepad'.", {"query": str})
async def focus_window(args):
    w = winapi.focus(args["query"])
    return text(f"Focused '{w['title']}' at {w['x']},{w['y']} {w['w']}x{w['h']}." if w
                else f"No open window matches '{args['query']}'. Try list_windows.")


LAST_SHOT = {"x": 0, "y": 0, "scale": 1.0}


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
    LAST_SHOT.update(x=region["left"], y=region["top"], scale=scale)
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
    events.emit("screenshot", file=name, what=label)
    for old in sorted(os.listdir(SHOTS_DIR))[:-300]:
        os.remove(os.path.join(SHOTS_DIR, old))


def to_desktop(x, y):
    return LAST_SHOT["x"] + x * LAST_SHOT["scale"], LAST_SHOT["y"] + y * LAST_SHOT["scale"]


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
    await asyncio.to_thread(winapi.type_text, args["text"].replace("\n", " "))
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
                s[k] = float(s[k]) * LAST_SHOT["scale"]
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

@tool("run_command", "Run a PowerShell command on this PC and get its output (last 8000 characters). For files and "
      "folders, system info, web requests (Invoke-RestMethod, curl.exe) and anything without its own tool. "
      "description: a few plain words on what it does (read out if the user is asked to approve it). "
      "timeout: seconds, 1-600.", {"command": str, "description": str, "timeout": int})
async def run_command(args):
    shell = powershell(args["command"]) if os.name == "nt" else ["bash", "-c", args["command"]]
    proc = await asyncio.create_subprocess_exec(*shell, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT,
                                                stdin=asyncio.subprocess.DEVNULL, cwd=config.HOME, creationflags=NO_WINDOW)
    try:
        out, _ = await asyncio.wait_for(proc.communicate(), max(1, min(600, args.get("timeout") or 60)))
    except (asyncio.TimeoutError, asyncio.CancelledError) as e:
        proc.kill()
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
