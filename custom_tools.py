"""Jarvis writes new tools here so it can do things it couldn't before.

Rules for code Jarvis writes (and for you):
- Each tool is an async function decorated with @tool using pctools helpers.
- Append new tools; do not delete or rename ones it wrote last time.
- Restart Jarvis after editing.

Example:

from pctools import tool, text

@tool("example", "Say hello.", {})
async def example(args):
    return text("Hello from a custom tool.")
"""
