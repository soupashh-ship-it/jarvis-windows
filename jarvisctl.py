"""Talk to a running Jarvis without your voice.

  python jarvisctl.py listen            same as saying "hey jarvis"
  python jarvisctl.py stop              shut up / cancel what you're doing
  python jarvisctl.py say open spotify  type a command instead of speaking it
  python jarvisctl.py yes | no          answer a confirmation question
  python jarvisctl.py speak hello       just say something out loud (voice test)
  python jarvisctl.py status
"""
import json
import os
import sys
import urllib.request

TOKEN_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", "ctl.token")

if len(sys.argv) < 2 or sys.argv[1] in ("-h", "--help"):
    sys.exit(__doc__.strip())
cmd, rest = sys.argv[1], " ".join(sys.argv[2:])
if cmd in ("say", "speak") and not rest:
    sys.exit(f"jarvisctl {cmd} needs some text")
try:
    with open(TOKEN_FILE) as f:
        token = f.read().strip()
    req = urllib.request.Request("http://127.0.0.1:8765/api/cmd", json.dumps({"cmd": cmd, "text": rest}).encode(),
                                 {"Content-Type": "application/json", "X-Jarvis-Token": token})
    with urllib.request.urlopen(req, timeout=10) as r:
        print(json.load(r)["reply"])
except OSError:
    sys.exit("Jarvis isn't running.")
