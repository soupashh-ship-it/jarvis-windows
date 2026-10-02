"""Jarvis's "show me where" overlay: refined HUD rings, arrows, targeted brackets and callouts.

One see-through, always-on-top, click-through window per monitor that never takes focus. pctools.annotate starts
it with the shapes (already in desktop pixels) as JSON; it fades out smoothly after `duration` seconds, or is
ended cleanly by clear_annotations / a newer drawing. It cannot draw over exclusive-fullscreen games (borderless works).

Aesthetic & Motion Upgrade:
- Clean modern HUD styling inspired by Cloud Office / Claude screen guidance & Apple visionOS/iOS motion.
- Subtle dual-tone cyan-blue energy gradients, luminous depth glow layers, and crisp drop shadows.
- Precision corner brackets on bounding boxes and center reticles on target rings.
- Smooth cubic ease-out bezier arrow curves with directional chevron heads and travel ripples.
- Translucent glass pills with hairline gradient rims and balanced typography.

Try it (desktop pixels; one JSON argument):
    .venv\\Scripts\\python overlay.py "{\"duration\": 5, \"shapes\": [{\"type\": \"ring\", \"x\": 960, \"y\": 540, \"radius\": 60, \"label\": \"Target Action\\nClick here to proceed\", \"step\": 1}]}"
"""
import json
import math
import os
import signal
import sys
import time

# Shapes come in physical desktop pixels (what screenshots and clicks use), so Qt works in those too.
os.environ["QT_ENABLE_HIGHDPI_SCALING"] = "0"

from PySide6.QtCore import QPointF, QRectF, Qt, QTimer  # noqa: E402
from PySide6.QtGui import (QBrush, QColor, QFont, QFontMetricsF, QLinearGradient,  # noqa: E402
                           QPainter, QPainterPath, QPen, QRadialGradient)  # noqa: E402
from PySide6.QtWidgets import QApplication, QWidget  # noqa: E402

import design as d  # noqa: E402

STROKE = 2.6
BADGE_R = 10.5
GAP = 10.0
POINTER = 8.0
CORNER_LEN = 16.0

ACCENT = d.color("annotate")
ACCENT_CYAN = QColor(56, 189, 248)  # luminous cyan accent for HUD highlights and glows


def bezier(a, c, b, t):
    u = 1.0 - t
    return QPointF(u * u * a.x() + 2 * u * t * c.x() + t * t * b.x(),
                   u * u * a.y() + 2 * u * t * c.y() + t * t * b.y())


def bezier_tangent(a, c, b, t):
    u = 1.0 - t
    dx = 2 * u * (c.x() - a.x()) + 2 * t * (b.x() - c.x())
    dy = 2 * u * (c.y() - a.y()) + 2 * t * (b.y() - c.y())
    mag = math.hypot(dx, dy)
    if mag == 0:
        return 1.0, 0.0
    return dx / mag, dy / mag


