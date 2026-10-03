"""Jarvis writes new tools here so it can do things it couldn't before.

Rules for code Jarvis writes (and for you):
- Each tool is an async function decorated with @tool using pctools helpers.
- Append new tools; don't delete or rename ones from earlier.
- After writing, call reload_tools (or list_custom_tools): no restart needed.
- Jarvis runs what it writes here with full access, so keep it short and readable.

Example:

from pctools import tool, text

@tool("example", "Say hello.", {})
async def example(args):
    return text("Hello from a custom tool.")
"""