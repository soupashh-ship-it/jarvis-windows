"""Workers: background jobs Jarvis hands off. Each is its own conversation with the model, with the same desktop
tools, running in this process. Risky steps are asked out loud like Jarvis's own; when a worker finishes or
needs you, Jarvis is told and says so."""
import asyncio
import logging
import time

import brain
import config
import events
import pctools
from pctools import text, tool

log = logging.getLogger("worker")

confirm = None      # async (question) -> bool, asks out loud; set by jarvis.py
notify = None       # (message) -> None, hands a message to Jarvis's session; set by jarvis.py
workers = {}        # name -> Worker
LIVE = ("starting", "running", "waiting")
NAMES = ("start_worker", "list_workers", "message_worker", "stop_worker")

BRIEF = """# You are a Jarvis worker
JARVIS, {name}'s voice assistant, started you to do one job in the background on their Windows PC (home folder {home}). Nobody is watching this conversation.
- Work on your own with your tools. Risky steps are put to {name} out loud for you; if the answer is no, find another way or stop.
- Finish with one or two plain sentences: what you did and where the result is. That line is read out to {name}.
- If you need a decision or information, end with a line starting "NEED USER:" and the question.
- Clicks are not checked with {name}. Before clicking anything that sends, posts, submits, buys, books or deletes, stop and end with "NEED USER:" asking whether to, unless {name} has already said yes to exactly that.
- Only one of you (Jarvis or a worker) can use the mouse and keyboard at a time. If a tool says they are in use, do other parts of the job or wait; don't fight over the screen.
"""


def outcome(result, failed):
    return "failed" if failed else "waiting" if "NEED USER:" in (result or "") else "done"


class Worker:
    def __init__(self, name, task):
        self.name, self.state, self.last, self.started = name, "starting", "", time.time()
        tools = [f for n, f in pctools.TOOLS.items() if n not in NAMES]
        self.agent = brain.Agent(BRIEF.format(name=config.USER_NAME, home=config.HOME), self.permit, tools,
                                 model=config.WORKER_MODEL or None, sub=True, name=name)
        self.runner = asyncio.create_task(self.run(task))

    def set(self, state, **extra):
        self.state = state
        events.emit("worker", name=self.name, state=state, text=self.last[-500:], **extra)

    def tell(self):
        notify(f"[worker update, not from {config.USER_NAME}] The {self.name} worker is {self.state}. "
               f"Its last message:\n{self.last[-1500:]}\n"
               f"Tell {config.USER_NAME} in one short sentence; if it needs them, ask its question.")

    async def permit(self, tool_name, data):
        if brain.policy(tool_name, data) == "allow":
            return True
        self.set("waiting", asking=True)
        ok = await confirm(f"{config.HONORIFIC.capitalize()}, the {self.name} worker would like to "
                           f"{brain.describe(tool_name, data)}. Shall I allow it?")
        self.set("running")
        return ok

    async def run(self, message):
        self.set("running")
        try:
            self.last = await self.agent.run(message) or self.last
            self.set(outcome(self.last, False))
        except asyncio.CancelledError:
            raise                                  # stopped on purpose
        except Exception as e:
            log.exception("worker %s died", self.name)
            self.last = f"It crashed: {e}"
            self.set("failed")
        finally:
            await self.agent.close()
        self.tell()


def find(name):
    return workers.get(name.strip().lower())


def names():
    return ", ".join(workers) or "none"


def as_tasks():
    """Live workers, shaped like background tasks for the dashboard and widget."""
    return [{"task_id": "worker:" + w.name, "description": f"{w.name} ({w.state})", "type": "Worker",
             "started": w.started} for w in workers.values() if w.state in LIVE]


async def stop(w):
    w.runner.cancel()
    await asyncio.wait({w.runner}, timeout=15)
    w.set("stopped")


async def stop_all():
    await asyncio.gather(*(stop(w) for w in workers.values() if not w.runner.done()))


@tool("start_worker", "Start a background worker for a job that will take more than a minute or so, or that should "
      "run in parallel. name: short spoken name, e.g. 'report'. task: a complete, self-contained brief "
      "(the worker cannot see this conversation).", {"name": str, "task": str})
async def start_worker(args):
    name = args["name"].strip().lower()
    old = workers.get(name)
    if old and not old.runner.done():
        return text(f"A worker called {name} is already {old.state}. Message it, stop it, or pick another name.")
    workers[name] = Worker(name, args["task"])
    return text(f"Started worker {name}. You'll be told when it finishes or needs the user.")


@tool("list_workers", "List workers: name, state (starting, running, waiting, done, failed, stopped), minutes "
      "since start, last message.", {})
async def list_workers(args):
    return text("\n".join(f"{w.name}: {w.state}, {round((time.time() - w.started) / 60)} min, "
                          f"last said: {w.last[-300:]}" for w in workers.values()) or "No workers.")


@tool("message_worker", "Send a follow-up instruction, or the user's answer to its question, to a worker that has "
      "finished its last step (done, waiting or failed).", {"name": str, "text": str})
async def message_worker(args):
    w = find(args["name"])
    if not w:
        return text(f"No worker by that name. Workers: {names()}.")
    if not w.runner.done():
        return text(f"{w.name} is still working; message it when it reports back, or stop it.")
    w.runner = asyncio.create_task(w.run(args["text"]))
    return text(f"Sent to {w.name}.")


@tool("stop_worker", "Stop a worker by name.", {"name": str})
async def stop_worker(args):
    w = find(args["name"])
    if not w:
        return text(f"No worker by that name. Workers: {names()}.")
    await stop(w)
    return text(f"Stopped {w.name}.")
