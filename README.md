# Jarvis for Windows

A voice assistant for your Windows 10/11 PC that talks like J.A.R.V.I.S. from the Iron Man films. Say **"Hey Jarvis"**, wait for the chime, talk. It can open apps, switch windows, look at the screen, point things out on it, click, scroll, type, control media and volume, run PowerShell, write files, and hand longer jobs to background workers.

The default brain uses Gemini 3.8 Flash Medium through Google's official Antigravity CLI and the Google account you sign in with; no API key is needed. Wake word, speech recognition and speech output run locally. You can switch the brain to Ollama, LM Studio, OpenAI-compatible services, or other providers in `config_local.py`.

This is a Windows port of a Linux (KDE Plasma) Jarvis. The Windows app has its own small agent loop; the official Antigravity CLI is used only for its signed-in model session and is installed by setup.

How it fits together:

- `ears.py`: microphone, "Hey Jarvis" wake word, end-of-speech detection, speech to text (local faster-whisper, or any OpenAI-compatible transcription endpoint).
- `brain.py`: talks to the model (one streamed `/chat/completions` call per step), runs its tool calls, the persona, the notes between sessions, and the safety gate that asks you out loud before risky actions.
- `mouth.py`: text to speech (Kokoro by default, a Piper voice, or an OpenAI-compatible speech endpoint), cut off the moment you talk over it.
- `pctools.py` + `winapi.py`: the tools the model can call. `winapi.py` is the Windows part (windows, mouse, keyboard, hotkeys) through ctypes.
- `worker.py`: background workers, each its own conversation with the same tools.
- `jarvis.py`: the main loop. `dashboard.py` + `dashboard.html`: a local web dashboard. `widget.py`: the status capsule at the bottom of the screen. `overlay.py`: draws rings, arrows and labels on screen for `annotate`. `design.py`: their shared colours and motion. `events.py`: the activity log. `jarvisctl.py`: control it from a terminal, and make a bug report.
- `persona.md`: the system prompt, a template filled in from your config. `lines.md`: real JARVIS lines by situation, used to tune the voice.

## Requirements

