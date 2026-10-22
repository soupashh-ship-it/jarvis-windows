using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Encoder = System.Drawing.Imaging.Encoder;

namespace Jarvis.Core;

public sealed record DisplayInfo(
    int Index, string Device, int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight, double Scale, bool Primary)
{
    public string Label => Primary ? "primary display" : $"display {Index + 1}";
}

public static class DisplayApi
{
    public static List<DisplayInfo> All()
    {
        var raw = new List<(Native.RECT bounds, Native.RECT work, string device, uint dpi, bool primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
            {
                var info = new Native.MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(),
                    szDevice = ""
                };
                if (Native.GetMonitorInfo(monitor, ref info))
                {
                    var dpi = 96u;
                    try
                    {
                        if (Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dx, out _) == 0) dpi = dx;
                    }
                    catch { }
                    raw.Add((info.rcMonitor, info.rcWork, info.szDevice,
                        dpi, (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);

        if (raw.Count == 0)
        {
            var w = Native.GetSystemMetrics(0);
            var h = Native.GetSystemMetrics(1);
            raw.Add((new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h },
                new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h }, "DISPLAY", 96, true));
        }

        return raw
            .OrderByDescending(r => r.primary)
            .ThenBy(r => r.bounds.Left)
            .ThenBy(r => r.bounds.Top)
            .Select((r, i) => new DisplayInfo(i, r.device, r.bounds.Left, r.bounds.Top,
                r.bounds.Width, r.bounds.Height, r.work.Left, r.work.Top, r.work.Width, r.work.Height,
                Math.Round(r.dpi / 96.0, 3), r.primary))
            .ToList();
    }

    public static DisplayInfo Primary() => All().FirstOrDefault(d => d.Primary) ?? All()[0];

    public static DisplayInfo? FromPoint(int x, int y)
    {
        var pt = new Native.POINT { X = x, Y = y };
        var h = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        foreach (var d in All())
        {
            if (x >= d.X && x < d.X + d.Width && y >= d.Y && y < d.Y + d.Height) return d;
            _ = h;
        }
        return null;
    }

    public static DisplayInfo ForWindow(nint hwnd)
    {
        var h = Native.MonitorFromWindow(hwnd, 2);
        var all = All();
        var rect = WindowApi.Bounds(hwnd);
        if (rect is not null)
        {
            var mid = new Native.POINT { X = rect.Value.Left + rect.Value.Width / 2, Y = rect.Value.Top + rect.Value.Height / 2 };
            var match = all.FirstOrDefault(d => mid.X >= d.X && mid.X < d.X + d.Width && mid.Y >= d.Y && mid.Y < d.Y + d.Height);
            if (match is not null) return match;
        }
        return all.FirstOrDefault() ?? new DisplayInfo(0, "DISPLAY", 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1, true);
    }

    public static DisplayInfo Resolve(string selector) => selector?.Trim().ToLowerInvariant() switch
    {
        "left" => All().OrderBy(d => d.X).First(),
        "right" => All().OrderBy(d => d.X).Last(),
        "secondary" => All().FirstOrDefault(d => !d.Primary) ?? Primary(),
        "active" => ForWindow(WindowApi.Active()?.Handle ?? IntPtr.Zero),
        "mouse" => FromPoint(InputApi.Cursor().X, InputApi.Cursor().Y) ?? Primary(),
        _ => Primary()
    };
}

public sealed record WindowInfo(
    nint Handle, string Title, string ClassName, int ProcessId, string ProcessName,
    Native.RECT Bounds, bool Minimized, bool Maximized, bool Active)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;
    public string Describe() =>
        $"{DisplayName} [{ProcessName}] at {Bounds.Left},{Bounds.Top} {Bounds.Width}x{Bounds.Height}" +
        (Minimized ? " (minimised)" : Maximized ? " (maximised)" : "");
}

