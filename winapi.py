"""The Windows parts, straight through ctypes: windows, keyboard and mouse input, global hotkeys.

Imports on any OS (so the tests run anywhere); the functions themselves only work on Windows.
"""
import ctypes
import logging
import os
import threading
import time
from ctypes import wintypes

log = logging.getLogger("winapi")

if os.name == "nt":
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    dwmapi = ctypes.WinDLL("dwmapi")
    user32.GetForegroundWindow.restype = wintypes.HWND
    # physical pixels everywhere, so window rectangles, the cursor and screenshots all agree
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

VK = {"backspace": 0x08, "tab": 0x09, "enter": 0x0D, "return": 0x0D, "shift": 0x10, "ctrl": 0x11, "control": 0x11,
      "alt": 0x12, "pause": 0x13, "capslock": 0x14, "esc": 0x1B, "escape": 0x1B, "space": 0x20, "pageup": 0x21,
      "pagedown": 0x22, "end": 0x23, "home": 0x24, "left": 0x25, "up": 0x26, "right": 0x27, "down": 0x28,
      "print": 0x2C, "insert": 0x2D, "delete": 0x2E, "del": 0x2E, "win": 0x5B, "super": 0x5B, "meta": 0x5B,
      "menu": 0x5D, "semicolon": 0xBA, "equal": 0xBB, "plus": 0xBB, "comma": 0xBC, "minus": 0xBD, "dot": 0xBE,
      "period": 0xBE, "slash": 0xBF, "grave": 0xC0, "volumemute": 0xAD, "volumedown": 0xAE, "volumeup": 0xAF,
      "nexttrack": 0xB0, "prevtrack": 0xB1, "mediastop": 0xB2, "playpause": 0xB3}
VK.update({c: ord(c.upper()) for c in "abcdefghijklmnopqrstuvwxyz0123456789"})
VK.update({f"f{i}": 0x6F + i for i in range(1, 25)})
EXTENDED = {0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D}   # need the extended-key flag
MODS = {"alt": 1, "ctrl": 2, "control": 2, "shift": 4, "win": 8, "super": 8, "meta": 8}


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.LONG),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def send(*inputs):
    user32.SendInput(len(inputs), (INPUT * len(inputs))(*inputs), ctypes.sizeof(INPUT))


def key(vk, up=False):
    return INPUT(type=1, ki=KEYBDINPUT(wVk=vk, dwFlags=(2 if up else 0) | (1 if vk in EXTENDED else 0)))


def mouse(flags, data=0):
    return INPUT(type=0, mi=MOUSEINPUT(dwFlags=flags, mouseData=data))


# ---------- keyboard and mouse ----------

def press(names):
    """Hold the keys down in order, release them in reverse: ["ctrl", "shift", "esc"]."""
    vks = [VK[n] for n in names]
    send(*[key(v) for v in vks], *[key(v, up=True) for v in reversed(vks)])


def type_text(text):
    """Type any text as Unicode key events, so the keyboard layout doesn't matter."""
    units = memoryview(text.encode("utf-16-le")).cast("H")
    for u in units:
        send(INPUT(type=1, ki=KEYBDINPUT(wScan=u, dwFlags=4)), INPUT(type=1, ki=KEYBDINPUT(wScan=u, dwFlags=4 | 2)))
        time.sleep(0.004)


def move(x, y):
    return bool(user32.SetCursorPos(round(x), round(y)))


def click(button="left", clicks=1):
    down, up = {"left": (0x02, 0x04), "right": (0x08, 0x10), "middle": (0x20, 0x40)}[button]
    for _ in range(clicks):
        send(mouse(down), mouse(up))
        time.sleep(0.08)


def wheel(notches):
    """Positive scrolls up, negative down."""
    send(mouse(0x0800, 120 * notches))


# ---------- windows ----------

def _title(h):
    buf = ctypes.create_unicode_buffer(user32.GetWindowTextLengthW(h) + 1)
    user32.GetWindowTextW(h, buf, len(buf))
    return buf.value


