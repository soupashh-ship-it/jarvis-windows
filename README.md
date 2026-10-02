# Jarvis for Windows

A voice assistant for your Windows 10/11 PC that talks like J.A.R.V.I.S. from the Iron Man films. Say **"Hey Jarvis"**, wait for the chime, talk. It can open apps, switch windows, look at the screen, click, scroll, type, control media and volume, run PowerShell, write files, and hand longer jobs to background workers.

It is model agnostic. The brain is any chat model that supports tool calling behind an OpenAI-compatible endpoint: Ollama, LM Studio, OpenAI, OpenRouter, Groq, Gemini, Anthropic and others. By default everything runs locally: wake word (openWakeWord), speech to text (faster-whisper), the model (Ollama) and the voice (Kokoro). Nothing needs the cloud unless you point it there.

This is a Windows port of a Linux (KDE Plasma) Jarvis. The Linux original drives Claude Code; this one has its own small agent loop instead, so no Claude Code or any other CLI is needed.

How it fits together:

- `ears.py`: microphone, "Hey Jarvis" wake word, end-of-speech detection, speech to text (local faster-whisper, or any OpenAI-compatible transcription endpoint).
- `brain.py`: talks to the model (one streamed `/chat/completions` call per step), runs its tool calls, the persona, the notes between sessions, and the safety gate that asks you out loud before risky actions.
- `mouth.py`: text to speech (Kokoro by default, a Piper voice, or an OpenAI-compatible speech endpoint), cut off the moment you talk over it.
- `pctools.py` + `winapi.py`: the tools the model can call. `winapi.py` is the Windows part (windows, mouse, keyboard, hotkeys) through ctypes.
- `worker.py`: background workers, each its own conversation with the same tools.
- `jarvis.py`: the main loop. `dashboard.py` + `dashboard.html`: a local web dashboard. `widget.py`: a small always-on-top status panel. `events.py`: the activity log. `jarvisctl.py`: control it from a terminal.
- `persona.md`: the system prompt, a template filled in from your config. `lines.md`: real JARVIS lines by situation, used to tune the voice.

## Requirements