public static class WindowApi
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow_Ignore", "IME", "MSCTFIME UI", "Default IME", "TaskListThumbnailWnd",
        "Windows.Internal.Shell.TabProxyWindow", "XamlExplorerHostIslandWindow"
    };

    private static readonly HashSet<string> IgnoredTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager", "Windows Input Experience", "Microsoft Text Input Application",
        "Setting", "Settings", "Search", "Start", "Action center", "Notification Center"
    };

    public static List<WindowInfo> All()
    {
        var result = new List<WindowInfo>();
        var self = Environment.ProcessId;
        var fg = Native.GetForegroundWindow();

        Native.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                var title = Title(hwnd);
                if (string.IsNullOrWhiteSpace(title) || IgnoredTitles.Contains(title)) return true;

                var cls = ClassName(hwnd);
                if (IgnoredClasses.Contains(cls)) return true;

                var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0)
                    return true;

                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == self) return true;

                var minimized = Native.IsIconic(hwnd);
                var rect = minimized ? NormalRect(hwnd) : (Native.GetWindowRect(hwnd, out var r) ? r : default);
                if (rect.Width <= 0 || rect.Height <= 0) return true;

                result.Add(new WindowInfo(hwnd, title, cls, (int)pid, ProcessName((int)pid), rect,
                    minimized, Native.IsZoomed(hwnd), hwnd == fg));
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result.OrderByDescending(w => w.Active).ThenBy(w => w.ProcessName).ToList();
    }

    private static Native.RECT NormalRect(nint hwnd)
    {
        var p = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        return Native.GetWindowPlacement(hwnd, ref p) ? p.rcNormalPosition : default;
    }

    public static Native.RECT? Bounds(nint hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (Native.IsIconic(hwnd)) { var r = NormalRect(hwnd); return r.Width > 0 ? r : null; }
        return Native.GetWindowRect(hwnd, out var rect) ? rect : null;
    }

    public static WindowInfo? Active()
    {
        var fg = Native.GetForegroundWindow();
        return All().FirstOrDefault(w => w.Handle == fg);
    }

    public static string Title(nint hwnd)
    {
        var len = Native.GetWindowTextLength(hwnd);
        var sb = new StringBuilder(Math.Max(len + 2, 8));
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    public static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static readonly Dictionary<int, string> ProcessNames = new();

    public static string ProcessName(int pid)
    {
        lock (ProcessNames)
        {
            if (ProcessNames.TryGetValue(pid, out var cached)) return cached;
        }
        var name = "?";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
            // Friendlier names for the things people actually ask for.
            name = name.ToLowerInvariant() switch
            {
                "chrome" => "Chrome", "msedge" => "Edge", "firefox" => "Firefox", "code" => "VS Code",
                "explorer" => "Explorer", "spotify" => "Spotify", "discord" => "Discord", "steam" => "Steam",
                "notepad" => "Notepad", "notepad++" => "Notepad++", "windowsterminal" => "Terminal",
                "wt" => "Terminal", "powershell" => "PowerShell", "pwsh" => "PowerShell",
                "acrobat" => "Acrobat", "outlook" => "Outlook", "teams" => "Teams", "slack" => "Slack",
                _ => char.ToUpperInvariant(name[0]) + name[1..]
            };
        }
        catch { }
        lock (ProcessNames) ProcessNames[pid] = name;
        return name;
    }

    public static List<WindowInfo> Find(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return new List<WindowInfo>();
        return All()
            .Select(w => (w, score: Score(w, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.w)
            .ToList();
    }

    private static int Score(WindowInfo w, string q)
    {
        var title = w.Title.ToLowerInvariant();
        var proc = w.ProcessName.ToLowerInvariant();
        if (proc == q) return 100;
        if (proc.StartsWith(q)) return 92;
        if (title == q) return 90;
        if (title.StartsWith(q)) return 80;
        if (proc.Contains(q)) return 70;
        if (title.Contains(q)) return 60;
        return 0;
    }

    public static bool Focus(WindowInfo window) => Focus(window.Handle);

    public static bool Focus(nint hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        try
        {
            if (Native.IsIconic(hwnd))
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Thread.Sleep(90);
            }

            var targetThread = Native.GetWindowThreadProcessId(hwnd, out _);
            var thisThread = Native.GetCurrentThreadId();
            var attached = targetThread != thisThread && Native.AttachThreadInput(thisThread, targetThread, true);
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(thisThread, targetThread, false);
            }

            Thread.Sleep(80);
            if (Native.GetForegroundWindow() == hwnd) return true;

            // Windows can refuse the first attempt when focus is being held by another app.
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            Native.SetForegroundWindow(hwnd);
            Thread.Sleep(60);
            return Native.GetForegroundWindow() == hwnd;
        }
        catch (Exception ex)
        {
            Log.Warn("focus failed", ex);
            return false;
        }
    }

    public static bool Act(string action, WindowInfo window)
    {
        var hwnd = window.Handle;
        var display = DisplayApi.ForWindow(hwnd);
        var (wx, wy, ww, wh) = (display.WorkX, display.WorkY, display.WorkWidth, display.WorkHeight);
        switch (action.Trim().ToLowerInvariant())
        {
            case "minimize": return Native.ShowWindow(hwnd, Native.SW_SHOWMINIMIZED);
            case "maximize": return Native.ShowWindow(hwnd, Native.SW_SHOWMAXIMIZED);
            case "restore": return Native.ShowWindow(hwnd, Native.SW_RESTORE);
            case "close": return Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            case "left":
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                return Native.MoveWindow(hwnd, wx, wy, ww / 2, wh, true);
            case "right":
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                return Native.MoveWindow(hwnd, wx + ww / 2, wy, ww - ww / 2, wh, true);
            case "center":
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                var cw = Math.Min(ww * 3 / 4, 1400);
                var ch = Math.Min(wh * 3 / 4, 900);
                return Native.MoveWindow(hwnd, wx + (ww - cw) / 2, wy + (wh - ch) / 2, cw, ch, true);
            case "top":
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                return Native.MoveWindow(hwnd, wx, wy, ww, wh / 2, true);
            case "bottom":
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                return Native.MoveWindow(hwnd, wx, wy + wh / 2, ww, wh - wh / 2, true);
            default: return false;
        }
    }
}

