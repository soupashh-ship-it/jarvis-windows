"""Jarvis desktop widget: a Dynamic Island style capsule, always on top, bottom centre of a monitor.

Resting, it's a small dark pill with a dim mic (a collapsed Dynamic Island). It springs open when you start talking
(wake word or hotkey), stays open while listening, thinking and speaking, and settles back into the pill a couple of
seconds after Jarvis finishes. Background-work changes ("+1 in the background") and "Jarvis isn't running" open it for
a few seconds too. One colour per state (design.COLORS), never mixed: listening, a mic that glows with your voice;
thinking, a softly turning orb and a shimmer across the label; speaking, the capsule's whole edge glows (iOS 18 Siri
style) as bright as Jarvis's voice; waiting for yes/no, an orange orb.
The text: what you're saying (live), then what he's saying, sentence by sentence. Look: design.py.
Windows can't blur behind a shape the way KDE does, so the capsule is a translucent dark fill instead.

Run: .venv\\Scripts\\pythonw widget.py   (install.ps1 starts it at logon, next to Jarvis)
"""
import http.client
import json
import math
import os
import sys
import threading
import time

from PySide6.QtCore import QObject, QPointF, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import (QColor, QConicalGradient, QFont, QFontMetricsF, QLinearGradient, QPainter, QPainterPath,
                           QPen, QPixmap, QRadialGradient, QRegion)
from PySide6.QtWidgets import QApplication, QWidget

import config
import design as d

TITLE = "Jarvis Widget"
CAP_W = 460                                  # expanded capsule width
CAP_MAX_H = 104
PAD_X, PAD_TOP, PAD_BOTTOM = 22, 20, 26       # room around the capsule for its shadow and edge glow
W, H = CAP_W + 2 * PAD_X, CAP_MAX_H + PAD_TOP + PAD_BOTTOM
PORT = 8765
TOKEN_FILE = os.path.join(config.JARVIS_DIR, "logs", "ctl.token")
ACTIVE = ("listening", "thinking", "speaking", "waiting")   # open while these last; a small pill otherwise
REST_W, REST_H = 112, 32                      # the resting pill (a collapsed Dynamic Island)
LINGER_S = 2.5                                # stays up this long after he finishes, then tucks away
NOTE_S = 4.0                                  # background-work / offline notes show this long

LABELS = {"idle": "Idle", "listening": "Listening", "thinking": "Thinking", "speaking": "Jarvis",
          "waiting": "Say yes or no", "offline": "Jarvis isn't running", "working": "Working"}
TOOL_NAMES = {"screenshot": "looking at the screen", "click_at": "clicking", "move_mouse": "moving the mouse",
              "scroll": "scrolling", "type_text": "typing", "press_keys": "pressing keys", "open_app": "opening an app",
              "focus_window": "switching windows", "list_windows": "checking windows", "media": "media controls",
              "volume": "volume", "run_command": "running a command", "write_file": "writing a file",
              "start_worker": "starting a worker", "annotate": "pointing on screen",
              "set_timer": "setting a timer", "list_timers": "checking timers", "cancel_timer": "cancelling a timer",
              "app_volume": "changing app volume", "find_file": "finding a file", "web_search": "searching the web",
              "organize_folder": "tidying files", "find_duplicates": "looking for duplicates", "biggest_files": "checking file sizes",
              "system_info": "checking the PC", "weather_now": "checking the weather", "clipboard": "using the clipboard",
              "ui_click": "clicking a button", "web_browse": "browsing the web",
              "remember": "remembering", "recall": "recalling", "read_screen_text": "reading the screen"}


def short_dur(sec):
    sec = int(max(0, sec))
    return f"{sec}s" if sec < 60 else f"{sec // 60}m {sec % 60:02d}s" if sec < 3600 else f"{sec // 3600}h {sec % 3600 // 60:02d}m"


def describe_step(ev):
    """'Command: Check the weather' rather than just 'running a command'."""
    short = ev["name"]
    name = TOOL_NAMES.get(short, short.replace("_", " "))
    try:
        inp = json.loads(ev.get("input") or "{}")
    except ValueError:
        inp = {}
    detail = inp.get("description") or inp.get("query") or inp.get("url") or inp.get("name") or ""
    if short == "write_file" and inp.get("path"):
        detail = os.path.basename(inp["path"])
    if short in ("screenshot", "click_at", "move_mouse", "scroll", "annotate"):
        detail = ""
    name = name[0].upper() + name[1:]
    return f"{name}: {detail}" if detail else name