def _app(h):
    """The window's program name without .exe, e.g. 'chrome', 'Spotify'."""
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(h, ctypes.byref(pid))
    hp = kernel32.OpenProcess(0x1000, False, pid.value)     # PROCESS_QUERY_LIMITED_INFORMATION
    if not hp:
        return ""
    try:
        buf, size = ctypes.create_unicode_buffer(32768), wintypes.DWORD(32768)
        kernel32.QueryFullProcessImageNameW(hp, 0, buf, ctypes.byref(size))
        return os.path.splitext(os.path.basename(buf.value))[0]
    finally:
        kernel32.CloseHandle(hp)


def _info(h, active):
    r = wintypes.RECT()          # the visible frame, without the invisible resize border GetWindowRect includes
    if dwmapi.DwmGetWindowAttribute(wintypes.HWND(h), 9, ctypes.byref(r), ctypes.sizeof(r)):
        user32.GetWindowRect(h, ctypes.byref(r))
    return {"id": h, "title": _title(h), "app": _app(h), "x": r.left, "y": r.top, "w": r.right - r.left,
            "h": r.bottom - r.top, "active": h == active, "minimized": bool(user32.IsIconic(h))}


def _cloaked(h):
    """Suspended Store apps and windows on other virtual desktops are 'visible' but cloaked."""
    c = ctypes.c_int(0)
    dwmapi.DwmGetWindowAttribute(wintypes.HWND(h), 14, ctypes.byref(c), ctypes.sizeof(c))
    return bool(c.value)


def windows():
    """Windows a person would call a window: visible, titled, not cloaked, not a tool window."""
    hits, active = [], user32.GetForegroundWindow()

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(h, _):
        if (user32.IsWindowVisible(h) and user32.GetWindowTextLengthW(h) and not _cloaked(h)
                and not user32.GetWindowLongW(h, -20) & 0x80 and _title(h) != "Program Manager"):
            hits.append(h)
        return True
    user32.EnumWindows(each, 0)
    return [_info(h, active) for h in hits]


def active_window():
    h = user32.GetForegroundWindow()
    return _info(h, h) if h else None


def focus(query):
    """Bring the best match to the front: exact app name, then title, then part of the app name."""
    q, ws = query.lower(), windows()
    hit = (next((w for w in ws if w["app"].lower() == q), None) or next((w for w in ws if q in w["title"].lower()), None)
           or next((w for w in ws if q in w["app"].lower()), None))
    if not hit:
        return None
    h = hit["id"]
    if hit["minimized"]:
        user32.ShowWindow(h, 9)                              # SW_RESTORE
    if not user32.SetForegroundWindow(h):
        # Windows only lets the app you last typed in change the foreground; borrowing its input queue gets round that
        mine, theirs = kernel32.GetCurrentThreadId(), user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), None)
        user32.AttachThreadInput(mine, theirs, True)
        user32.BringWindowToTop(h)
        user32.SetForegroundWindow(h)
        user32.AttachThreadInput(mine, theirs, False)
    return {**hit, "minimized": False, "active": user32.GetForegroundWindow() == h}


# ---------- global hotkeys ----------

def parse_hotkey(combo):
    """'win+shift+j' -> (RegisterHotKey modifier flags, virtual key)."""
    names = [k.strip().lower() for k in combo.split("+") if k.strip()]
    return sum(MODS[n] for n in names[:-1]), VK[names[-1]]


def listen_hotkeys(loop, actions):
    """{"win+j": fn, ...}: fn is called in the asyncio loop each time its hotkey is pressed, from any app."""
    combos, fns = list(actions), list(actions.values())

    def run():                     # hotkeys belong to the thread that registered them, which must pump messages
        for i, combo in enumerate(combos, 1):
            mods, vk = parse_hotkey(combo)
            if not user32.RegisterHotKey(None, i, mods | 0x4000, vk):           # 0x4000: no auto-repeat
                log.warning("couldn't register hotkey %s (Windows or another app already uses it)", combo)
        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == 0x0312:                                            # WM_HOTKEY
                loop.call_soon_threadsafe(fns[msg.wParam - 1])

    threading.Thread(target=run, daemon=True, name="hotkeys").start()
