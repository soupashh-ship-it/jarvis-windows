using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>
/// The offline brain. With no API key configured, JARVIS still works: it opens apps, controls media and
/// volume, takes screenshots, sets timers, reports the machine, and remembers things. Deterministic, instant,
/// and nothing leaves the machine.
/// </summary>
public sealed class IntentEngine
{
    private readonly JarvisServices _services;

    public IntentEngine(JarvisServices services) => _services = services;

    private static string Clean(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        t = Regex.Replace(t, @"^(hey|ok|okay|hi|hello)?\s*jarvis[,.\s]*", "");
        t = Regex.Replace(t, @"^(please|would you|could you|can you|will you)\s+", "");
        t = Regex.Replace(t, @"\s+(please|for me|thanks|thank you)$", "");
        return t.Trim();
    }

    public async Task<string?> TryHandleAsync(string raw, CancellationToken ct)
    {
        var text = Clean(raw);
        if (text.Length == 0) return "I'm listening, " + _services.Settings.Honorific + ".";

        // ---- presence and time ----
        if (Regex.IsMatch(text, @"^(hello|hi|hey|good morning|good afternoon|good evening|you there|are you up)\b"))
        {
            var hour = DateTime.Now.Hour;
            var part = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
            return $"{part}, {_services.Settings.Honorific}. At your service.";
        }

        if (Regex.IsMatch(text, @"^(what('s| is) )?the time\b|^what time is it\b|^time$"))
            return $"It is {DateTime.Now:h:mm tt}, {_services.Settings.Honorific}.";

        if (Regex.IsMatch(text, @"^(what('s| is) )?(the )?(date|day)\b|^what day is it\b"))
            return $"It is {DateTime.Now:dddd d MMMM}.";

        if (Regex.IsMatch(text, @"^(status|system status|status report|how('s| is) the (system|pc|computer)|" +
                                @"how are the resources|system report)\b"))
            return SystemProbe.Summary();

        if (Regex.IsMatch(text, @"^(what can you do|help|your commands|what are your capabilities)\b"))
            return "I can open and arrange apps, take screenshots, click and type, control volume and media, " +
                   "report the system, set timers, and with an API key configured, do anything a language model can. " +
                   "Try \"open spotify\" or \"volume down\".";

        // ---- volume ----
        var setVolume = Regex.Match(text, @"^(?:set )?(?:the )?volume (?:to )?(\d{1,3})(?: ?percent)?$");
        if (setVolume.Success)
        {
            var result = SystemTools.Volume("set", int.Parse(setVolume.Groups[1].Value));
            return result.Success ? result.Content : "I couldn't reach the audio device.";
        }

        if (Regex.IsMatch(text, @"^(?:turn (?:it|the volume) up|volume up|louder|turn it up)$"))
            return VolumeResult("up", "Louder.");
        if (Regex.IsMatch(text, @"^(?:turn (?:it|the volume) down|volume down|quieter|turn it down)$"))
            return VolumeResult("down", "Quieter.");
        if (Regex.IsMatch(text, @"^(?:mute|mute the (?:sound|volume|audio))$"))
            return VolumeResult("mute", "Muted.");
        if (Regex.IsMatch(text, @"^(?:unmute|unmute the (?:sound|volume|audio))$"))
            return VolumeResult("unmute", "Sound restored.");

        // ---- media ----
        if (Regex.IsMatch(text, @"^(play|pause|resume|play or pause|play\/pause)$|^pause the (music|video)$|" +
                                @"^resume( the (music|video))?$"))
        {
            InputApi.PressCombo("playpause");
            return "Of course.";
        }
        if (Regex.IsMatch(text, @"^(next|next track|skip)( track| song)?$"))
        {
            InputApi.PressCombo("next");
            return "Skipped.";
        }
        if (Regex.IsMatch(text, @"^(previous|last|previous track|go back a track)$"))
        {
            InputApi.PressCombo("previous");
            return "Previous track.";
        }

        // ---- screenshots ----
        if (Regex.IsMatch(text, @"^(take a |a )?screenshot\b|^capture the screen\b|^look at (my|the) screen\b"))
        {
            var shot = await ScreenCapture.CaptureAsync("primary", 1.0).ConfigureAwait(false);
            if (shot is null) return "I'm afraid the screenshot failed.";
            _services.Screenshot.Update(shot, _services.DesktopActions.Version);
            var bytes = ScreenCapture.EncodeJpeg(shot.Bitmap);
            var file = ScreenCapture.SaveBytes(bytes, Paths.ShotsDir);
            shot.Bitmap.Dispose();
            await _services.Ui.NotifyAsync("JARVIS", $"Screenshot saved to {Paths.ShotsDir}").ConfigureAwait(false);
            return $"Captured. It is in the shots folder as {file}.";
        }

        // ---- timers ----
        var timer = Regex.Match(text, @"^(?:set (?:a|the) )?timer (?:for )?(\d+)\s*(second|seconds|minute|minutes|hour|hours)\b" +
                                        @"(?: for (.*))?$|^(?:remind me|wake me) in (\d+)\s*(second|seconds|minute|minutes|hour|hours)\b(?: to (.*))?$");
        if (timer.Success)
        {
            var first = timer.Groups[1].Success;
            var amount = int.Parse(first ? timer.Groups[1].Value : timer.Groups[4].Value);
            var unit = (first ? timer.Groups[2].Value : timer.Groups[5].Value).ToLowerInvariant();
            var label = first ? timer.Groups[3].Value : timer.Groups[6].Value;
            var seconds = unit.StartsWith("second") ? amount : unit.StartsWith("minute") ? amount * 60 : amount * 3600;
            var item = _services.Timers.Add(TimeSpan.FromSeconds(seconds), label);
            var friendly = seconds < 90 ? $"{seconds} seconds" : seconds < 5400 ? $"{seconds / 60} minutes" : $"{seconds / 3600} hours";
            return $"Timer set for {friendly}{(label.Length > 0 ? $", for {label}" : "")}.";
        }

        // ---- clipboard ----
        if (Regex.IsMatch(text, @"^(what('s| is) on )?the clipboard\b|^read the clipboard\b|^what did i copy\b"))
        {
            var content = ClipboardApi.GetText();
            return content.Length == 0 ? "The clipboard is empty." :
                content.Length > 200 ? $"It begins: {content[..200]}…" : content;
        }

        // ---- memory ----
        var remember = Regex.Match(text, @"^remember (that )?(.+)$|^make a note that (.+)$");
        if (remember.Success)
        {
            var fact = remember.Groups[2].Success ? remember.Groups[2].Value : remember.Groups[3].Value;
            _services.Memory.Remember(fact);
            return "Noted, " + _services.Settings.Honorific + ".";
        }

        var recall = Regex.Match(text, @"^(?:what do you (?:remember|know) about|recall(?: about)?) (.+)$");
        if (recall.Success)
        {
            var facts = _services.Memory.Search(recall.Groups[1].Value);
            return facts.Count == 0
                ? "I'm afraid I don't have anything recorded about that."
                : "I have: " + string.Join("; ", facts.Select(f => f.Text)) + ".";
        }

        // ---- weather ----
        if (Regex.IsMatch(text, @"^(what('s| is) )?the weather\b|^weather\b|^how('s| is) the weather\b"))
        {
            var cityMatch = Regex.Match(text, @"\b(?:in|for) ([a-z\s]+)$");
            var city = cityMatch.Success ? cityMatch.Groups[1].Value.Trim() : _services.Settings.City;
            var result = await WeatherAsync(city).ConfigureAwait(false);
            return result;
        }

        // ---- web search ----
        var search = Regex.Match(text, @"^(?:search (?:the web |online )?for|google|look up) (.+)$");
        if (search.Success)
        {
            var query = search.Groups[1].Value;
            AppLauncher.OpenUri("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
            return $"Searching the web for {query}.";
        }

        // ---- open and close ----
        var open = Regex.Match(text, @"^(?:open|launch|start|run|bring up|show me) (.+)$");
        if (open.Success)
        {
            var target = open.Groups[1].Value.Trim();
            if (target.StartsWith("http")) { AppLauncher.OpenUri(target); return "Opening it."; }
            var result = AppLauncher.Launch(target);
            return result.Message;
        }

        var focus = Regex.Match(text, @"^(?:switch to|go to|focus|activate) (.+)$");
        if (focus.Success)
        {
            var matches = WindowApi.Find(focus.Groups[1].Value.Trim());
            if (matches.Count == 0) return $"I can't see a window called {focus.Groups[1].Value}.";
            WindowApi.Focus(matches[0]);
            _services.Screenshot.RememberFocus(matches[0].Handle);
            return $"Switched to {matches[0].Title}.";
        }

        var close = Regex.Match(text, @"^(?:close|dismiss|exit|quit) (.+)$");
        if (close.Success)
        {
            var target = close.Groups[1].Value.Trim();
            var matches = WindowApi.Find(target);
            if (matches.Count == 0) return $"I can't see a window called {target}.";
            var allowed = await ConfirmAsync($"Shall I close {matches[0].Title}?", "window.close", ct).ConfigureAwait(false);
            if (!allowed) return "Very good, I'll leave it.";
            Native.PostMessage(matches[0].Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return $"Closed {matches[0].Title}.";
        }

        // ---- typing ----
        var type = Regex.Match(text, @"^type (.+)$");
        if (type.Success)
        {
            if (!_services.Screenshot.HasShot)
                return "Take a screenshot first, " + _services.Settings.Honorific + ", so I know which window has focus.";
            if (_services.Screenshot.FocusRefusal() is { } refusal) return refusal;
            InputApi.TypeText(type.Groups[1].Value);
            return "Typed.";
        }

        // ---- system ----
        if (Regex.IsMatch(text, @"^(lock|lock the (pc|computer|screen))\b"))
        {
            if (!await ConfirmAsync("Shall I lock the computer?", "system.power", ct).ConfigureAwait(false))
                return "As you wish.";
            Native.LockWorkStation();
            return "Locked.";
        }

        if (Regex.IsMatch(text, @"^(sleep|suspend|hibernate)( the (pc|computer))?$"))
        {
            if (!await ConfirmAsync("Shall I put the computer to sleep?", "system.power", ct).ConfigureAwait(false))
                return "As you wish.";
            Native.SetSuspendState(false, false, false);
            return "Going to sleep.";
        }

        if (Regex.IsMatch(text, @"^(minimize|hide) (everything|all windows|all)$|^show the desktop\b"))
        {
            InputApi.PressCombo("win+d");
            return "Desktop.";
        }

        // ---- arrange windows ----
        var arrange = Regex.Match(text, @"^(snap|move) (.+?) (left|right|maximize|minimize|center)$");
        if (arrange.Success)
        {
            var matches = WindowApi.Find(arrange.Groups[2].Value.Trim());
            if (matches.Count == 0) return "I can't see that window.";
            WindowApi.Act(arrange.Groups[3].Value, matches[0]);
            return $"{char.ToUpperInvariant(arrange.Groups[3].Value[0])}{arrange.Groups[3].Value[1..]} done.";
        }

        if (Regex.IsMatch(text, @"^(what('s| is) (open|running)|list (the )?windows|show me the windows)\b"))
        {
            var windows = WindowApi.All();
            return windows.Count == 0 ? "Nothing is open." :
                "Open: " + string.Join(", ", windows.Take(8).Select(w => w.Title));
        }

        return null;
    }

    private async Task<bool> ConfirmAsync(string question, string rule, CancellationToken ct)
    {
        var outcome = await _services.Approvals.RequestAsync(
            new ApprovalRequest("local", question, "", rule, RiskLevel.Confirm), ct).ConfigureAwait(false);
        return outcome == ApprovalOutcome.Allowed;
    }

    private static string VolumeResult(string action, string fallback)
    {
        var result = SystemTools.Volume(action, 0);
        return result.Success ? result.Content : fallback;
    }

    private async Task<string> WeatherAsync(string city)
    {
        if (!_services.Settings.AllowNetwork) return "Network access is switched off in settings.";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.5.0");
            var slug = Uri.EscapeDataString(city).Replace("%20", "+");
            var text = await http.GetStringAsync($"https://wttr.in/{slug}?format=%C,+%t,feels+like+%f").ConfigureAwait(false);
            return $"{city}: {text.Trim()}";
        }
        catch (Exception ex)
        {
            return $"I couldn't reach the weather service: {ex.Message}";
        }
    }
}