class Feed(QObject):
    """Asks Jarvis what's happening several times a second (a tiny local request, about 2 ms)."""
    event = Signal(dict)

    token = ""

    def start(self):
        threading.Thread(target=self._run, daemon=True).start()

    def _get(self, path):
        c = http.client.HTTPConnection("127.0.0.1", PORT, timeout=3)
        c.request("GET", path, headers={"Host": f"127.0.0.1:{PORT}", "X-Jarvis-Token": self.token})
        r = c.getresponse()
        body = r.read()
        c.close()
        return r.status, body

    def _run(self):
        use_live = True
        while True:
            busy = False
            try:
                if use_live:
                    status, body = self._get("/api/live")
                    if status == 403:          # Jarvis made a new key (first start): read it again
                        with open(TOKEN_FILE) as f:
                            self.token = f.read().strip()
                        status, body = self._get("/api/live")
                    if status == 404:
                        use_live = False       # an older Jarvis without /api/live: use the full status instead
                        continue
                    view = json.loads(body)
                else:
                    status, body = self._get("/api/snapshot")
                    view = from_snapshot(json.loads(body))
                self.event.emit({"kind": "live", **view})
                busy = view["activity"] != "idle" or bool(view["tasks"])
            except Exception:
                self.event.emit({"kind": "live", "activity": "offline", "mic": 0, "out": 0, "tasks": [],
                                 "question": "", "you": "", "said": "", "step": "", "self_started": False})
                use_live = True
                time.sleep(2)
                continue
            time.sleep((1 / 15 if use_live else 0.7) if busy else 0.5)


def from_snapshot(snap):
    """Build the widget's view from the dashboard snapshot (fallback for an older running Jarvis)."""
    hist = snap.get("history", [])
    last_turn = max((i for i, e in enumerate(hist) if e["kind"] == "turn_start"), default=-1)
    said = step = ""
    for e in hist[last_turn + 1:]:
        if e["kind"] == "say":
            said = e["text"]
        elif e["kind"] == "tool_use" and not e.get("sub"):
            step = describe_step(e)
    heard = [e for e in hist if e["kind"] == "heard" and e.get("text")]
    turn = snap.get("turn") or {}
    return {"activity": snap.get("activity", "idle"), "mic": 0, "out": 0, "question": snap.get("question") or "",
            "tasks": snap.get("tasks", []), "you": (heard[-1]["text"] if heard else "") if snap.get("question")
            else turn.get("text") or (heard[-1]["text"] if heard else ""), "said": said, "step": step,
            "self_started": turn.get("source") == "worker"}