class Shape:
    """One annotation element, laid out once in desktop pixels with dynamic HUD motion."""
    def __init__(self, s, i, fonts):
        self.kind = s.get("type", "ring")
        self.delay = i * d.STAGGER
        self.title, _, self.detail = str(s.get("label") or "").partition("\n")
        self.title, self.detail = self.title.strip(), self.detail.strip()
        self.step = str(s["step"]) if s.get("step") else ""
        self.fonts = fonts
        x, y = float(s["x"]), float(s["y"])

        if self.kind == "arrow":
            self.a = QPointF(float(s["from_x"]), float(s["from_y"]))
            self.b = QPointF(x, y)
            v = self.b - self.a
            dist = math.hypot(v.x(), v.y())
            bow = min(42.0, max(14.0, dist * 0.16))
            norm = QPointF(v.y() / (dist or 1), -v.x() / (dist or 1))
            self.c = (self.a + self.b) / 2 + norm * bow
            self.bounds = QRectF(self.a, self.b).normalized()
            anchor, below = QRectF(self.a, self.a), None
        elif self.kind == "rect":
            self.bounds = QRectF(x, y, float(s["w"]), float(s["h"]))
            anchor, below = self.bounds, True
        else:
            r = float(s.get("radius", 40))
            self.bounds = QRectF(x - r, y - r, 2 * r, 2 * r)
            anchor, below = self.bounds, True

        self.pill = self.layout_pill(anchor, below) if self.title else None
        self.badge = None
        if self.step and not self.pill:
            if self.kind == "arrow":
                self.badge = self.a
            elif self.kind == "rect":
                self.badge = self.bounds.topLeft() + QPointF(-4, -4)
            else:
                self.badge = self.bounds.center() + QPointF(-0.707, -0.707) * (self.bounds.width() / 2 + 4)

        self.extent = self.bounds.adjusted(-60, -60, 60, 60)
        if self.pill:
            self.extent = self.extent.united(self.pill[0].adjusted(-40, -40, 40, 50))
        if self.badge:
            self.extent = self.extent.united(QRectF(self.badge.x() - 25, self.badge.y() - 25, 50, 50))

    def layout_pill(self, anchor, below):
        tf, df, _ = self.fonts
        tw = tf.horizontalAdvance(self.title)
        dw = df.horizontalAdvance(self.detail) if self.detail else 0
        lead = 2 * BADGE_R + 10 if self.step else 14
        scr = QApplication.screenAt(anchor.center().toPoint()) or QApplication.primaryScreen()
        g = QRectF(scr.geometry()).adjusted(14, 14, -14, -14)
        w = min(g.width(), 26 + lead + max(tw, dw))                    # a too-long label is cut short, not off-screen
        h = 18 + tf.height() + (df.height() + 2 if self.detail else 0)
        box = QRectF(0, 0, max(w, 80), max(h, 2 * BADGE_R + 18))
        if below is None:
            box.moveCenter(anchor.center())
        else:
            room_below = anchor.bottom() + GAP + POINTER + box.height() <= g.bottom()
            room_above = anchor.top() - GAP - POINTER - box.height() >= g.top()
            below = room_below or not room_above
            box.moveCenter(QPointF(anchor.center().x(), 0))
            if below:
                box.moveTop(anchor.bottom() + GAP + POINTER)
            else:
                box.moveBottom(anchor.top() - GAP - POINTER)
        box.moveLeft(max(g.left(), min(box.left(), g.right() - box.width())))
        box.moveTop(max(g.top(), min(box.top(), g.bottom() - box.height())))

        path = QPainterPath()
        path.addRoundedRect(box, d.RADIUS_PILL, d.RADIUS_PILL)
        if below is not None:
            px = max(box.left() + 20, min(anchor.center().x(), box.right() - 20))
            edge, tip = (box.top(), box.top() - POINTER) if below else (box.bottom(), box.bottom() + POINTER)
            tri = QPainterPath()
            tri.moveTo(px - 9, edge)
            tri.quadTo(px - 2, tip + (1 if below else -1), px, tip)
            tri.quadTo(px + 2, tip + (1 if below else -1), px + 9, edge)
            tri.closeSubpath()
            path = path.united(tri)
        return box, path, (1 if below else -1) if below is not None else 0

    # ---------- drawing ----------

    def paint(self, p, t):
        dt = t - self.delay
        if dt <= 0:
            return
        op = d.clamp01(dt / d.FADE_IN)
        p.save()
        p.setBrush(Qt.NoBrush)
        if self.kind == "arrow":
            self.paint_arrow(p, t, dt, op)
        elif self.kind == "rect":
            self.paint_rect(p, t, dt, op)
        else:
            self.paint_ring(p, t, dt, op)
        if self.pill:
            self.paint_pill(p, t, dt)
        if self.badge:
            self.paint_badge(p, self.badge, d.ease_back(d.clamp01((dt - 0.08) / 0.3)), shadow=True)
        p.restore()

    def paint_rect(self, p, t, dt, op):
        scale = 0.90 + 0.10 * d.ease_back(d.clamp01(dt / 0.32))
        breath = 0.90 + 0.10 * math.sin(t * 3.2)
        b = self.bounds
        c = b.center()
        w, h = b.width() * scale, b.height() * scale
        r = QRectF(c.x() - w / 2, c.y() - h / 2, w, h)
        corner_r = min(12.0, min(w, h) / 4)
        path = QPainterPath()
        path.addRoundedRect(r, corner_r, corner_r)

        fill_grad = QLinearGradient(r.topLeft(), r.bottomLeft())
        fill_grad.setColorAt(0.0, d.alpha(ACCENT, 0.09 * op * breath))
        fill_grad.setColorAt(1.0, d.alpha(ACCENT, 0.03 * op * breath))
        p.fillPath(path, fill_grad)

        for gw, ga in ((16, 0.04), (10, 0.09), (5, 0.18)):
            p.setPen(QPen(d.alpha(ACCENT, ga * op * breath), gw))
            p.drawPath(path)

        p.save()
        p.translate(0, 1.2)
        p.setPen(QPen(d.alpha(QColor(0, 0, 0), 0.32 * op), STROKE + 1.8))
        p.drawPath(path)
        p.restore()

        p.setPen(QPen(d.alpha(ACCENT, op), STROKE))
        p.drawPath(path)

        clen = min(CORNER_LEN, min(w, h) * 0.35)
        if clen > 5:
            p.setPen(QPen(d.alpha(ACCENT_CYAN, 0.85 * op), STROKE + 0.8, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
            brackets = QPainterPath()
            brackets.moveTo(r.left(), r.top() + clen)
            brackets.lineTo(r.left(), r.top() + corner_r)
            brackets.quadTo(r.left(), r.top(), r.left() + corner_r, r.top())
            brackets.lineTo(r.left() + clen, r.top())
            brackets.moveTo(r.right() - clen, r.top())
            brackets.lineTo(r.right() - corner_r, r.top())
            brackets.quadTo(r.right(), r.top(), r.right(), r.top() + corner_r)
            brackets.lineTo(r.right(), r.top() + clen)
            brackets.moveTo(r.left(), r.bottom() - clen)
            brackets.lineTo(r.left(), r.bottom() - corner_r)
            brackets.quadTo(r.left(), r.bottom(), r.left() + corner_r, r.bottom())
            brackets.lineTo(r.left() + clen, r.bottom())
            brackets.moveTo(r.right() - clen, r.bottom())
            brackets.lineTo(r.right() - corner_r, r.bottom())
            brackets.quadTo(r.right(), r.bottom(), r.right(), r.bottom() - corner_r)
            brackets.lineTo(r.right(), r.bottom() - clen)
            p.drawPath(brackets)

        phase = (dt - 0.4) % 2.2 / 1.3
        if dt > 0.4 and phase < 1.0:
            grow = 20 * d.ease_out(phase)
            r_ripple = r.adjusted(-grow, -grow, grow, grow)
            p_rip = QPainterPath()
            p_rip.addRoundedRect(r_ripple, corner_r + grow * 0.5, corner_r + grow * 0.5)
            p.setPen(QPen(d.alpha(ACCENT, 0.45 * (1.0 - phase) * op), 1.8))
            p.drawPath(p_rip)

    def paint_ring(self, p, t, dt, op):
        scale = 0.88 + 0.12 * d.ease_back(d.clamp01(dt / 0.32))
        breath = 0.90 + 0.10 * math.sin(t * 3.5)
        c = self.bounds.center()
        base_r = (self.bounds.width() / 2) * scale

        p.setPen(Qt.NoPen)
        p.setBrush(d.alpha(ACCENT_CYAN, 0.85 * op * breath))
        p.drawEllipse(c, 2.5, 2.5)
        p.setPen(QPen(d.alpha(ACCENT, 0.4 * op), 1.0))
        p.setBrush(Qt.NoBrush)
        p.drawEllipse(c, 5.0, 5.0)

        grad = QRadialGradient(c, base_r)
        grad.setColorAt(0.0, d.alpha(ACCENT, 0.14 * op * breath))
        grad.setColorAt(0.7, d.alpha(ACCENT, 0.05 * op * breath))
        grad.setColorAt(1.0, d.alpha(ACCENT, 0.0))
        p.setPen(Qt.NoPen)
        p.setBrush(grad)
        p.drawEllipse(c, base_r, base_r)

        p_ring = QPainterPath()
        p_ring.addEllipse(c, base_r, base_r)
        for gw, ga in ((16, 0.04), (10, 0.09), (5, 0.18)):
            p.setPen(QPen(d.alpha(ACCENT, ga * op * breath), gw))
            p.drawPath(p_ring)

        p.save()
        p.translate(0, 1.2)
        p.setPen(QPen(d.alpha(QColor(0, 0, 0), 0.32 * op), STROKE + 1.8))
        p.drawPath(p_ring)
        p.restore()

        p.setPen(QPen(d.alpha(ACCENT, op), STROKE))
        p.drawPath(p_ring)

        p.setPen(QPen(d.alpha(ACCENT_CYAN, 0.75 * op), 1.6, Qt.SolidLine, Qt.RoundCap))
        p.drawLine(QPointF(c.x() + base_r + 2, c.y()), QPointF(c.x() + base_r + 7, c.y()))
        p.drawLine(QPointF(c.x() - base_r - 2, c.y()), QPointF(c.x() - base_r - 7, c.y()))
        p.drawLine(QPointF(c.x(), c.y() + base_r + 2), QPointF(c.x(), c.y() + base_r + 7))
        p.drawLine(QPointF(c.x(), c.y() - base_r - 2), QPointF(c.x(), c.y() - base_r - 7))

        phase = (dt - 0.4) % 2.0 / 1.2
        if dt > 0.4 and phase < 1.0:
            rip_r = base_r + 24 * d.ease_out(phase)
            p.setPen(QPen(d.alpha(ACCENT, 0.5 * (1.0 - phase) * op), 1.8))
            p.drawEllipse(c, rip_r, rip_r)

    def paint_arrow(self, p, t, dt, op):
        pr = d.ease_out(d.clamp01(dt / 0.38))
        steps = max(6, int(30 * pr))
        pts = [bezier(self.a, self.c, self.b, pr * i / steps) for i in range(steps + 1)]

        p.setBrush(d.alpha(ACCENT_CYAN, 0.9 * op))
        p.setPen(QPen(d.alpha(ACCENT, 0.5 * op), 1.2))
        p.drawEllipse(self.a, 3.5, 3.5)
        p.setPen(QPen(d.alpha(ACCENT, 0.35 * op), 1.0))
        p.setBrush(Qt.NoBrush)
        p.drawEllipse(self.a, 6.5, 6.5)

        path = QPainterPath(pts[0])
        for pt in pts[1:]:
            path.lineTo(pt)

        p.save()
        p.translate(0, 1.2)
        p.setPen(QPen(d.alpha(QColor(0, 0, 0), 0.32 * op), STROKE + 2.0, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.drawPath(path)
        p.restore()

        p.setPen(QPen(d.alpha(ACCENT, 0.22 * op), STROKE + 5.0, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.drawPath(path)

        p.setPen(QPen(d.alpha(ACCENT, op), STROKE, Qt.SolidLine, Qt.RoundCap, Qt.RoundJoin))
        p.drawPath(path)

        if pr >= 0.99:
            shim_phase = ((dt - 0.38) % 1.8) / 1.2
            if shim_phase < 1.0:
                s_pt = bezier(self.a, self.c, self.b, shim_phase)
                s_alpha = math.sin(shim_phase * math.pi)
                p.setPen(Qt.NoPen)
                p.setBrush(d.alpha(ACCENT_CYAN, 0.85 * s_alpha * op))
                p.drawEllipse(s_pt, 3.2, 3.2)

        if pr > 0.35:
            tip = pts[-1]
            tx, ty = bezier_tangent(self.a, self.c, self.b, pr)
            ang = math.atan2(ty, tx)
            head_len, notch = 13.0, 3.5
            left_wing = tip - QPointF(math.cos(ang - 0.45), math.sin(ang - 0.45)) * head_len
            right_wing = tip - QPointF(math.cos(ang + 0.45), math.sin(ang + 0.45)) * head_len
            notch_base = tip - QPointF(math.cos(ang), math.sin(ang)) * (head_len - notch)
            poly = QPainterPath()
            poly.moveTo(tip)
            poly.lineTo(left_wing)
            poly.lineTo(notch_base)
            poly.lineTo(right_wing)
            poly.closeSubpath()

            p.save()
            p.translate(0, 1.2)
            p.fillPath(poly, d.alpha(QColor(0, 0, 0), 0.35 * op))
            p.restore()
            p.fillPath(poly, d.alpha(ACCENT, 0.95 * op))
            pen_head = QPen(d.alpha(QColor(255, 255, 255), 0.75 * op), 1.2)
            pen_head.setCapStyle(Qt.RoundCap)
            pen_head.setJoinStyle(Qt.RoundJoin)
            p.setPen(pen_head)
            p.drawPath(poly)

        if dt > 0.4:
            target_phase = (dt - 0.4) % 2.0 / 1.0
            if target_phase < 1.0:
                tp_r = 4.0 + 12.0 * d.ease_out(target_phase)
                p.setPen(QPen(d.alpha(ACCENT_CYAN, 0.55 * (1.0 - target_phase) * op), 1.5))
                p.setBrush(Qt.NoBrush)
                p.drawEllipse(self.b, tp_r, tp_r)

    def paint_pill(self, p, t, dt):
        box, path, side = self.pill
        k = d.clamp01((dt - 0.06) / d.FADE_IN)
        if k <= 0:
            return
        p.save()
        p.setOpacity(p.opacity() * k)
        p.translate(0, -side * 7.0 * (1.0 - d.ease_out(k)))
        d.shadow(p, path, opacity=k * 0.95, blur=18, dy=6, peak=80)
        d.shadow(p, path, opacity=k * 0.25, blur=10, dy=2, peak=45)

        grad = QLinearGradient(box.topLeft(), box.bottomLeft())
        grad.setColorAt(0.0, QColor(26, 30, 40, 235))
        grad.setColorAt(1.0, QColor(13, 15, 20, 248))
        p.fillPath(path, grad)

        bgrad = QLinearGradient(box.topLeft(), box.bottomLeft())
        bgrad.setColorAt(0.0, QColor(255, 255, 255, 50))
        bgrad.setColorAt(0.4, QColor(255, 255, 255, 22))
        bgrad.setColorAt(1.0, QColor(255, 255, 255, 10))
        p.setPen(QPen(bgrad, 1.0))
        p.setBrush(Qt.NoBrush)
        p.drawPath(path)

        tf, df, fonts = self.fonts
        x = box.left() + 14
        if self.step:
            self.paint_badge(p, QPointF(x + BADGE_R, box.center().y()), 1.0)
            x += 2 * BADGE_R + 10
        else:
            dot_y = box.center().y()
            p.setPen(Qt.NoPen)
            p.setBrush(d.alpha(ACCENT_CYAN, 0.85))
            p.drawEllipse(QPointF(x + 4, dot_y), 3.0, 3.0)
            p.setPen(QPen(d.alpha(ACCENT, 0.4), 1.0))
            p.setBrush(Qt.NoBrush)
            p.drawEllipse(QPointF(x + 4, dot_y), 5.5, 5.5)
            x += 16

        text_h = tf.height() + (df.height() + 2 if self.detail else 0)
        y = box.center().y() - text_h / 2
        p.setFont(fonts[0])
        p.setPen(d.LABEL)
        room = box.right() - x - 8
        p.drawText(QRectF(x, y, box.right() - x - 8, tf.height()), Qt.AlignLeft | Qt.AlignVCenter,
                   tf.elidedText(self.title, Qt.ElideRight, room))
        if self.detail:
            p.setFont(fonts[1])
            p.setPen(d.SECONDARY)
            p.drawText(QRectF(x, y + tf.height() + 2, box.right() - x - 8, df.height()),
                       Qt.AlignLeft | Qt.AlignVCenter, df.elidedText(self.detail, Qt.ElideRight, room))
        p.restore()

    def paint_badge(self, p, at, scale, shadow=False):
        if scale <= 0:
            return
        r = BADGE_R * scale
        if shadow:
            ring = QPainterPath()
            ring.addEllipse(at, r + 2.0, r + 2.0)
            d.shadow(p, ring, blur=10, dy=2, peak=70)
            p.setPen(QPen(d.alpha(ACCENT_CYAN, 0.45), 1.5))
        else:
            p.setPen(QPen(d.alpha(ACCENT_CYAN, 0.35), 1.2))

        bgrad = QLinearGradient(at.x() - r, at.y() - r, at.x() + r, at.y() + r)
        bgrad.setColorAt(0.0, ACCENT_CYAN)
        bgrad.setColorAt(1.0, ACCENT)
        p.setBrush(bgrad)
        p.drawEllipse(at, r, r)
        p.setFont(self.fonts[2][2])
        p.setPen(QColor(255, 255, 255))
        p.drawText(QRectF(at.x() - r, at.y() - r, 2 * r, 2 * r), Qt.AlignCenter, self.step)


class Overlay(QWidget):
    def __init__(self, screen, shapes, clock):
        super().__init__()
        self.shapes, self.clock, self.origin = shapes, clock, screen.geometry().topLeft()
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
                            | Qt.WindowTransparentForInput | Qt.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        self.setScreen(screen)
        self.setGeometry(screen.geometry())               # whole monitor, over the taskbar too
        mine = QRectF(screen.geometry())
        self.dirty = [s.extent.translated(-self.origin.x(), -self.origin.y()).toAlignedRect()
                      for s in shapes if s.extent.intersects(mine)]

    def animate(self):
        for r in self.dirty:                       # repaint only around the shapes, not the whole monitor
            self.update(r)

    def paintEvent(self, _):
        t, fade = self.clock()
        p = QPainter(self)
        p.setRenderHints(QPainter.Antialiasing | QPainter.TextAntialiasing)
        p.translate(-self.origin.x(), -self.origin.y())       # shapes are in desktop pixels
        p.setOpacity(fade)
        for s in self.shapes:
            s.paint(p, t)


def main():
    if len(sys.argv) < 2:
        return
    spec = json.loads(sys.argv[1])
    app = QApplication(sys.argv[:1])
    app.setApplicationName("jarvis-overlay")
    fonts = (d.font(14, QFont.DemiBold), d.font(12), d.font(11, QFont.Bold))
    metrics = (QFontMetricsF(fonts[0]), QFontMetricsF(fonts[1]), fonts)
    shapes = [Shape(s, i, metrics) for i, s in enumerate(spec["shapes"])]
    start = time.monotonic()
    state = {"out": max(0.5, float(spec.get("duration", 8))) - d.FADE_OUT}

    def clock():
        t = time.monotonic() - start
        return t, 1 - d.ease_out((t - state["out"]) / d.FADE_OUT)

    def fade_now(*_):
        state["out"] = min(state["out"], time.monotonic() - start)

    signal.signal(signal.SIGTERM, fade_now)       # where the OS sends SIGTERM: fade out rather than vanish
    windows = [Overlay(s, shapes, clock) for s in app.screens()]
    for w in windows:
        w.show()

    def tick():
        t = clock()[0]
        if t > state["out"] + d.FADE_OUT + 0.05:
            app.quit()
        for w in windows:
            w.animate()

    timer = QTimer()
    timer.timeout.connect(tick)
    timer.start(16)
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
