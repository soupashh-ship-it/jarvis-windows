"""The dashboard web server: http://127.0.0.1:8765 (this PC only).

Everything needs the token from logs/ctl.token: another Windows account on the same PC can reach 127.0.0.1 too.
`python jarvisctl.py dashboard` opens /?token=..., which swaps it for a cookie; jarvisctl and the widget send it
as an X-Jarvis-Token header. It survives restarts (delete logs/ctl.token to change it).

GET  /?token=...       the page
GET  /api/snapshot     everything about right now, plus recent history
GET  /api/events       live stream (server-sent events)
POST /api/cmd          listen / stop / say / yes / no
"""
import asyncio
import json
import os
import secrets

import psutil
from aiohttp import web

import config
import events
import pctools

PORT = 8765
SHOTS_DIR = os.path.join(config.JARVIS_DIR, "logs", "shots")
PAGE = os.path.join(config.JARVIS_DIR, "dashboard.html")
TOKEN_FILE = os.path.join(config.JARVIS_DIR, "logs", "ctl.token")
ALLOWED_HOSTS = {f"127.0.0.1:{PORT}", f"localhost:{PORT}"}
NO_TOKEN = ("Open the dashboard from the Jarvis folder with:  .venv\\Scripts\\python jarvisctl.py dashboard\n"
            "(It needs the key in logs\\ctl.token, so other people on this PC can't use it.)")


def _token():
    try:
        with open(TOKEN_FILE) as f:
            old = f.read().strip()
        if len(old) >= 32:
            return old               # the same key across restarts, so an open dashboard keeps working
    except OSError:
        pass
    return secrets.token_urlsafe(32)


TOKEN = _token()


# ---------- processes: Jarvis and what it started ----------

_seen = {}                                       # pid -> Process, so cpu_percent has a previous reading


def _describe(cmd):
    """Turn a raw command line into something a person can read."""
    if any(c.endswith("jarvis.py") for c in cmd):
        return "Jarvis (ears, voice, brain, dashboard)", "core"
    if "-Command" in cmd:
        return cmd[cmd.index("-Command") + 1].replace(pctools.PS_UTF8, "")[:300], "job"
    return " ".join(cmd)[:300], "other"


def processes():
    me, out = psutil.Process(), []
    for p in [me] + me.children(recursive=True):
        p = _seen.setdefault(p.pid, p)
        try:
            with p.oneshot():
                cmd, cpu, mem, started = p.cmdline(), p.cpu_percent(), p.memory_info().rss / 1e6, p.create_time()
        except psutil.Error:
            _seen.pop(p.pid, None)
            continue
        label, role = _describe(cmd)
        out.append({"pid": p.pid, "label": label, "role": role, "started": started, "cpu": round(cpu, 1),
                    "mem_mb": round(mem), "ends": None})
    return out


# ---------- web ----------

def _check(request):
    if request.host not in ALLOWED_HOSTS:
        raise web.HTTPForbidden(text="bad host")
    key = request.headers.get("X-Jarvis-Token") or request.cookies.get("jarvis") or ""
    if not secrets.compare_digest(key, TOKEN):
        raise web.HTTPForbidden(text=NO_TOKEN)


async def page(request):
    if request.host in ALLOWED_HOSTS and secrets.compare_digest(request.query.get("token", ""), TOKEN):
        resp = web.Response(status=302, headers={"Location": "/"})   # the key moves into a cookie, out of the address bar
        resp.set_cookie("jarvis", TOKEN, httponly=True, samesite="Strict", max_age=400 * 86400)
        return resp
    _check(request)
    with open(PAGE, encoding="utf-8") as f:
        html = f.read()
    return web.Response(text=html, content_type="text/html",
                        headers={"Cache-Control": "no-store", "X-Frame-Options": "DENY"})


async def snapshot(request):
    _check(request)
    j = request.app["jarvis"]
    if request.query.get("lite"):
        return web.json_response(j.snapshot(), dumps=lambda o: json.dumps(o, default=str))
    return web.json_response(j.snapshot() | {"history": list(events.history)[-1500:], "procs": processes()},
                             dumps=lambda o: json.dumps(o, default=str))


async def stream(request):
    _check(request)
    resp = web.StreamResponse(headers={"Content-Type": "text/event-stream", "Cache-Control": "no-store"})
    await resp.prepare(request)
    q = events.subscribe()
    try:
        while events.subscribed(q):              # dropped for falling behind: end it, the page reconnects and reloads
            try:
                ev = await asyncio.wait_for(q.get(), 15)
                await resp.write(f"data: {json.dumps(ev, default=str)}\n\n".encode())
            except asyncio.TimeoutError:
                await resp.write(b": keepalive\n\n")
    except (ConnectionResetError, asyncio.CancelledError):
        pass
    finally:
        events.unsubscribe(q)
    return resp


async def live(request):
    _check(request)
    return web.json_response(request.app["jarvis"].live_view())


async def command(request):
    _check(request)
    body = await request.json()
    reply = await request.app["jarvis"].command(body.get("cmd"), body.get("text", ""))
    return web.json_response({"reply": reply})


async def shot(request):
    _check(request)
    name = os.path.basename(request.match_info["name"])
    path = os.path.join(SHOTS_DIR, name)
    if not os.path.exists(path):
        raise web.HTTPNotFound()
    return web.FileResponse(path)


async def ticker(jarvis):
    """Live-only updates, only while a dashboard tab is open."""
    n = 0
    while True:
        await asyncio.sleep(0.25)
        if not events.watchers():
            continue
        events.emit("meter", **jarvis.meter())
        n += 1
        if n % 8 == 0:
            events.emit("procs", procs=processes())


async def start(jarvis):
    os.makedirs(SHOTS_DIR, exist_ok=True)
    with open(TOKEN_FILE, "w") as f:
        f.write(TOKEN)
    # ponytail: logs/ctl.token is only as private as the Jarvis folder; on a shared PC keep that folder in your user folder
    app = web.Application()
    app["jarvis"] = jarvis
    app.router.add_get("/", page)
    app.router.add_get("/api/snapshot", snapshot)
    app.router.add_get("/api/events", stream)
    app.router.add_get("/api/live", live)
    app.router.add_post("/api/cmd", command)
    app.router.add_get("/shots/{name}", shot)
    runner = web.AppRunner(app, access_log=None)
    await runner.setup()
    await web.TCPSite(runner, "127.0.0.1", PORT).start()
    asyncio.create_task(ticker(jarvis))
    return runner