def place_window(w):
    """The monitor from config, inside the taskbar, with the capsule's outer edge WIDGET_MARGIN_* from the screen edge
    (the window is a little bigger than the capsule, for its shadow and edge glow)."""
    screens = sorted(QApplication.screens(), key=lambda s: s.geometry().x())
    s = {"left": screens[0], "right": screens[-1]}.get(config.WIDGET_SCREEN) or next(
        (x for x in screens if x.name() == config.WIDGET_SCREEN), screens[0])
    g, corner = s.availableGeometry(), config.WIDGET_CORNER
    x = (g.right() + 1 - W - config.WIDGET_MARGIN_X + PAD_X if corner.endswith("right")
         else g.left() + config.WIDGET_MARGIN_X - PAD_X if corner.endswith("left") else g.center().x() - W // 2)
    y = (g.top() + config.WIDGET_MARGIN_Y - PAD_TOP if corner.startswith("top")
         else g.bottom() + 1 - H - config.WIDGET_MARGIN_Y + PAD_BOTTOM)
    w.move(x, y)


def clamp_on_screen(x, y, margin=60):
    """Keep the window grabbable: at least `margin` px of it stays on some screen."""
    u = None
    for s in QApplication.screens():
        g = s.availableGeometry()
        u = g if u is None else u.united(g)
    if u is None:
        return x, y
    return (min(max(x, u.left() - W + margin), u.right() - margin),
            min(max(y, u.top() - H + margin), u.bottom() - margin))


def load_pos():
    """A dragged window position from an earlier run, or None (use the config corner)."""
    try:
        with open(config.STATE_FILE, encoding="utf-8") as f:
            st = json.load(f)
        x, y = st.get("widget_x"), st.get("widget_y")
        if isinstance(x, (int, float)) and isinstance(y, (int, float)):
            return int(x), int(y)
    except Exception:
        pass
    return None


def save_pos(x, y):
    try:
        try:
            with open(config.STATE_FILE, encoding="utf-8") as f:
                st = json.load(f)
        except Exception:
            st = {}
        st["widget_x"], st["widget_y"] = int(x), int(y)
        with open(config.STATE_FILE, "w", encoding="utf-8") as f:
            json.dump(st, f)
    except Exception:
        pass


class Widget(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(TITLE)
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
                            | Qt.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        self.setFixedSize(W, H)
        self.setCursor(Qt.OpenHandCursor)
        self._drag_off = None           # QPoint: grab offset while the capsule is being dragged
        self._mask_rect = None          # last capsule rect the click mask was built from
        self.activity = ""                # unknown until the first poll, so starting with Jarvis off still says so
        self.mic = self.out = 0.0         # smoothed levels
        self.mic_raw = self.out_raw = 0.0
        self.you = ""                     # what you said (live, then final)
        self.said = ""                    # sentence he's speaking now
        self.step = ""
        self.question = ""
        self.tasks = {}                   # background work: task_id -> {description, started, last_tool}
        self.self_started = False
        self.t0 = self.last_tick = time.time()
        self.activity_since = time.time()       # when the current state began (for the "· Xs" in the label)
        self.linger_until = 0.0           # stays up this long after he finishes
        self.note_until = 0.0             # a background-work or offline note shows until then
        self.task_ids = frozenset()
        self.font_label = d.font_mono(12)
        self.font_text = d.font(15)
        self.font_small = d.font(13)
        self.font_pill = d.font(14, QFont.Medium)
        self.cap_w, self.cap_h = d.Spring(CAP_W), d.Spring(64)
        self.presence = d.Spring(0)       # 0 = the small resting pill, 100 = fully open (springs both ways)
        self.content = None               # what's on show: (label, text, dim, footer, lines, compact)
        self.content_key = None
        self.content_t0 = 0.0             # when the current kind of content appeared (for its fade-in)
        self.last_key = None
        self.glass_key = self.glass_pm = None
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(16)
        self._mask_rect = self.capsule().toAlignedRect()
        self.setMask(QRegion(self._mask_rect))

    # ---------- events ----------

    def on_event(self, ev):
        if ev.get("kind") != "live":
            return
        if ev["activity"] != self.activity:
            if ev["activity"] == "offline":
                self.note_until = time.time() + NOTE_S
            self.activity = ev["activity"]
            self.activity_since = time.time()
        self.mic_raw, self.out_raw = ev.get("mic", 0), ev.get("out", 0)
        self.question = ev.get("question", "")
        self.you, self.said, self.step = ev.get("you", ""), ev.get("said", ""), ev.get("step", "")
        self.self_started = ev.get("self_started", False)
        self.tasks = {t.get("task_id", str(i)): t for i, t in enumerate(ev.get("tasks", []))}
        ids = frozenset(self.tasks)
        if ids != self.task_ids:
            self.task_ids = ids
            if ids:                        # a job started or finished and others are still going: say so briefly
                self.note_until = time.time() + NOTE_S

    def compose(self, now):
        """What to show: (label, text, dim, footer, task_lines, compact). Same wording as always."""
        a = self.activity
        label, text, dim, footer, lines = LABELS.get(a, a), "", False, "", None
        if self.tasks and a != "idle":
            elapsed = short_dur(now - min(t["started"] for t in self.tasks.values()))
            footer = f"Background work · {elapsed}"
        if a == "listening":
            label, text = "Listening", self.you or "..."
        elif a == "thinking":
            # mid-answer it says "Thinking"; once a tool actually starts, it reads "Working" so you can tell
            label = "Worker update" if self.self_started else ("Working" if self.step else "Thinking")
            if self.step:
                text = self.step
            else:
                text, dim = ("Checking on a background job" if self.self_started else self.you), True
        elif a == "speaking":
            text = self.said or "..."
        elif a == "waiting":
            text = self.question
        elif a == "idle" and self.tasks:
            label = "Working"
            lines = [(short_dur(now - t["started"]), t["description"] or "Background job")
                     for t in sorted(self.tasks.values(), key=lambda t: t["started"])[:3]]
        elif a == "idle" and self.said and now < self.linger_until:
            label, text, dim = "Jarvis", self.said, True
        elif a == "idle":
            text, dim = "Say “Hey Jarvis”", True
        elif a == "offline":
            text, dim = "Jarvis isn't running", True
        compact = a in ("idle", "offline") and not lines and label != "Jarvis"
        return label, text, dim, footer, lines, compact

    def tick(self):
        now = time.time()
        dt, self.last_tick = min(0.05, now - self.last_tick), now
        # attack fast, release slow, so the glow feels alive rather than jittery
        for name in ("mic", "out"):
            raw, cur = getattr(self, name + "_raw"), getattr(self, name)
            setattr(self, name, cur + (raw - cur) * (0.5 if raw > cur else 0.12))
        if self.activity in ACTIVE:
            self.linger_until = now + LINGER_S
        show = self.activity in ACTIVE or bool(self.tasks) or now < self.linger_until or now < self.note_until
        if self.activity == "listening" and not self.you:   # the silent window after wake/follow-up: stay as the small pill
            show = False
        self.presence.target = 100 if show else 0
        if show or self.content is None:   # while tucking away, keep showing what was there
            self.content = self.compose(now)
        key = (self.content[0], self.content[5])
        if key != self.content_key:
            self.content_key, self.content_t0 = key, now
        self.cap_w.target, self.cap_h.target = self.target_size(self.content)
        for spring in (self.cap_w, self.cap_h, self.presence):
            spring.step(dt)
        cap_rect = self.capsule().toAlignedRect()   # clicks land on the capsule only; around it stays click-through
        if cap_rect != self._mask_rect:
            self._mask_rect = cap_rect
            self.setMask(QRegion(cap_rect))
        if not show and not self.presence.moving:      # resting pill: still, so only redraw if it changes
            key = ("rest", self.activity == "offline", bool(self.tasks))
            if key != self.last_key:
                self.last_key = key
                self.update()
            self.timer.setInterval(100)
            return
        # Full 60 fps only while it morphs or follows a voice; the orb alone (thinking, waiting) repaints just its
        # own corner at 30 fps; a note that just sits there only repaints when its text changes (saves CPU).
        morphing = (self.cap_w.moving or self.cap_h.moving or self.presence.moving or now - self.content_t0 < 0.4)
        voice = self.activity in ("listening", "speaking")
        orb_only = not (morphing or voice) and (self.activity in ("thinking", "waiting") or bool(self.content[4]))
        key = (self.content, int(now // 30))
        if morphing or voice or key != self.last_key:
            self.last_key = key
            self.update()
        elif orb_only:
            cap = self.capsule()
            self.update(QRectF(cap.left() - 4, cap.top(), 66, min(cap.height(), 64)).toAlignedRect())
            self.update(QRectF(cap.right() - 60, cap.top(), 60, 34).toAlignedRect())   # the elapsed readout
            if self.activity == "thinking":                     # the shimmering label
                self.update(QRectF(cap.left(), cap.top(), cap.width(), 34).toAlignedRect())
        self.timer.setInterval(16 if morphing or voice else 33 if orb_only else 100)

    # ---------- dragging ----------

    def mousePressEvent(self, e):
        if e.button() == Qt.LeftButton and self.capsule().contains(e.position()):
            self._drag_off = e.globalPosition().toPoint() - self.pos()
            self.setCursor(Qt.ClosedHandCursor)
            e.accept()
        else:
            e.ignore()

    def mouseMoveEvent(self, e):
        if self._drag_off is None:
            return
        pos = e.globalPosition().toPoint() - self._drag_off
        self.move(*clamp_on_screen(pos.x(), pos.y()))
        e.accept()

    def mouseReleaseEvent(self, e):
        if e.button() == Qt.LeftButton and self._drag_off is not None:
            self._drag_off = None
            self.setCursor(Qt.OpenHandCursor)
            save_pos(self.x(), self.y())        # next start opens where you left it
            e.accept()

    # ---------- layout ----------

    def _state_name(self):
        """What colour/name this frame shows for itself: Working while it's doing something, else the activity."""
        if self.activity == "thinking" and self.step:
            return "working"
        if self.activity in ("idle", "offline") and self.tasks:
            return "working"
        return self.activity if self.activity in d.COLORS else "idle"

    def bare(self, content):
        """Speaking (and the moment after) has no icon: the capsule's glowing edge is the voice."""
        return self.activity == "speaking" or (self.activity == "idle" and content[0] == "Jarvis")

    def target_size(self, content):
        label, text, dim, footer, lines, compact = content
        if compact:
            fm = QFontMetricsF(self.font_pill)
            return min(CAP_W, 44 + fm.horizontalAdvance(text) + 20), 40
        body = (len(lines) * 20) if lines else self.text_height(text, self.bare(content))
        h = 14 + 16 + 4 + body + (20 if footer else 0) + 14
        return CAP_W, max(64, min(CAP_MAX_H, h))

    def text_height(self, text, bare):
        if not text:
            return 0
        fm = QFontMetricsF(self.font_text)
        r = fm.boundingRect(QRectF(0, 0, CAP_W - (44 if bare else 60), 1000), Qt.TextWordWrap, text)
        return min(r.height(), 3 * fm.lineSpacing())

    def capsule(self):
        pr = max(0.0, self.presence.value / 100)
        w = REST_W + (self.cap_w.value - REST_W) * pr       # opens out of the resting pill, and settles back into it
        h = REST_H + (self.cap_h.value - REST_H) * pr
        corner = config.WIDGET_CORNER
        x = W - PAD_X - w if corner.endswith("right") else PAD_X if corner.endswith("left") else (W - w) / 2
        y = PAD_TOP if corner.startswith("top") else H - PAD_BOTTOM - h
        return QRectF(x, y, w, h)

    # ---------- drawing ----------

    def paintEvent(self, _):
        if not self.content:
            return
        p = QPainter(self)
        p.setRenderHints(QPainter.Antialiasing | QPainter.TextAntialiasing)
        cap = self.capsule()
        path = QPainterPath()
        path.addRoundedRect(cap, 12, 12)
        p.fillPath(path, d.ISLAND)                   # flat dark slab, no sheen
        # a state-coloured underbar that pulses while he's alive
        now = time.time() - self.t0
        pulse = 0.55 + 0.45 * math.sin(now * (5.0 if self.activity == "listening" else 1.6))
        col = d.color(self._state_name())
        p.setPen(Qt.NoPen)
        p.setBrush(d.alpha(col, 0.55 + 0.35 * pulse if self.activity in ACTIVE else 0.5))
        p.drawRoundedRect(QRectF(cap.left() + 14, cap.bottom() - 4.5, cap.width() - 28, 2.6), 1.3, 1.3)
        if self.presence.value < 40:                         # resting (or nearly): just the equalizer
            self.draw_rest(p, cap, d.clamp01(1 - self.presence.value / 40))
        if self.presence.value < 60:                         # too small for words yet
            return
        p.save()
        p.setClipPath(path)
        p.setOpacity(p.opacity() * d.ease_out((time.time() - self.content_t0) / 0.28)
                     * d.clamp01((self.presence.value - 60) / 30))
        label, text, dim, footer, lines, compact = self.content
        if compact:
            self.draw_compact(p, cap, text)
        else:
            self.draw_expanded(p, cap, label, text, dim, footer, lines)
        p.restore()

    def glass(self, cap, path):
        """The capsule itself (shadow, translucent dark glass, sheen, hairline), redrawn only when its size changes."""
        key = (round(cap.x(), 1), round(cap.y(), 1), round(cap.width(), 1), round(cap.height(), 1))
        if key != self.glass_key:
            dpr = self.devicePixelRatioF()
            pm = QPixmap(round(W * dpr), round(H * dpr))
            pm.setDevicePixelRatio(dpr)
            pm.fill(Qt.transparent)
            q = QPainter(pm)
            q.setRenderHint(QPainter.Antialiasing)
            d.shadow(q, path)
            q.fillPath(path, d.ISLAND)
            sheen = QLinearGradient(cap.topLeft(), QPointF(cap.left(), cap.top() + min(40, cap.height())))
            sheen.setColorAt(0, QColor(255, 255, 255, 14))
            sheen.setColorAt(1, QColor(255, 255, 255, 0))
            q.fillPath(path, sheen)
            q.setPen(QPen(d.HAIRLINE, 1))
            q.drawPath(path)
            q.end()
            self.glass_key, self.glass_pm = key, pm
        return self.glass_pm

    def draw_edge_glow(self, p, cap, path):
        """iOS 18 Siri style: soft light running round the whole edge, as bright as Jarvis's voice is loud."""
        t = time.time() - self.t0
        c = d.color("speaking")
        g = QConicalGradient(cap.center(), -t * 110)      # one colour; two brighter arcs chase round the edge
        for pos, a in ((0, 1.0), (0.18, 0.25), (0.5, 0.85), (0.68, 0.2), (1, 1.0)):
            g.setColorAt(pos, d.alpha(c, a))
        level = 0.35 + 0.65 * self.out
        base = p.opacity()
        p.setBrush(Qt.NoBrush)
        for width, a in ((14, 0.12), (8, 0.25), (3.5, 0.55), (1.5, 0.95)):   # wide and faint to thin and bright
            p.setOpacity(base * a * level)
            p.setPen(QPen(g, width))
            p.drawPath(path)
        p.setOpacity(base)

    def draw_rest(self, p, cap, k):
        """The resting pill: a tiny equalizer in the state's colour, plus the background-work dot."""
        c = cap.center()
        if self.activity == "idle" and self.tasks:
            self.draw_sun(p, c, 8)
            return
        if self.activity == "listening":
            col = d.alpha(d.color("listening"), (0.35 + 0.55 * self.mic) * k)
        elif self.activity == "offline":
            col = d.alpha(d.LABEL, 0.25 * k)
        elif self.activity == "working":
            col = d.alpha(d.color("working"), 0.7 * k)
        else:
            col = d.alpha(d.LABEL, 0.5 * k)
        n, w, gap, t = 5, 3.0, 3.2, time.time() - self.t0
        level = self.mic if self.activity == "listening" else 0.25 + 0.2 * math.sin(t * 1.8)
        p.setPen(Qt.NoPen)
        p.setBrush(col)
        for i in range(n):
            phase = (t * 6 + i * 1.3) % math.tau
            h = 3 + 11 * max(0.12, level * (0.55 + 0.45 * math.sin(phase)))
            x = c.x() + (i - (n - 1) / 2) * (w + gap) - w / 2
            p.drawRoundedRect(QRectF(x, c.y() - h / 2, w, h), w / 2, w / 2)
        if self.tasks:
            p.setPen(Qt.NoPen)
            p.setBrush(d.alpha(d.color("working"), 0.55 * k))
            p.drawEllipse(QPointF(cap.right() - 16, c.y()), 2.5, 2.5)

    def draw_compact(self, p, cap, text):
        dot_c = d.color(self._state_name())
        p.setPen(Qt.NoPen)
        p.setBrush(dot_c)
        p.drawEllipse(QPointF(cap.left() + 18, cap.center().y()), 3.5, 3.5)
        p.setFont(self.font_pill)
        p.setPen(d.TERTIARY if self.activity == "offline" else d.SECONDARY)
        r = QRectF(cap.left() + 30, cap.top(), cap.width() - 44, cap.height())
        p.drawText(r, Qt.AlignLeft | Qt.AlignVCenter, p.fontMetrics().elidedText(text, Qt.ElideRight, int(r.width())))

    def draw_expanded(self, p, cap, label, text, dim, footer, lines):
        a, t = self.activity, time.time() - self.t0
        # a small glowing state dot lines up with the label; text starts after it
        bare = self.bare(self.content)
        if not bare:
            dot_c = d.color(self._state_name())
            pulse = 0.6 + 0.4 * math.sin((time.time() - self.t0) * (5.0 if self.activity == "listening" else 2.0))
            p.setPen(Qt.NoPen)
            p.setBrush(d.alpha(dot_c, 0.25 * (pulse if a in ACTIVE else 0.8)))
            p.drawEllipse(QPointF(cap.left() + 26, cap.top() + 22), 6.5, 6.5)   # soft halo
            p.setBrush(d.alpha(dot_c, 1.0))
            p.drawEllipse(QPointF(cap.left() + 26, cap.top() + 22), 3.6, 3.6)
        left = cap.left() + 40 if not bare else cap.left() + 18
        top, right = cap.top() + 14, cap.right() - 22
        p.setFont(self.font_label)
        label_rect = QRectF(left, top, right - left - 48, 16)
        label = label.upper()
        if a == "thinking":                                   # a soft highlight sweeping across the label
            x = label_rect.left() + ((t * 0.55) % 1.6 - 0.3) * 260
            g = QLinearGradient(x - 60, 0, x + 60, 0)
            base = d.alpha(d.LABEL, 0.55)
            g.setColorAt(0, base)
            g.setColorAt(0.5, d.LABEL)
            g.setColorAt(1, base)
            p.setPen(QPen(g, 1))
        elif a == "idle" and lines:
            p.setPen(d.color("working"))
        else:
            p.setPen(d.SECONDARY if a in ("speaking", "idle") else d.color(a))
        p.drawText(label_rect, Qt.AlignLeft | Qt.AlignVCenter,
                   p.fontMetrics().elidedText(label, Qt.ElideRight, int(label_rect.width())))
        if a in ACTIVE and self.activity_since:                 # elapsed in this state, HUD-style, right-aligned
            p.setFont(d.font_mono(10, QFont.Normal))
            p.setPen(d.alpha(d.TERTIARY, 1.0))
            p.drawText(QRectF(right - 44, top, 44, 16), Qt.AlignRight | Qt.AlignVCenter,
                       short_dur(time.time() - self.activity_since))
        body_top = top + 20
        bottom = cap.bottom() - 14 - (20 if footer else 0)
        if lines:
            p.setFont(self.font_small)
            fm = p.fontMetrics()
            for i, (dur, desc) in enumerate(lines):
                y = body_top + i * 20
                p.setPen(d.SECONDARY)
                p.drawText(QRectF(left, y, 56, 20), Qt.AlignLeft | Qt.AlignVCenter, dur)
                p.setPen(d.LABEL)
                p.drawText(QRectF(left + 60, y, right - left - 60, 20), Qt.AlignLeft | Qt.AlignVCenter,
                           fm.elidedText(desc, Qt.ElideRight, int(right - left - 60)))
        elif text:
            p.setFont(self.font_text)
            p.setPen(d.SECONDARY if dim else d.LABEL)
            box = QRectF(left, body_top, right - left, bottom - body_top)
            p.drawText(box, Qt.AlignLeft | Qt.AlignTop | Qt.TextWordWrap, self.fit(p, text, box, a == "waiting"))
        if footer:
            p.setFont(self.font_label)
            p.setBrush(d.color("working"))
            p.setPen(Qt.NoPen)
            p.drawEllipse(QPointF(left + 3, cap.bottom() - 22), 3, 3)
            p.setPen(d.alpha(d.color("working"), 0.75))
            p.drawText(QRectF(left + 12, cap.bottom() - 30, right - left, 16), Qt.AlignLeft | Qt.AlignVCenter, footer)

    def draw_mic(self, p, c):
        """SF Symbols style mic.fill on a disc; only the mic glows, pulsing with your voice."""
        t, level, blue = time.time() - self.t0, self.mic, d.color("listening")
        p.setPen(Qt.NoPen)
        halo_r = 17 + 16 * level
        halo = QRadialGradient(c, halo_r)
        halo.setColorAt(0, d.alpha(blue, 0.30 + 0.45 * level))
        halo.setColorAt(1, d.alpha(blue, 0))
        p.setBrush(halo)
        p.drawEllipse(c, halo_r, halo_r)
        phase = (t % 1.4) / 1.4                                   # a ripple leaving the disc while you talk
        if level > 0.05:
            p.setBrush(Qt.NoBrush)
            p.setPen(QPen(d.alpha(blue, 0.6 * level * (1 - phase)), 1.5))
            p.drawEllipse(c, 15 + 10 * phase, 15 + 10 * phase)
            p.setPen(Qt.NoPen)
        r = 15 * (1 + 0.06 * level)
        disc = QRadialGradient(c - QPointF(0, r * 0.5), r * 1.6)
        disc.setColorAt(0, blue.lighter(125))
        disc.setColorAt(1, blue.darker(115))
        p.setBrush(disc)
        p.drawEllipse(c, r, r)
        s = r / 15                                                # the glyph: capsule, cradle, stem, foot
        p.setBrush(d.on(blue))
        p.drawRoundedRect(QRectF(c.x() - 3.6 * s, c.y() - 8.2 * s, 7.2 * s, 11 * s), 3.6 * s, 3.6 * s)
        pen = QPen(d.on(blue), 1.7 * s, Qt.SolidLine, Qt.RoundCap)
        p.setPen(pen)
        p.setBrush(Qt.NoBrush)
        cradle = QPainterPath()
        cradle.moveTo(c.x() - 6 * s, c.y() - 1.5 * s)
        cradle.cubicTo(c.x() - 6 * s, c.y() + 6.5 * s, c.x() + 6 * s, c.y() + 6.5 * s, c.x() + 6 * s, c.y() - 1.5 * s)
        p.drawPath(cradle)
        p.drawLine(QPointF(c.x(), c.y() + 4.6 * s), QPointF(c.x(), c.y() + 8 * s))
        p.drawLine(QPointF(c.x() - 3.2 * s, c.y() + 8 * s), QPointF(c.x() + 3.2 * s, c.y() + 8 * s))

    def draw_sun(self, p, c, scale=12):
        """A warm, understated sun mark for ongoing background work."""
        orange = d.color("waiting")
        halo = QRadialGradient(c, scale * 1.8)
        halo.setColorAt(0, d.alpha(orange, 0.28))
        halo.setColorAt(1, d.alpha(orange, 0))
        p.setPen(Qt.NoPen)
        p.setBrush(halo)
        p.drawEllipse(c, scale * 1.8, scale * 1.8)
        p.setBrush(Qt.NoBrush)
        p.setPen(QPen(orange, max(1.5, scale * 0.14), Qt.SolidLine, Qt.RoundCap))
        p.drawEllipse(c, scale * 0.32, scale * 0.32)
        for i in range(8):
            angle = math.tau * i / 8
            inner, outer = scale * 0.58, scale * 0.82
            p.drawLine(QPointF(c.x() + math.cos(angle) * inner, c.y() + math.sin(angle) * inner),
                       QPointF(c.x() + math.cos(angle) * outer, c.y() + math.sin(angle) * outer))

    def draw_orb(self, p, c, r):
        """The orb: Jarvis's face. It swirls while thinking and breathes while waiting for an answer."""
        a, t = self.activity, time.time() - self.t0
        if a in ("thinking", "waiting"):
            r *= 1 + 0.05 * math.sin(t * 2.4)                 # breathing
        if a == "offline":
            p.setPen(QPen(d.alpha(d.GREY, 0.7), 1.5, Qt.DashLine))
            p.setBrush(Qt.NoBrush)
            p.drawEllipse(c, r, r)
            return
        lit = a != "idle" or bool(self.tasks)
        base = d.color("background" if a == "idle" and self.tasks else a)
        p.setPen(Qt.NoPen)
        if lit:                                               # a soft, quiet glow
            halo = QRadialGradient(c, r * 1.9)
            halo.setColorAt(0, d.alpha(base, 0.32))
            halo.setColorAt(1, d.alpha(base, 0))
            p.setBrush(halo)
            p.drawEllipse(c, r * 1.9, r * 1.9)
        if a == "thinking":                                   # a soft disc
            g = QRadialGradient(c, r)
            g.setColorAt(0, d.alpha(base, 0.95))
            g.setColorAt(1, d.alpha(base, 0.55))
        else:
            g = QRadialGradient(c - QPointF(r * 0.2, r * 0.3), r * 1.25)
            g.setColorAt(0, base.lighter(150) if lit else QColor(150, 150, 158))
            g.setColorAt(0.55, base if lit else QColor(96, 96, 104))
            g.setColorAt(1, base.darker(160) if lit else QColor(58, 58, 64))
        p.setBrush(g)
        p.drawEllipse(c, r, r)
        if a == "thinking":                                   # with a thin highlight circling it
            p.setPen(QPen(d.alpha(base, 0.75), 1.6, Qt.SolidLine, Qt.RoundCap))
            p.setBrush(Qt.NoBrush)
            p.drawArc(QRectF(c.x() - r - 4, c.y() - r - 4, 2 * r + 8, 2 * r + 8), int(-t * 220 * 16) % 5760, 80 * 16)
            return
        gloss = QRadialGradient(c - QPointF(0, r * 0.45), r * 0.8)  # a glassy highlight on top
        gloss.setColorAt(0, QColor(255, 255, 255, 38))
        gloss.setColorAt(1, QColor(255, 255, 255, 0))
        p.setBrush(gloss)
        p.drawEllipse(c, r, r)
        if a == "waiting":                                          # "?" in the orb: it's asking you
            p.setFont(d.font(14, QFont.DemiBold))
            p.setPen(QPen(d.on(base), 1.5))
            p.drawText(QRectF(c.x() - r, c.y() - r, 2 * r, 2 * r), Qt.AlignCenter, "?")
        if self.tasks and a == "idle":                        # background work: an amber dot circling the orb
            ang = t * 1.6
            p.setBrush(d.color("background"))
            p.drawEllipse(c + QPointF(math.cos(ang), math.sin(ang)) * (r + 6), 2.5, 2.5)

    @staticmethod
    def fit(p, text, box, keep_start=False):
        """Show the END of long text (the newest words), trimmed from the front; a yes/no question keeps its START
        (what it would do, and to what), trimmed from the end."""
        flags = Qt.AlignLeft | Qt.AlignTop | Qt.TextWordWrap
        if p.boundingRect(box, flags, text).height() <= box.height():
            return text
        words, shown = text.split(), text
        while len(words) > 1:
            words.pop(-1 if keep_start else 0)
            shown = " ".join(words) + " ..." if keep_start else "... " + " ".join(words)
            if p.boundingRect(box, flags, shown).height() <= box.height():
                break
        return shown


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("jarvis-widget")
    app.setDesktopFileName("jarvis-widget")
    w = Widget()
    place_window(w)
    saved = load_pos()                  # back where you dragged it, if that spot is still on a screen
    if saved is not None and any(s.geometry().contains(saved[0] + W // 2, saved[1] + H // 2)
                                 for s in QApplication.screens()):
        w.move(*clamp_on_screen(*saved))
    feed = Feed()
    feed.event.connect(w.on_event)
    w.show()
    feed.start()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