- Windows 10 or 11, 64-bit.
- **Python 3.12** from python.org (keep the "py launcher" option ticked). Other versions may work; 3.12 is what the pinned packages are chosen for.
- A model to talk to (see [Choose a model](#choose-a-model)). For the fully local default, install [Ollama](https://ollama.com) and run `ollama pull qwen3-vl:8b` (needs a decent GPU, or patience).
- A microphone. Headphones are recommended if you want to interrupt it by talking over it.
- About 2 GB of disk for the voice, wake word and whisper models.

## Install

```
git clone https://github.com/NickBhai-GH/jarvis-windows.git
cd jarvis-windows
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` does four things, and is safe to run again:

1. creates `.venv` with Python 3.12 and installs `requirements.txt`,
2. downloads the Kokoro voice into `models\` and the "Hey Jarvis" wake word model,
3. registers a Task Scheduler task called **Jarvis** for your user (no admin needed) that starts Jarvis and its widget at every logon, windowless, and restarts them if they crash,
4. starts it.

The whisper models download by themselves on the first start, so the first start takes a minute or two.

### Microphone

- Settings > Privacy & security > Microphone: turn on **Microphone access** and **Let desktop apps access your microphone**.
- Settings > System > Sound > Input: pick the mic you want as the default. Jarvis uses the default input unless you set `MIC_DEVICE`.
- To see every input with its number: `.venv\Scripts\python -m sounddevice`, then set `MIC_DEVICE = 3` (or part of its name) in `config_local.py`.
- The dashboard's mic meter shows whether it hears you and how close you get to the wake threshold.

## Choose a model

Put your settings in `config_local.py` in the repo folder (gitignored; it overrides `config.py`). The model needs **tool calling**; for screenshots it also needs **vision** (otherwise set `LLM_VISION = False`). The API key can also come from the `JARVIS_LLM_API_KEY` environment variable instead of the file.

Ollama, local (the default, no key):

```
LLM_BASE_URL = "http://localhost:11434/v1"
LLM_MODEL = "qwen3-vl:8b"          # any Ollama model tagged "tools" (and "vision" for screenshots)
```

OpenAI:

```
LLM_BASE_URL = "https://api.openai.com/v1"
LLM_API_KEY = "sk-..."
LLM_MODEL = "gpt-5-mini"
```

Anthropic (Claude), through Anthropic's OpenAI-compatible endpoint:

```
LLM_BASE_URL = "https://api.anthropic.com/v1/"
LLM_API_KEY = "sk-ant-..."
LLM_MODEL = "claude-opus-5"        # claude-sonnet-5 answers faster
```

OpenRouter (one key, hundreds of models):

```
LLM_BASE_URL = "https://openrouter.ai/api/v1"
LLM_API_KEY = "sk-or-..."
LLM_MODEL = "anthropic/claude-sonnet-5"   # any id from openrouter.ai/models that supports tools
```

Others work the same way:

| Provider | `LLM_BASE_URL` |
|---|---|
| LM Studio (local) | `http://localhost:1234/v1` (model = the loaded model's name) |
| Groq | `https://api.groq.com/openai/v1` |
| Google Gemini | `https://generativelanguage.googleapis.com/v1beta/openai/` |
| Anything else that speaks OpenAI chat completions with `tools` | its `/v1` URL |

`WORKER_MODEL` lets background workers use a different (say cheaper) model on the same endpoint.

## Configure the rest

```
USER_NAME = "Pepper"
HONORIFIC = "ma'am"
CITY = "Manchester"
VOCAB = "Jarvis, Pepper, Manchester, Spotify, Discord, Outlook."
```

| Setting | What it does |
|---|---|
| `USER_NAME` / `HONORIFIC` | Who you are and how it addresses you: "sir", "ma'am", "boss" |
| `CITY` | City for the weather line in the morning greeting (wttr.in, use + for spaces) |
| `WRITE_OK_DIRS` | Folders outside your home it may write files to without asking |
| `LLM_*`, `WORKER_MODEL`, `MAX_STEPS` | The brain, see above |
| `STT_ENGINE` | `"whisper"` (local, default) or `"openai"` with `STT_BASE_URL`, `STT_API_KEY`, `STT_MODEL` (OpenAI, Groq, a local whisper server) |
| `WHISPER_MODEL` / `VOCAB` | Local whisper size, and names it keeps mishearing |
| `VOICE_ENGINE` | `"kokoro"` (local, default), `"piper"` with `PIPER_MODEL`, or `"openai"` with `TTS_BASE_URL`, `TTS_API_KEY`, `TTS_MODEL` (needs `pcm` output) |
| `VOICE` / `SPEED` | Kokoro voice (bm_lewis, bm_george, bm_daniel, bm_fable), or the remote service's voice name |
| `WAKE_THRESHOLD` | Raise if it wakes on its own, lower if it ignores you |
| `END_SILENCE_S` | Raise if it cuts you off mid-sentence |
| `MIC_DEVICE` | None for the default input |
| `HOTKEY_LISTEN` / `HOTKEY_STOP` | Global hotkeys, default Win+J and Win+Shift+J |
| `WIDGET_*` | Which monitor and corner the widget sits in |

Restart after changing the config: `Stop-ScheduledTask Jarvis; Start-ScheduledTask Jarvis` in PowerShell.

## Run and control

It starts at logon by itself. To run it by hand instead (you see its log in the window): `.venv\Scripts\python jarvis.py`. The log is always in `logs\jarvis.log`.

Dashboard: **http://127.0.0.1:8765** (this PC only). It shows what Jarvis is doing, background workers, history with every step and screenshot, what it heard, and has Yes/No buttons and a box for typed commands.

| Do | How |
|---|---|
| Talk | "Hey Jarvis", **Win+J**, or `python jarvisctl.py listen` |
| Interrupt / cancel | Talk over it, say "Hey Jarvis", **Win+Shift+J**, or `python jarvisctl.py stop` |
| Type instead of talk | The dashboard box, or `python jarvisctl.py say open spotify` |
| Answer a "Shall I...?" | Say yes or no, the dashboard buttons, or `python jarvisctl.py yes` / `no` |
| Voice test | `python jarvisctl.py speak hello` |
| Status | `python jarvisctl.py status` |
| Fresh session | Say "new session" |
| Uninstall | `Unregister-ScheduledTask Jarvis`, then delete the folder |

After you answer, it listens for 5 seconds so you can follow up without the wake word. If a hotkey is already taken by Windows or another app, the log says so; pick another in `config_local.py`.

## Sessions and notes

Each conversation is a fresh session. A new one starts after 30 minutes idle (`SESSION_IDLE_RESET_MIN`), when you say "new session", or when Jarvis shuts down cleanly. Before a session closes the model rewrites `notes.md` (open threads, a dated log of what you asked, lessons about working with you), and the next session reads it first. `notes.md` is created on first use (see `notes.example.md`), stays out of git, and you can edit it by hand. If Jarvis is killed (logoff, Task Scheduler "End"), that session's notes are not written.

## Workers

For a job that takes more than a minute, or when you say "and also have Y going", Jarvis starts a **worker**: a separate conversation with the same tools, running in the background with a short name ("the report"). Jarvis answers in one line and stays free for you. Ask "what are the workers doing?", "tell the report to use last month", "stop the report". When a worker finishes or ends with a question ("NEED USER:"), Jarvis tells you. Its risky steps are asked out loud like Jarvis's own.

## Safety gate

The rules are `policy` in `brain.py`.

- Allowed without asking: the desktop tools (except Enter), PowerShell commands that only read or compute, and `write_file` inside your home folder and `WRITE_OK_DIRS`.
- Asks out loud first, with a spoken yes/no: PowerShell that deletes, moves, copies over or writes files, kills processes, changes services, scheduled tasks, the registry or system settings, installs software, runs elevated, pushes with git, or sends data to the web (POST and friends); `write_file` outside your home or into `.ssh`, your PowerShell profile, the Startup folder or the Jarvis folder itself; pressing Enter (that is how most apps send); and any tool it doesn't know.
- Clicks are not gated, so the persona tells it to ask before clicking Send, Post, Buy or Delete.
- No answer counts as no.

The command rules are a pattern match on the command text, not a sandbox: a determined model can get round them (for example by building a command string at runtime). Read `policy` before you trust it with anything important, and add your own rules there. Smaller local models follow the persona less reliably than large hosted ones.

## Voice

Kokoro works out of the box with British voices (`bm_lewis` is the default). For a closer JARVIS sound you can train your own Piper voice (see the Piper project's training guide), `pip install piper-tts` into `.venv`, then set `VOICE_ENGINE = "piper"` and `PIPER_MODEL` to the `.onnx` file with its `.onnx.json` next to it. No trained voice or film audio ships with this repo.

## Tests

```
.venv\Scripts\python test_brain.py      # agent loop against a fake model server, safety rules, workers, hotkeys
.venv\Scripts\python test_time_tag.py   # the greeting time tags
.venv\Scripts\python test_voice.py "Good evening."   # writes logs\voice-kokoro.wav
```

`test_brain.py` and `test_time_tag.py` need no model, microphone or Windows.

## Known untested

This port was written and checked on Linux: every file compiles, imports cleanly, and `test_brain.py` passes (the agent loop, tool-call streaming, the gate, workers, hotkey parsing and app matching). `requirements.txt` was resolved against Windows / Python 3.12 wheels. **None of it has been run on a real Windows machine yet.** Not verified:

- `install.ps1` end to end, including the Task Scheduler task (logon start, windowless `pythonw`, restart on failure).
- Everything in `winapi.py`: SendInput typing and keys, mouse moves and clicks on multi-monitor and high-DPI setups, window listing and the focus-stealing workaround in `focus()` (Windows sometimes refuses to change the foreground window), RegisterHotKey with Win+J.
- `open_app` / `list_apps` through `Get-StartApps` and `shell:AppsFolder` launching, and matching the new window afterwards.
- Screenshots with `mss` (coordinates with monitors left of or above the main one, DPI scaling), volume through `pycaw`, media keys, `plyer` notifications.
- `run_command` through Windows PowerShell 5.1 (UTF-8 output, windowless child processes) and the PowerShell patterns in the safety gate on real-world commands.
- Audio on Windows: `sounddevice` mic and speaker, wake word, whisper and Kokoro speed on typical hardware.
- The widget's placement and click-through on Windows.
- The real providers: only the OpenAI-compatible protocol was tested, against a fake server. Tool calling and image input through Ollama, OpenAI, Anthropic's compatible endpoint, OpenRouter, Groq and Gemini were not exercised. Media "status" (what is playing) is not available on Windows and says so.

Issues and fixes from Windows users are very welcome.

## Licence

MIT, see `LICENSE`.
