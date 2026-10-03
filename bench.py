"""Measure what actually matters: how fast Jarvis hears, thinks and speaks, and how fast the tools answer.

Run: .venv\\Scripts\\python bench.py [n]
"""
import asyncio
import statistics
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0] or ".")

import brain
import config
import pctools

ROUNDS = int(sys.argv[1]) if len(sys.argv) > 1 else 3


async def bench_model():
    """Latency of the real provider: one short turn, end to end."""
    times = []
    for i in range(ROUNDS):
        a = brain.Agent("bench", lambda n, d: asyncio.sleep(0, "allow"), list(pctools.TOOLS.values()))
        t = time.time()
        try:
            await a.run("Reply with exactly: PONG")
            times.append(time.time() - t)
            print(f"  model turn {i + 1}: {times[-1]:.1f}s")
        except Exception as e:
            print(f"  model turn {i + 1}: FAILED after {time.time() - t:.1f}s: {str(e)[:120]}")
        finally:
            await a.close()
    return times


async def bench_tools():
    async def timed(name, tool, args):
        t = time.time()
        try:
            await tool(args)
            return time.time() - t
        except Exception as e:
            print(f"  {name}: FAILED {str(e)[:100]}")
            return None
    out = {}
    out["current_date"] = await timed("current_date", pctools.current_date, {})
    out["system_info"] = await timed("system_info", pctools.system_info, {})
    out["web_search"] = await timed("web_search", pctools.TOOLS["web_search"], {"query": "weather today"})
    out["screenshot"] = await timed("screenshot", pctools.TOOLS["screenshot"], {})
    return out


def report(label, values):
    ok = [v for v in values if v is not None]
    if not ok:
        print(f"{label}: nothing succeeded")
        return
    print(f"{label}: min {min(ok):.2f}s  median {statistics.median(ok):.2f}s  max {max(ok):.2f}s")


async def main():
    print(f"provider: {config.LLM_PROVIDER}  model: {config.LLM_MODEL}")
    print("model latency:")
    report("  model", await bench_model())
    print("tool latency:")
    tools = await bench_tools()
    for name, dt in tools.items():
        if dt is not None:
            print(f"  {name}: {dt:.2f}s")


asyncio.run(main())