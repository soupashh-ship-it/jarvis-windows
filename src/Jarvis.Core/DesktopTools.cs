using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

public static class DesktopTools
{
    public static IEnumerable<ToolDefinition> Create()
    {
        yield return new ToolDefinition
        {
            Name = "list_windows",
            Category = "desktop",
            ReadOnly = true,
            Description = "List the open windows: title, application, position and size, and which one is active.",
            ParametersJson = T.Schema(),
            Handler = (_, _) =>
            {
                var windows = WindowApi.All();
                if (windows.Count == 0) return Task.FromResult(ToolResult.Done("No visible windows."));
                var sb = new StringBuilder();
                foreach (var w in windows.Take(40))
                    sb.AppendLine((w.Active ? "* " : "  ") + w.Describe());
                return Task.FromResult(ToolResult.Done(sb.ToString().TrimEnd()));
            }
        };

        yield return new ToolDefinition
        {
            Name = "focus_window",
            Category = "desktop",
            ReadOnly = false,
            Description = "Bring a window to the front and give it keyboard focus. Matches the window title or app name.",
            ParametersJson = T.Schema(T.Str("query", "Part of a window title or an application name, e.g. 'discord'.", true)),
            Describe = a => $"switch to {Json.Str(a, "query")}",
            Handler = (ctx, args) =>
            {
                var query = Json.Str(args, "query");
                var matches = WindowApi.Find(query);
                if (matches.Count == 0) return Task.FromResult(ToolResult.Fail($"No open window matches '{query}'."));
                var target = matches[0];
                if (!WindowApi.Focus(target)) return Task.FromResult(ToolResult.Fail($"Windows would not let me focus '{target.Title}'."));
                ctx.Services.Screenshot.RememberFocus(target.Handle);
                return Task.FromResult(ToolResult.Done($"Focused '{target.Title}' at {target.Bounds.Left},{target.Bounds.Top} " +
                                                       $"{target.Bounds.Width}x{target.Bounds.Height}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "window_action",
            Category = "desktop",
            ReadOnly = false,
            Description = "Arrange or close a window: minimize, maximize, restore, close, left (half screen), right, " +
                          "top, bottom, or center. Give a window name, or leave it empty for the active window.",
            ParametersJson = T.Schema(
                T.OneOf("action", "What to do with the window.", new[]
                {
                    "minimize", "maximize", "restore", "close", "left", "right", "top", "bottom", "center"
                }, true),
                T.Str("query", "Window or app name. Empty means the window in front.")),
            Describe = a => $"{Json.Str(a, "action")} the window{(Json.Str(a, "query") is { Length: > 0 } q ? " " + q : "")}",
            Handler = (ctx, args) =>
            {
                var action = Json.Str(args, "action").ToLowerInvariant();
                var query = Json.Str(args, "query");
                WindowInfo? window = string.IsNullOrWhiteSpace(query) ? WindowApi.Active() : WindowApi.Find(query).FirstOrDefault();
                if (window is null) return Task.FromResult(ToolResult.Fail($"No window matches '{query}'."));
                var ok = WindowApi.Act(action, window);
                return Task.FromResult(ok
                    ? ToolResult.Done($"{action} applied to '{window.Title}'.")
                    : ToolResult.Fail($"Windows did not accept '{action}' for '{window.Title}'."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "screenshot",
            Category = "screen",
            ReadOnly = true,
            Description = "Look at the screen. target: 'primary', 'secondary', 'active_window' (sharpest), or 'all'. " +
                          "click_at, move_mouse, scroll and annotate use pixel coordinates from the most recent screenshot.",
            ParametersJson = T.Schema(T.OneOf("target", "Which part of the screen to capture.",
                new[] { "primary", "secondary", "active_window", "all" }, true)),
            Handler = async (ctx, args) =>
            {
                var target = Json.Str(args, "target", "primary");
                var image = await ScreenCapture.CaptureAsync(target).ConfigureAwait(false);
                if (image is null) return ToolResult.Fail("The screenshot failed.");

                ctx.Services.Screenshot.Update(image, ctx.Services.DesktopActions.Version);
                // From now on, clicking is only safe while this same window still has focus.
                ctx.Services.Screenshot.RememberFocus(WindowApi.Active()?.Handle ?? IntPtr.Zero);
                var bytes = ScreenCapture.EncodeJpeg(image.Bitmap);
                var file = ScreenCapture.SaveBytes(bytes, Paths.ShotsDir);
                ctx.Services.Events.Emit("screenshot", image.Label, true,
                    ("file", file), ("width", image.Bitmap.Width), ("height", image.Bitmap.Height));

                var note = $"Screenshot of {image.Label}, {image.Bitmap.Width}x{image.Bitmap.Height} pixels. " +
                           "Use these image-pixel coordinates with click_at and annotate.";
                image.Bitmap.Dispose();
                return ToolResult.WithImage(note, bytes);
            }
        };

        yield return new ToolDefinition
        {
            Name = "click_at",
            Category = "screen",
            ReadOnly = false,
            Description = "Click at a point in the most recent screenshot image (pixel coordinates in that image). " +
                          "button: left, right or middle; clicks: 1 or 2.",
            ParametersJson = T.Schema(
                T.Int("x", "X coordinate in the last screenshot image.", true),
                T.Int("y", "Y coordinate in the last screenshot image.", true),
                T.OneOf("button", "Mouse button.", new[] { "left", "right", "middle" }),
                T.Int("clicks", "1 for a click, 2 for a double-click.")),
            Describe = a => $"click at {Json.Int(a, "x")},{Json.Int(a, "y")}",
            Handler = (ctx, args) =>
            {
                if (!ctx.Services.Screenshot.HasShot)
                    return Task.FromResult(ToolResult.Fail("Take a screenshot first; I have no coordinates to click."));
                var refusal = ctx.Services.Screenshot.FocusRefusal(ctx.Services.DesktopActions.Version);
                if (refusal is not null) return Task.FromResult(ToolResult.Fail(refusal));

                var (x, y) = ctx.Services.Screenshot.ToDesktop(Json.Int(args, "x"), Json.Int(args, "y"));
                InputApi.MoveTo(x, y);
                Thread.Sleep(35);
                InputApi.Click(Json.Str(args, "button", "left"), Json.Int(args, "clicks", 1));
                Thread.Sleep(110);
                var active = WindowApi.Active();
                if (active is not null) ctx.Services.Screenshot.RememberFocus(active.Handle);
                return Task.FromResult(ToolResult.Done(
                    $"Clicked {Json.Str(args, "button", "left")} x{Json.Int(args, "clicks", 1)} at desktop {x},{y}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "move_mouse",
            Category = "screen",
            ReadOnly = false,
            Description = "Move the mouse pointer to a point in the most recent screenshot image.",
            ParametersJson = T.Schema(
                T.Int("x", "X coordinate in the last screenshot image.", true),
                T.Int("y", "Y coordinate in the last screenshot image.", true)),
            Handler = (ctx, args) =>
            {
                if (!ctx.Services.Screenshot.HasShot) return Task.FromResult(ToolResult.Fail("Take a screenshot first."));
                var refusal = ctx.Services.Screenshot.FocusRefusal(ctx.Services.DesktopActions.Version);
                if (refusal is not null) return Task.FromResult(ToolResult.Fail(refusal));
                var (x, y) = ctx.Services.Screenshot.ToDesktop(Json.Int(args, "x"), Json.Int(args, "y"));
                InputApi.MoveTo(x, y);
                return Task.FromResult(ToolResult.Done($"Moved to {x},{y}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "scroll",
            Category = "screen",
            ReadOnly = false,
            Description = "Scroll the mouse wheel over a point in the most recent screenshot image. " +
                          "direction: up or down (or left/right). amount: notches, 1 to 20.",
            ParametersJson = T.Schema(
                T.Int("x", "X coordinate in the last screenshot image.", true),
                T.Int("y", "Y coordinate in the last screenshot image.", true),
                T.OneOf("direction", "Wheel direction.", new[] { "up", "down", "left", "right" }, true),
                T.Int("amount", "Number of notches (1-20).")),
            Handler = (ctx, args) =>
            {
                if (!ctx.Services.Screenshot.HasShot) return Task.FromResult(ToolResult.Fail("Take a screenshot first."));
                var refusal = ctx.Services.Screenshot.FocusRefusal(ctx.Services.DesktopActions.Version);
                if (refusal is not null) return Task.FromResult(ToolResult.Fail(refusal));
                var (x, y) = ctx.Services.Screenshot.ToDesktop(Json.Int(args, "x"), Json.Int(args, "y"));
                InputApi.MoveTo(x, y);
                Thread.Sleep(40);
                var direction = Json.Str(args, "direction", "down").ToLowerInvariant();
                var amount = Math.Clamp(Json.Int(args, "amount", 3), 1, 20);
                var horizontal = direction is "left" or "right";
                var signed = direction is "down" or "right" ? -amount : amount;
                InputApi.Scroll(signed, horizontal);
                return Task.FromResult(ToolResult.Done($"Scrolled {direction} {amount}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "type_text",
            Category = "screen",
            ReadOnly = false,
            Description = "Type text into whatever window has focus, as if on the keyboard. Newlines are not allowed; " +
                          "use press_keys 'enter' separately.",
            ParametersJson = T.Schema(T.Str("text", "The text to type.", true)),
            Describe = a => "type into the focused window",
            Handler = (ctx, args) =>
            {
                var refusal = ctx.Services.Screenshot.FocusRefusal();
                if (refusal is not null) return Task.FromResult(ToolResult.Fail(refusal));
                var text = Json.Str(args, "text").Replace("\r", " ").Replace("\n", " ");
                if (text.Length == 0) return Task.FromResult(ToolResult.Fail("There was nothing to type."));
                InputApi.TypeText(text);
                return Task.FromResult(ToolResult.Done("Typed."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "press_keys",
            Category = "screen",
            ReadOnly = false,
            Description = "Press a key or combination in the focused window, e.g. 'enter', 'ctrl+t', 'alt+tab', " +
                          "'win+d', 'ctrl+shift+esc'.",
            ParametersJson = T.Schema(T.Str("keys", "Key names joined with '+'.", true)),
            Describe = a => $"press {Json.Str(a, "keys")}",
            Handler = (ctx, args) =>
            {
                var refusal = ctx.Services.Screenshot.FocusRefusal();
                if (refusal is not null) return Task.FromResult(ToolResult.Fail(refusal));
                var keys = Json.Str(args, "keys");
                var (ok, unknown) = InputApi.PressCombo(keys);
                return Task.FromResult(ok
                    ? ToolResult.Done($"Pressed {keys}.")
                    : ToolResult.Fail($"I do not know the key(s): {string.Join(", ", unknown)}."));
            }
        };

        yield return new ToolDefinition
        {
            Name = "annotate",
            Category = "screen",
            ReadOnly = false,
            Description = "Draw on the user's screen to show them where something is: bright rings, arrows and boxes " +
                          "with numbered steps. Coordinates are pixels in the most recent screenshot image, exactly " +
                          "like click_at. Shapes: ring (x, y, radius), arrow (from_x, from_y -> x, y; head at x, y), " +
                          "rect (x, y, w, h); optional label and step number. A new annotate replaces the old drawing.",
            ParametersJson = T.Schema(
                T.Objects("shapes", "Shapes to draw, in order.", new[]
                {
                    T.OneOf("type", "Shape kind.", new[] { "ring", "arrow", "rect" }, true),
                    T.Num("x", "X (centre for ring, head for arrow, top-left for rect).", true),
                    T.Num("y", "Y (centre for ring, head for arrow, top-left for rect).", true),
                    T.Num("radius", "Ring radius in screenshot pixels."),
                    T.Num("from_x", "Arrow tail X."),
                    T.Num("from_y", "Arrow tail Y."),
                    T.Num("w", "Rectangle width."),
                    T.Num("h", "Rectangle height."),
                    T.Str("label", "Two to four words, e.g. 'Graphics settings'."),
                    T.Int("step", "Step number for a sequence, e.g. 1.")
                }, true),
                T.Num("duration", "Seconds to leave it on screen, 1-120. Default 9.")),
            Describe = a => "draw on the screen to show where something is",
            Handler = async (ctx, args) =>
            {
                if (!ctx.Services.Screenshot.HasShot)
                    return ToolResult.Fail("Take a screenshot first so the drawing lines up with what they see.");
                var refusal = ctx.Services.Screenshot.FocusRefusal(ctx.Services.DesktopActions.Version);
                if (refusal is not null) return ToolResult.Fail(refusal);
                var shapes = AnnotationParser.Parse(args, ctx.Services.Screenshot);
                if (shapes.Count == 0) return ToolResult.Fail("There were no usable shapes to draw.");
                var duration = Math.Clamp(Json.Num(args, "duration", 9), 1, 120);
                await ctx.Services.Annotations.ShowAsync(
                    new AnnotationRequest(shapes, TimeSpan.FromSeconds(duration), 0, 0, 1)).ConfigureAwait(false);
                return ToolResult.Done($"Drawing {shapes.Count} shape(s) on screen for {duration:0.#} seconds.");
            }
        };

        yield return new ToolDefinition
        {
            Name = "clear_annotations",
            Category = "screen",
            ReadOnly = false,
            Description = "Remove whatever annotate drew on the screen right now.",
            ParametersJson = T.Schema(),
            Handler = (ctx, _) =>
            {
                ctx.Services.Annotations.Clear();
                return Task.FromResult(ToolResult.Done("Cleared."));
            }
        };
    }
}

public static class AnnotationParser
{
    public static List<AnnotationShape> Parse(JsonElement args, ScreenshotState shot)
    {
        var list = new List<AnnotationShape>();
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("shapes", out var shapes) ||
            shapes.ValueKind != JsonValueKind.Array) return list;

        foreach (var s in shapes.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var type = Json.Str(s, "type").ToLowerInvariant();
            var x = Json.Num(s, "x");
            var y = Json.Num(s, "y");
            var label = Json.Str(s, "label");
            var step = s.TryGetProperty("step", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number
                ? stepEl.GetInt32() : (int?)null;

            switch (type)
            {
                case "ring":
                {
                    var (dx, dy) = shot.ToDesktop(x, y);
                    list.Add(new AnnotationShape("ring", dx, dy,
                        Radius: Json.Num(s, "radius", 60) * shot.Scale,
                        Label: string.IsNullOrWhiteSpace(label) ? null : label, Step: step));
                    break;
                }
                case "arrow":
                {
                    var (fx, fy) = shot.ToDesktop(Json.Num(s, "from_x", x), Json.Num(s, "from_y", y));
                    var (dx, dy) = shot.ToDesktop(x, y);
                    list.Add(new AnnotationShape("arrow", dx, dy, FromX: fx, FromY: fy,
                        Label: string.IsNullOrWhiteSpace(label) ? null : label, Step: step));
                    break;
                }
                case "rect":
                {
                    var (dx, dy) = shot.ToDesktop(x, y);
                    list.Add(new AnnotationShape("rect", dx, dy,
                        Width: Json.Num(s, "w", 200) * shot.Scale,
                        Height: Json.Num(s, "h", 80) * shot.Scale,
                        Label: string.IsNullOrWhiteSpace(label) ? null : label, Step: step));
                    break;
                }
            }
        }
        return list;
    }
}