/// <summary>One screenshot plus the exact mapping from its pixels back to the desktop.</summary>
public sealed record CapturedImage(Bitmap Bitmap, int DesktopX, int DesktopY, double Scale, string Label, nint WindowHandle)
{
    public (int X, int Y) ToDesktop(double imageX, double imageY) =>
        ((int)Math.Round(DesktopX + imageX * Scale), (int)Math.Round(DesktopY + imageY * Scale));

    public Size DesktopSize => new((int)Math.Round(Bitmap.Width * Scale), (int)Math.Round(Bitmap.Height * Scale));
}

public static class ScreenCapture
{
    /// <summary>The app hides its own HUD and drawings before a capture so the model never sees them.</summary>
    public static Func<Task>? BeforeCapture;
    public static Func<Task>? AfterCapture;

    public static async Task<CapturedImage?> CaptureAsync(string target, double maxScale = 1.0)
    {
        if (BeforeCapture is not null) await BeforeCapture().ConfigureAwait(false);
        try
        {
            return await CaptureCoreAsync(target, maxScale).ConfigureAwait(false);
        }
        finally
        {
            if (AfterCapture is not null)
            {
                try { await AfterCapture().ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task<CapturedImage?> CaptureCoreAsync(string target, double maxScale)
    {
        var t = (target ?? "primary").Trim().ToLowerInvariant();
        if (t is "window" or "active_window" or "active")
        {
            var active = WindowApi.Active();
            if (active is not null)
            {
                var shot = CaptureWindow(active.Handle);
                if (shot is not null)
                {
                    var r = WindowApi.Bounds(active.Handle) ?? new Native.RECT();
                    return new CapturedImage(shot, r.Left, r.Top, 1.0,
                        $"the active window '{active.Title}'", active.Handle);
                }
            }
            t = "primary";
        }

        var displays = t switch
        {
            "all" or "both" => DisplayApi.All(),
            "secondary" or "right" => new List<DisplayInfo> { DisplayApi.Resolve("secondary") },
            "left" => new List<DisplayInfo> { DisplayApi.Resolve("left") },
            _ => new List<DisplayInfo> { DisplayApi.Primary() }
        };

        var x = displays.Min(d => d.X);
        var y = displays.Min(d => d.Y);
        var right = displays.Max(d => d.X + d.Width);
        var bottom = displays.Max(d => d.Y + d.Height);
        var width = right - x;
        var height = bottom - y;

        try
        {
            using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            var label = t switch
            {
                "all" or "both" => "all displays",
                "secondary" or "right" => "the second display",
                "left" => "the left display",
                _ => "the primary display"
            };

            // Cap the long edge so vision tokens stay sane and coordinates stay easy to reason about.
            var longEdge = Math.Max(width, height);
            var scale = Math.Max(1.0, longEdge / 1920.0) * maxScale;
            Bitmap output = full;
            if (scale > 1.02)
            {
                var nw = Math.Max(1, (int)Math.Round(width / scale));
                var nh = Math.Max(1, (int)Math.Round(height / scale));
                output = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(output);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(full, 0, 0, nw, nh);
            }
            else
            {
                output = (Bitmap)full.Clone();
                scale = 1.0;
            }
            return new CapturedImage(output, x, y, scale, label, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("screenshot failed", ex);
            return null;
        }
    }

    public static Bitmap? CaptureWindow(nint hwnd)
    {
        try
        {
            var rect = WindowApi.Bounds(hwnd);
            if (rect is null) return null;
            var w = rect.Value.Width;
            var h = rect.Value.Height;
            if (w <= 0 || h <= 0) return null;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                var ok = Native.PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
                if (!ok)
                {
                    g.CopyFromScreen(rect.Value.Left, rect.Value.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
            }
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn("window capture failed", ex);
            return null;
        }
    }

    public static string SaveJpeg(Bitmap bitmap, string directory, out string fileName, long quality = 82)
    {
        Directory.CreateDirectory(directory);
        fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg";
        var path = Path.Combine(directory, fileName);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(path, codec, parameters);
        Prune(directory, 300);
        return path;
    }

    public static byte[] EncodeJpeg(Bitmap bitmap, long quality = 82)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    public static string SaveBytes(byte[] jpeg, string directory)
    {
        Directory.CreateDirectory(directory);
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg";
        File.WriteAllBytes(Path.Combine(directory, fileName), jpeg);
        Prune(directory, 300);
        return fileName;
    }

    private static void Prune(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*.jpg").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(keep)) File.Delete(old);
        }
        catch { }
    }
}

public static class ClipboardApi
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static string GetText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Native.OpenClipboard(IntPtr.Zero)) { Thread.Sleep(40); continue; }
            try
            {
                var handle = Native.GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                var ptr = Native.GlobalLock(handle);
                if (ptr == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(ptr) ?? ""; }
                finally { Native.GlobalUnlock(handle); }
            }
            catch { return ""; }
            finally { Native.CloseClipboard(); }
        }
        return "";
    }

    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Native.OpenClipboard(IntPtr.Zero)) { Thread.Sleep(40); continue; }
            try
            {
                Native.EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var mem = Native.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
                if (mem == IntPtr.Zero) return false;
                var ptr = Native.GlobalLock(mem);
                if (ptr == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                    Marshal.WriteInt16(ptr, text.Length * 2, 0);
                }
                finally { Native.GlobalUnlock(mem); }
                return Native.SetClipboardData(CF_UNICODETEXT, mem) != IntPtr.Zero;
            }
            catch { return false; }
            finally { Native.CloseClipboard(); }
        }
        return false;
    }
}