- Windows 10 or 11, 64-bit.
- **Python 3.12** from python.org (keep the "py launcher" option ticked). Other versions may work; 3.12 is what the pinned packages are chosen for.
- A Google account with access to the Gemini model through Antigravity. The setup opens Google's CLI so you can sign in once; Jarvis does not ask for an API key.
- Optional: [Ollama](https://ollama.com) and a local model if you prefer not to use Antigravity.
- A microphone. Headphones are recommended if you want to interrupt it by talking over it.
- About 2 GB of disk for the voice, wake word and whisper models.

## Install

1. Install **Python 3.12** from python.org and keep the **py launcher** option checked.
2. [Download the ZIP](https://github.com/soupashh-ship-it/jarvis-windows/archive/refs/heads/main.zip) and extract it to a permanent folder. The scheduled task will use this folder, so do not move it after setup.
3. Open PowerShell in the extracted folder and run:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

`install.ps1` installs the official Antigravity CLI if needed, prepares Jarvis, and starts it:

1. creates `.venv` with Python 3.12 and installs `requirements.txt`,
2. downloads the Kokoro voice into `models\` and the "Hey Jarvis" wake word model,
3. opens the official Antigravity CLI. Sign in with the Google account that has access to the Gemini model, then type `/exit` or press **Ctrl+D** to return to setup,
4. registers two Task Scheduler tasks for your user, **Jarvis** and **Jarvis Widget** (no admin needed), that start Jarvis and its widget at every logon, windowless (two tasks not one: a task runs its actions one after another and Jarvis never ends), with restart on failure,
5. starts them.

The CLI stores its sign-in in your Windows credential store. The whisper models download on first start, so setup can take a few minutes.

## Update

If you downloaded a ZIP, stop Jarvis, extract the newer ZIP over the existing folder, then run `install.ps1` again. Your `config_local.py`, `notes.md`, logs and downloaded voice models are local files ignored by GitHub's ZIP and will be kept.

If you cloned the repo with Git, no reinstall needed; your `config_local.py`, `notes.md` and logs are kept. In PowerShell, in the repo folder, in this order:

```
"Jarvis", "Jarvis Widget" | % { Stop-ScheduledTask $_ }
git pull
.venv\Scripts\python -m pip install -r requirements.txt
"Jarvis", "Jarvis Widget" | % { Start-ScheduledTask $_ }
```

The pip line is quick when nothing changed, so always run it. Running `install.ps1` again instead does the same and is safe to repeat. If `git pull` refuses because you edited `config.py` itself, run `git stash`, `git pull`, then put your settings in `config_local.py` instead (`git stash show -p` shows what you had changed).

### Microphone

- Settings > Privacy & security > Microphone: turn on **Microphone access** and **Let desktop apps access your microphone**.
- Settings > System > Sound > Input: pick the mic you want as the default. Jarvis uses the default input unless you set `MIC_DEVICE`.
- To pick another mic, set `MIC_DEVICE = "Yeti"` (part of its name) in `config_local.py`. Windows lists each mic several times, once per audio system (MME, DirectSound, WASAPI, WDM-KS); a name picks the MME one. A number picks exactly that entry: every input is listed with its number at the top of `logs\jarvis.log` and by `.venv\Scripts\python -m sounddevice`.
- `logs\jarvis.log` says which mic it opened (`mic open: ...`) and records the peak wake score and signal level for each spoken attempt, including attempts interrupted by the manual trigger. Wake scores are accepted only when recent audio is present, which filters model spikes on digital silence.
- If the mic sends nothing but digital silence for 5 seconds (Windows' privacy switch, or a muted mic), Jarvis says so out loud once and logs it.
- The dashboard's mic meter shows whether it hears you; the line under it shows the wake word score and the threshold. Win+J is the fallback (if Windows or another app already owns it, the log says so; pick another `HOTKEY_LISTEN`).

## Choose a model

The default settings are already configured for the official Antigravity CLI and do not require `config_local.py`. The model runs through the account you signed in with; Jarvis does not read, store, or need a Gemini API key.

To change settings, create `config_local.py` in the repo folder (it is gitignored and overrides `config.py`). Models used through an OpenAI-compatible endpoint need **tool calling**; for screenshots they also need **vision** (otherwise set `LLM_VISION = False`). In that mode, the API key can come from the `JARVIS_LLM_API_KEY` environment variable instead of the file.

Ollama, local (no API key):

```
LLM_BASE_URL = "http://localhost:11434/v1"
LLM_MODEL = "qwen3-vl:8b"          # any Ollama model tagged "tools" (and "vision" for screenshots)
```

Antigravity subscription, through Google's official signed-in CLI (no API key):

```
LLM_PROVIDER = "antigravity"
LLM_MODEL = "gemini-3.8-flash-medium"  # Fast reply tier; use `agy models` for available model IDs
# WORKER_MODEL = "<model from agy models>"  # optional; blank uses the same model
```

The installer installs the [official Antigravity CLI](https://www.antigravity.google/docs/cli/install/) and opens its sign-in flow. Available models depend on the account; check `agy models`. Jarvis keeps its headless CLI session warm while running; screenshots are passed through its read-only image viewer. See the [headless CLI guide](https://www.antigravity.google/docs/cli/headless/) and [model list](https://www.antigravity.google/docs/models/).

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

`WORKER_MODEL` lets background workers use a different model. With Antigravity, choose a model ID shown by `agy models`; blank uses the same model as Jarvis.

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
| `WAKE_THRESHOLD` | Raise if it wakes on its own, lower if it ignores you (the log's `wake score` lines show how close you get) |
| `WAKE_MIN_RMS` | Minimum recent mic level needed to accept a wake score (lower for a very quiet mic; default `80`) |
| `WAKE_VAD_THRESHOLD` | Speech-activity cutoff applied to wake scores; lower if quiet speech is filtered |
| `END_SILENCE_S` | Raise if it cuts you off mid-sentence |
| `MIC_DEVICE` | None for the default input, or a number or part of a name (see [Microphone](#microphone)) |
| `HOTKEY_LISTEN` / `HOTKEY_STOP` | Global hotkeys, default Win+J and Win+Shift+J |
| `WIDGET_*` | Which monitor (`"left"`, `"right"` or a name) and where: `bottom-center` (default), `top-center` or a corner |

Restart after changing the config: `"Jarvis", "Jarvis Widget" | % { Stop-ScheduledTask $_; Start-ScheduledTask $_ }` in PowerShell.

A mistake inside `config_local.py` (a typo, an import that fails) now stops Jarvis with the error in `logs\console.log`, instead of quietly running on the defaults.

## Run and control

It starts at logon by itself. To run it by hand instead (you see its log in the window): `.venv\Scripts\python jarvis.py`. The log is always in `logs\jarvis.log` (it rotates at 2 MB, keeping three old ones); when it runs windowless, anything a library prints goes to `logs\console.log`.

The widget is a small capsule at the bottom centre of the screen. Resting, it's a little pill with a dim mic; it springs open when you talk to Jarvis, with one colour per state: a blue mic that pulses with your voice while listening, a white orb while thinking, a purple glow round the edge while he speaks, orange when he's waiting for a yes or no. It settles back a couple of seconds after he finishes. Drag the capsule itself anywhere (it remembers the spot next time); everywhere around it stays click-through, and it never takes the keyboard. (The Linux version blurs what's behind it; Windows can't blur behind a shape, so here it's a translucent dark fill.)

Dashboard: run `.venv\Scripts\python jarvisctl.py dashboard`. It opens http://127.0.0.1:8765 (this PC only) with the key from `logs\ctl.token`, so other Windows accounts on the same PC can't see or drive it; your browser keeps the key in a cookie, so later visits and Jarvis restarts don't need it again (delete `logs\ctl.token` to change it). On a shared PC keep the Jarvis folder inside your own user folder. The dashboard shows what Jarvis is doing, background workers, history with every step and screenshot, what it heard, and has Yes/No buttons and a box for typed commands.

| Do | How |
|---|---|
| Dashboard | `python jarvisctl.py dashboard` |
| Talk | "Hey Jarvis", **Win+J**, or `python jarvisctl.py listen` |
| Interrupt / cancel | Talk over it, say "Hey Jarvis", **Win+Shift+J**, or `python jarvisctl.py stop` |
| Type instead of talk | The dashboard box, or `python jarvisctl.py say open spotify` |
| Answer a "Shall I...?" | Say yes or no, the dashboard buttons, or `python jarvisctl.py yes` / `no` |
| Voice test | `python jarvisctl.py speak hello` |
| Status | `python jarvisctl.py status` |
| "Where is...?" | Ask "where's the volume mixer?" and it rings, points at and labels it on screen (`annotate`), then clears it |
| Bug report | Double-click `report.bat`, or `python jarvisctl.py report` (see below) |
| Fresh session | Say "new session" |
| Uninstall | `Unregister-ScheduledTask Jarvis; Unregister-ScheduledTask "Jarvis Widget"`, then delete the folder |

After you answer, it listens for 5 seconds so you can follow up without the wake word. If a hotkey is already taken by Windows or another app, the log says so; pick another in `config_local.py`.

## Bug reports

Double-click **`report.bat`** in the Jarvis folder (or run `.venv\Scripts\python jarvisctl.py report`). It makes `jarvis-report-<date>.zip` on your Desktop and opens Explorer on it; send that file (Discord, a GitHub issue). Nothing is uploaded by itself. It works even when Jarvis isn't running.

Inside: the logs (`jarvis.log` and its old copies, `console.log`, the activity log `events.jsonl`, which includes what Jarvis heard and said), the wake score and microphone lines on their own (`wake-and-mic.txt`), Python and Windows versions, the installed packages, the microphone list, and `config.py` / `config_local.py`. API keys, tokens and passwords are replaced with `<redacted>`: the values of your settings whose name contains KEY, TOKEN, SECRET, PASSWORD or AUTH (wherever they turn up), fields like `"password": "..."`, passwords inside URLs, and anything shaped like a key. Screenshots, `notes.md`, the dashboard token and the text Jarvis typed or wrote into files are left out. What you said and what Jarvis said are still in `events.jsonl`, and redaction can't catch a secret it can't recognise, so have a look inside before sending.

## Sessions and notes

Each conversation is a fresh session. A new one starts after 30 minutes idle (`SESSION_IDLE_RESET_MIN`), when you say "new session", or when Jarvis shuts down cleanly. Before a session closes the model rewrites `notes.md` (open threads, a dated log of what you asked, lessons about working with you), and the next session reads it first. `notes.md` is created on first use (see `notes.example.md`), stays out of git, and you can edit it by hand. If Jarvis is killed (logoff, Task Scheduler "End"), that session's notes are not written.

## Workers

For a job that takes more than a minute, or when you say "and also have Y going", Jarvis starts a **worker**: a separate conversation with the same tools, running in the background with a short name ("the report"). Jarvis answers in one line and stays free for you. Ask "what are the workers doing?", "tell the report to use last month", "stop the report". When a worker finishes or ends with a question ("NEED USER:"), Jarvis tells you. Its risky steps are asked out loud like Jarvis's own.

## Safety gate

The rules are `policy` in `brain.py`.

- Allowed without asking: the desktop tools (except Enter), PowerShell commands that only read or compute, and `write_file` inside your home folder and `WRITE_OK_DIRS`.
- Asks out loud first, with a spoken yes/no: PowerShell that deletes, moves, copies over, creates or writes files, downloads files, kills processes, starts programs or another shell, changes services, scheduled tasks, the registry or system settings, installs software, runs elevated, pushes with git, or sends data to the web (POST and friends); `write_file` outside your home or into `.ssh`, your PowerShell profile (found even when OneDrive moved Documents), the Startup folder or the Jarvis folder itself; `open_path` on a program, script or shortcut; pressing Enter (that is how most apps send); and any tool it doesn't know.
- The question says what will really happen, from the actual arguments: the command itself ("Shall I run Remove-Item notes.txt (delete the old notes)?"), or the full path of the file and whether it already exists. The model's own description only rides along in brackets.
- Only a plain yes counts: "yes", "yeah, go ahead", "okay, do it, sir". Anything else is a no, including "I'm not sure", "not okay" and "I can't confirm that". No answer counts as no.
- Clicks are not gated, so the persona (and each worker's brief) tells it to ask before clicking Send, Post, Buy or Delete.
- One at a time on the desktop: while Jarvis or a worker is using the mouse and keyboard in a request or job, the others are told it's busy. Each keeps its own screenshot, so clicks land where that one looked.
- Stop (the hotkey, the dashboard, `jarvisctl stop`) ends Jarvis's typing mid-word, kills his running command together with the programs it started, and drops anything you said that was still being transcribed. Background workers keep going; stop those by name.

The command rules are a pattern match on the command text, not a sandbox: a determined model can get round them (for example by building a command string at runtime). Read `policy` before you trust it with anything important, and add your own rules there. Smaller local models follow the persona less reliably than large hosted ones.

## Voice

Kokoro works out of the box with British voices (`bm_lewis` is the default). The maintainer's own `config_local.py` uses the Female American **Samantha** voice (`af_aoede`), which is what current Ash builds default to. For a closer JARVIS sound you can train your own Piper voice (see the Piper project's training guide), `pip install piper-tts` into `.venv`, then set `VOICE_ENGINE = "piper"` and `PIPER_MODEL` to the `.onnx` file with its `.onnx.json` next to it. No trained voice or film audio ships with this repo.

**Adding a custom voice** (if you don't like Samantha / want your own):

- **Quick way (no training):** Kokoro ships many voices. Set `VOICE = "<name>"` in `config_local.py`: `bm_lewis`, `bm_george`, `bm_daniel`, `bm_fable` (male British), `af_aoede`, `af_heart`, `af_bella`, `af_sarah` (female American), etc. Restart Jarvis.
- **Closer JARVIS/MCUED-style sound:** train a Piper voice (Piper has per-voice training docs), put `<voice>.onnx` + `<voice>.onnx.json` in `models/`, and set:
  ```
  VOICE_ENGINE = "piper"
  PIPER_MODEL = "models\\your-voice.onnx"   # the .json sits next to it
  ```
- **Web TTS:** point `VOICE_ENGINE = "openai"` at any OpenAI-compatible TTS endpoint (`TTS_BASE_URL`/`TTS_API_KEY`/`TTS_MODEL`); it must support `pcm` output.

## Wake word

By default Jarvis wakes on "**Hey Jarvis**" via openWakeWord's `hey_jarvis_v0.1.onnx`. To wake on "**Hey Samantha**" too (or instead), drop a custom openWakeWord model into `models/` (e.g. `hey_samantha.onnx`) and set in `config_local.py`:

```
WAKE_WORD_MODELS = ["hey_jarvis_v0.1.onnx", "hey_samantha.onnx"]
```

A custom model is trained from openWakeWord's docs (same pipeline as their `hey_jarvis` training example); openWakeWord does not ship a `hey_samantha` model, and film audio is not redistributable, so train your own. The same applies for any phrase: put the `.onnx` in `models/`, add its filename to `WAKE_WORD_MODELS`, restart.

## Tests

```
.venv\Scripts\python test_brain.py      # agent loop against a fake model server, safety rules, yes/no parsing, workers, hotkeys
.venv\Scripts\python test_time_tag.py   # the greeting time tags
.venv\Scripts\python test_tools.py      # the newer tools: organize_folder, calendar_add, recall, find_duplicates, biggest_files, current_date
.venv\Scripts\python test_voice.py "Good evening."   # writes logs\voice-kokoro.wav
```

`test_brain.py` and `test_time_tag.py` need no model, microphone or Windows.

## Known limits and verification

Jarvis has been run on Windows with Python 3.12 and the official Antigravity CLI. The local voice pipeline and Gemini subscription connection have both been used on one Windows machine. A clean install from the GitHub ZIP has not yet been independently verified on another PC, and display scaling, microphones and available models vary by setup.

- The wake word and Gemini model have worked on the maintainer's PC, but normal-speed speech recognition can still depend on the microphone and room noise. Use Win+J if needed and check `logs\jarvis.log` for microphone diagnostics.
- The setup script, Task Scheduler restart-on-failure behavior, high-DPI/multi-monitor pointer tools, exclusive-fullscreen overlays, and `report.bat` have not been verified across a range of Windows PCs.
- Other model providers may differ in tool calling and image support. Media "status" (what is playing) is not available on Windows and reports that limitation.
- **Changed after the first Windows report:** "Hey Jarvis" was rarely heard (cause not confirmed yet; the port now logs the mic it opened and every near-miss wake score, warns when the mic is digitally silent, and `MIC_DEVICE` by name no longer crashes on Windows' duplicate device names); background work didn't happen, most likely because of Gemini (its thought signatures were dropped, which Gemini 3 rejects on the request after any tool call, and two tool calls in one Gemini reply were merged into one broken call; the persona now also insists on actually calling `start_worker`); the old corner panel is replaced by the new capsule widget, and `annotate` is ported.
- **Still not verified on real Windows:** whether those changes actually fix the wake word on the tester's mic; the new widget and the `annotate` overlay on Windows (placement above the taskbar, click-through, high-DPI and multi-monitor: the overlay works in physical pixels, so its labels look smaller on a scaled display; it can't draw over exclusive-fullscreen games); WASAPI devices with automatic conversion; `report.bat` and finding a OneDrive-moved Desktop.
- **Not verified at all:** `install.ps1` end to end, including the Task Scheduler task (logon start, windowless `pythonw`, restart on failure); everything in `winapi.py` (SendInput typing and keys, mouse moves and clicks on multi-monitor and high-DPI setups, window listing and the focus-stealing workaround in `focus()`, RegisterHotKey with Win+J); `open_app` / `list_apps` through `Get-StartApps` and `shell:AppsFolder`; screenshots with `mss` (monitors left of or above the main one, DPI scaling), volume through `pycaw`, media keys, `plyer` notifications; `run_command` through Windows PowerShell 5.1 and the gate's PowerShell patterns on real-world commands; whisper and Kokoro speed on typical hardware.
- **Changed after the second Windows report (a code review), checked only on Linux and with fake inputs:** the strict yes/no, the gate's new rules and questions, the dashboard key and cookie (checked in Chrome against a stand-in Jarvis), workers' timeline in the history, the step limit per tool call, one desktop driver at a time, per-agent screenshots, Stop cancelling typing and transcripts. Not run on Windows at all yet: the two logon tasks, the model re-download check in `install.ps1`, SendInput failure reporting, cancelling typing mid-word, killing a command's child processes, the OneDrive-moved Documents and Startup lookups, the exit code after a crash.
- **Providers:** only the OpenAI-compatible protocol was tested, against a fake server (including Gemini's quirks as documented: no `index` on streamed tool calls, whole calls in one chunk, repeated ids, `extra_content.google.thought_signature`, and no empty-properties schemas). Real tool calling and image input through Ollama, OpenAI, Anthropic's compatible endpoint, OpenRouter, Groq and Gemini were not exercised here. Media "status" (what is playing) is not available on Windows and says so.

Issues and fixes from Windows users are very welcome; please attach the `report.bat` zip.

## Licence

MIT, see `LICENSE`.
