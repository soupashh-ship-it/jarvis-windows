"""Nightly health check: runs the test suite and writes a short report to logs/nightly.log.

Registered as the scheduled task "Jarvis Nightly Check" (see install.ps1 and the README).
"""
import datetime
import subprocess
import sys
import os

HERE = os.path.dirname(os.path.abspath(__file__))
LOGS = os.path.join(HERE, "logs")
SCRIPTS = ("test_brain.py", "test_time_tag.py", "test_tools.py")


def main():
    os.makedirs(LOGS, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%a %d %b %H:%M")
    lines = [f"\n===== nightly check {stamp} ====="]
    failed = []
    for script in SCRIPTS:
        try:
            r = subprocess.run([sys.executable, os.path.join(HERE, script)], cwd=HERE, capture_output=True,
                               text=True, encoding="utf-8", errors="replace", timeout=600,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            ok = r.returncode == 0
        except Exception as e:
            ok, r = False, None
            lines.append(f"{script}: ERROR {e}")
        lines.append(f"{script}: {'PASS' if ok else 'FAIL'}")
        if not ok:
            failed.append(script)
            if r is not None:
                tail = (r.stdout or "").strip().splitlines()[-6:] + (r.stderr or "").strip().splitlines()[-4:]
                lines += ["  " + t for t in tail]
    lines.append("RESULT: " + ("all green" if not failed else "FAILURES: " + ", ".join(failed)))
    report = "\n".join(lines)
    print(report)
    with open(os.path.join(LOGS, "nightly.log"), "a", encoding="utf-8") as f:
        f.write(report + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())