public static class SystemProbe
{
    private static ulong _prevIdle, _prevKernel, _prevUser;
    private static DateTime _prevAt = DateTime.MinValue;
    private static readonly object Gate = new();

    public static double CpuPercent()
    {
        lock (Gate)
        {
            if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            var i = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
            var k = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
            var u = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;
            var now = DateTime.UtcNow;

            double cpu = 0;
            if (_prevAt != DateTime.MinValue && (now - _prevAt).TotalMilliseconds > 50)
            {
                var idleDelta = (double)(i - _prevIdle);
                var totalDelta = (double)((k - _prevKernel) + (u - _prevUser));
                if (totalDelta > 0) cpu = Math.Clamp(100.0 * (1.0 - idleDelta / totalDelta), 0, 100);
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u; _prevAt = now;
            return Math.Round(cpu, 1);
        }
    }

    public static (double UsedGb, double TotalGb, double Percent) Memory()
    {
        var s = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref s)) return (0, 0, 0);
        var total = s.ullTotalPhys / 1024.0 / 1024 / 1024;
        var used = (s.ullTotalPhys - s.ullAvailPhys) / 1024.0 / 1024 / 1024;
        return (Math.Round(used, 1), Math.Round(total, 1), Math.Round(100.0 * used / Math.Max(total, 1), 0));
    }

    public static (bool HasBattery, int Percent, bool Charging) Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return (false, 0, false);
        var has = s.BatteryFlag != 128 && s.BatteryLifePercent != 255;
        return (has, has ? s.BatteryLifePercent : 0, s.ACLineStatus == 1);
    }

    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static TimeSpan IdleTime()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var idle = Environment.TickCount64 - info.dwTime;
        return TimeSpan.FromMilliseconds(Math.Max(0, idle));
    }

    public static string Summary()
    {
        var mem = Memory();
        var bat = Battery();
        var discs = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                discs.Add($"{d.Name.TrimEnd('\\')} {d.AvailableFreeSpace / 1024.0 / 1024 / 1024:0} GB free of " +
                          $"{d.TotalSize / 1024.0 / 1024 / 1024:0} GB");
            }
            catch { }
        }
        var battery = bat.HasBattery ? $"Battery {bat.Percent}% {(bat.Charging ? "charging" : "on battery")}. " : "";
        return $"CPU {CpuPercent():0}%, memory {mem.UsedGb:0.0} of {mem.TotalGb:0.0} GB used ({mem.Percent:0}%). " +
               $"Uptime {Uptime().Days}d {Uptime().Hours}h. {battery}{string.Join("; ", discs)}";
    }
}
