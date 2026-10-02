import json, urllib.request, time
BASE = "http://127.0.0.1:8069/v1"
for model in ["gemini-3-flash", "gemini-2.5-flash", "claude-sonnet-4-5"]:
    body = {"model": model, "messages": [{"role": "user", "content": "Reply with exactly: PONG"}]}
    req = urllib.request.Request(BASE + "/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json"})
    t = time.time()
    try:
        r = urllib.request.urlopen(req, timeout=30).read().decode()
        d = json.loads(r)
        print(f"{model} {time.time()-t:.1f}s OK:", d["choices"][0]["message"]["content"][:80].replace("\n", " "))
    except Exception as e:
        print(f"{model} {time.time()-t:.1f}s FAIL:", str(e)[:150])
