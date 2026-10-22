using System.Runtime.InteropServices;
using System.Windows.Interop;
using Jarvis.Core;

namespace Jarvis.App;

public static class HotkeyParser
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        var hasKey = false;

        foreach (var part in parts)
        {
            var token = part.ToLowerInvariant();
            switch (token)
            {
                case "ctrl": case "control": if ((modifiers & ModControl) != 0) return false; modifiers |= ModControl; continue;
                case "alt": if ((modifiers & ModAlt) != 0) return false; modifiers |= ModAlt; continue;
                case "shift": if ((modifiers & ModShift) != 0) return false; modifiers |= ModShift; continue;
                case "win": case "windows": case "meta": if ((modifiers & ModWin) != 0) return false; modifiers |= ModWin; continue;
            }

            if (!InputApi.KeyMap.TryGetValue(token, out var vk)) return false;
            if (vk is 0x11 or 0x10 or 0x12 or 0x5B) return false;
            if (hasKey) return false;
            hasKey = true;
            virtualKey = vk;
        }

        return hasKey;
    }

    public static string Describe(string text) => text;
}

/// <summary>System-wide hotkeys, registered on a hidden message window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int TalkId = 0x4A01;
    private const int StopId = 0x4A02;
    private const int DeckId = 0x4A03;

    private HwndSource? _source;
    private readonly List<int> _registered = new();

    public event Action? TalkPressed;
    public event Action? StopPressed;
    public event Action? DeckPressed;
    public string LastError { get; private set; } = "";

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("JarvisHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = (IntPtr)(-3) // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Register(AppSettings settings)
    {
        UnregisterAll();
        LastError = "";
        TryRegister(TalkId, settings.HotkeyTalk);
        TryRegister(StopId, settings.HotkeyStop);
        TryRegister(DeckId, settings.HotkeyDeck);
    }

    private void TryRegister(int id, string combo)
    {
        if (_source is null) return;
        if (!HotkeyParser.TryParse(combo, out var modifiers, out var vk))
        {
            LastError = $"Invalid hotkey '{combo}'";
            return;
        }
        if (Native.RegisterHotKey(_source.Handle, id, modifiers, vk))
        {
            _registered.Add(id);
        }
        else
        {
            LastError = $"'{combo}' is already taken by another app";
            Log.Warn("hotkey registration failed: " + combo);
        }
    }

    private void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _registered) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case TalkId: TalkPressed?.Invoke(); break;
            case StopId: StopPressed?.Invoke(); break;
            case DeckId: DeckPressed?.Invoke(); break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
