"""One place that talks to classic Outlook, so every Outlook tool fails the same friendly way."""
import os
import subprocess
import time

NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
OUTLOOK_EXES = (
    r"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE",
    r"C:\Program Files (x86)\Microsoft Office\root\Office16\OUTLOOK.EXE",
)


class OutlookUnavailable(Exception):
    """Classic Outlook isn't installed, isn't signed in, or didn't answer in time."""


def _ps(script, timeout=90):
    p = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                       text=True, encoding="utf-8", errors="replace", capture_output=True,
                       timeout=timeout, creationflags=NO_WINDOW)
    out, err = (p.stdout or "").strip(), (p.stderr or "").strip()
    if not out and ("0x800706BE" in err or "RPC" in err or "Exception" in err or "error" in err.lower()):
        raise OutlookUnavailable(err.splitlines()[0] if err else "Outlook didn't respond.")
    if not out and err:
        raise OutlookUnavailable(err.splitlines()[0])
    return out


def ensure_running(wait=25):
    """Start classic Outlook if it isn't up; return True if this call started it."""
    probe = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OUTLOOK.EXE"], text=True,
                          capture_output=True, creationflags=NO_WINDOW)
    if "OUTLOOK.EXE" in (probe.stdout or "").upper():
        return False
    exe = next((p for p in OUTLOOK_EXES if os.path.exists(p)), "")
    if not exe:
        raise OutlookUnavailable("Classic Outlook isn't installed (the Microsoft Store app isn't supported).")
    subprocess.Popen([exe], creationflags=NO_WINDOW)
    time.sleep(wait)
    return True


def run(script, timeout=90):
    """Make sure Outlook is up, then run a PowerShell snippet against its COM API."""
    started = ensure_running()
    return _ps(script, timeout), started