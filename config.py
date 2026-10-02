"""Jarvis settings. Change these (or override them in config_local.py), then restart Jarvis."""
import os
import tempfile

HOME = os.path.expanduser("~")
JARVIS_DIR = os.path.dirname(os.path.abspath(__file__))
RUNTIME_DIR = tempfile.gettempdir()
STATE_FILE = os.path.join(JARVIS_DIR, "state.json")
LOG_FILE = os.path.join(JARVIS_DIR, "logs", "jarvis.log")

# You
USER_NAME = "Tony"         # what Jarvis calls you when it talks about you
HONORIFIC = "sir"          # how Jarvis addresses you: "sir", "ma'am", "boss"...
CITY = "London"            # for the weather line in the morning greeting (wttr.in; use + for spaces, e.g. "New+York")

# Folders Jarvis may write to without asking (plus your home folder)
WRITE_OK_DIRS = []

# Brain: the official Antigravity CLI uses the Google account you sign in with (no API key).
# Set LLM_PROVIDER to "openai-compatible" to use Ollama or another compatible endpoint instead.
LLM_PROVIDER = "antigravity"
ANTIGRAVITY_CLI = os.environ.get("JARVIS_ANTIGRAVITY_CLI", "")  # optional path when agy isn't on PATH
LLM_BASE_URL = "http://localhost:11434/v1"
LLM_API_KEY = os.environ.get("JARVIS_LLM_API_KEY", "")   # or set it in config_local.py
LLM_MODEL = "gemini-3.8-flash-medium"
LLM_VISION = True          # False if the model can't read images (Jarvis then can't look at screenshots)
WORKER_MODEL = ""          # optional model for background workers; "" = same as LLM_MODEL
MAX_STEPS = 30             # tool calls per request before Jarvis gives up
SESSION_IDLE_RESET_MIN = 30   # after this long with nothing going on, the next request gets a fresh session
NOTES_FILE = os.path.join(JARVIS_DIR, "notes.md")   # running summary carried from session to session
PERSONA_FILE = os.path.join(JARVIS_DIR, "persona.md")

# Ears
MIC_DEVICE = None          # None = system default input, or a device name/number from: python -m sounddevice
WAKE_THRESHOLD = 0.5       # raise if it wakes by itself, lower if it misses you
WAKE_MIN_RMS = 80          # minimum recent PCM RMS for a wake; filters model spikes on digital silence
WAKE_VAD_THRESHOLD = 0.2   # OpenWakeWord's speech-activity gate; rejects wake scores on non-speech noise
WAIT_FOR_SPEECH_S = 6.0    # after "hey jarvis", how long to wait for you to start talking
END_SILENCE_S = 1.6        # this much silence = you've finished talking (lower values cut people off mid-thought)
MAX_UTTERANCE_S = 45.0
FOLLOW_UP_S = 5.0          # after Jarvis answers, listen this long without needing the wake word
CONFIRM_WAIT_S = 8.0       # how long to wait for "yes" on a confirmation
BARGE_IN_BY_VOICE = True   # talk over him to cut him off (needs headphones; set False if he interrupts himself)
STT_ENGINE = "whisper"     # "whisper" (local faster-whisper) or "openai" (any OpenAI-compatible /audio/transcriptions)
WHISPER_MODEL = "small.en"
STT_BASE_URL = "https://api.openai.com/v1"   # only for STT_ENGINE = "openai"
STT_API_KEY = os.environ.get("JARVIS_STT_API_KEY", "")
STT_MODEL = "whisper-1"
# Names and jargon whisper should expect. Add your own names, places and apps it keeps mishearing.
VOCAB = "Jarvis, Spotify, Discord, OBS, Steam, Explorer, Notepad, Chrome, Edge, Outlook."

# Hotkeys (Windows RegisterHotKey). Leave "" to turn one off.
HOTKEY_LISTEN = "win+j"         # same as saying "hey jarvis"
HOTKEY_STOP = "win+shift+j"     # shut up / cancel

# Mouth
VOICE_ENGINE = "kokoro"    # "kokoro" (local, works out of the box), "piper" (your own Piper voice, PIPER_MODEL)
                           # or "openai" (any OpenAI-compatible /audio/speech that can return pcm)
PIPER_MODEL = ""           # path to a Piper .onnx voice; its .onnx.json must sit next to it
VOICE = "bm_lewis"         # Kokoro voice: try bm_george, bm_daniel, bm_fable (for "openai": that service's voice name)
SPEED = 1.05
KOKORO_MODEL = os.path.join(JARVIS_DIR, "models", "kokoro-v1.0.onnx")
KOKORO_VOICES = os.path.join(JARVIS_DIR, "models", "voices-v1.0.bin")
TTS_BASE_URL = "https://api.openai.com/v1"   # only for VOICE_ENGINE = "openai"
TTS_API_KEY = os.environ.get("JARVIS_TTS_API_KEY", "")
TTS_MODEL = "gpt-4o-mini-tts"

# Desktop widget
WIDGET_SCREEN = "right"        # monitor by name (e.g. "\\\\.\\DISPLAY2"), or "left"/"right"
WIDGET_CORNER = "bottom-center"  # bottom-center, top-center, top-right, top-left, bottom-right, bottom-left
WIDGET_MARGIN_X = 16           # pixels from the side
WIDGET_MARGIN_Y = 30           # pixels from the top/bottom (the taskbar is already left out)

# Your own settings go in config_local.py (gitignored), e.g. USER_NAME = "Pepper"
try:
    from config_local import *  # noqa: F401,F403
except ImportError:
    pass
