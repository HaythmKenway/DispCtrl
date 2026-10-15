using System.Globalization;
using DispCtrl.Linux.Placement;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>pin</c>: keep a window above the others (the window
    /// manager's own "always on top").</summary>
    private static int PinCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        using var x = XConnection.Open();
        if (x is null) return Refuse(c, NoDesktop());
        if (verbs is ["list"])
        {
            foreach (var w in Desktop.Windows(x).Where(w => w.Above))
                c.Out.WriteLine($"0x{w.Id:x8}  {w.AppClass}: {w.Title}");
            return Done;
        }
        if (verbs is ["off"] && flags.ContainsKey("--all"))
        {
            int n = 0;
            foreach (var w in Desktop.Windows(x).Where(w => w.Above)) { Desktop.SetState(x, w.Id, false, "_NET_WM_STATE_ABOVE"); n++; }
            x.Flush();
            c.Out.WriteLine(n == 0 ? "No window was pinned." : $"Unpinned {n} window(s).");
            return Done;
        }
        if (verbs is not ([] or ["on"] or ["off"] or ["toggle"])) return Wrong(c, "pin takes on, off, toggle or list, and --window <id>; pin off --all");
        if (WindowFor(x, flags, c) is not { } window) return Refused;
        bool on = verbs is [] or ["toggle"] ? !window.Above : verbs[0] == "on";
        Desktop.SetState(x, window.Id, on, "_NET_WM_STATE_ABOVE");
        x.Flush();
        c.Out.WriteLine($"{(on ? "Pinned on top" : "Unpinned")}: {window.AppClass}: {window.Title}");
        return Done;
    }

    /// <summary><c>placement</c>: gather every window onto one display, move one
    /// to another display, or span one across displays.</summary>
    private static int PlacementCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        using var x = XConnection.Open();
        if (x is null) return Refuse(c, NoDesktop());
        var monitors = Desktop.Monitors(x);
        if (monitors.Count == 0) return Refuse(c, "no monitor is lit");
        var ordered = WindowMoves.Ordered(monitors);

        switch (verbs)
        {
            case ["gather"]:
            {
                var to = MonitorFor(x, flags.GetValueOrDefault("--to") ?? "pointer", monitors, ordered, current: null, c);
                if (to is null) return AskedWrongly;
                int moved = 0;
                foreach (var w in Desktop.Windows(x, includeMinimized: false))
                {
                    var from = Desktop.MonitorOf(monitors, w.Frame);
                    if (from.Name == to.Name) continue;
                    MoveWindow(x, w, from, to);
                    moved++;
                }
                x.Flush();
                c.Out.WriteLine(moved == 0 ? $"Every window is already on {to.Name}." : $"Moved {moved} window(s) to {to.Name}.");
                return Done;
            }
            case ["move"]:
            {
                if (WindowFor(x, flags, c) is not { } w) return Refused;
                var from = Desktop.MonitorOf(monitors, w.Frame);
                var to = MonitorFor(x, flags.GetValueOrDefault("--to") ?? "next", monitors, ordered, from, c);
                if (to is null) return AskedWrongly;
                if (to.Name == from.Name) { c.Out.WriteLine($"Already on {to.Name}."); return Done; }
                MoveWindow(x, w, from, to);
                x.Flush();
                c.Out.WriteLine($"{w.AppClass}: {w.Title} -> {to.Name}");
                return Done;
            }
            case ["span"]:
            {
                if (WindowFor(x, flags, c) is not { } w) return Refused;
                List<MonitorInfo> over;
                if (flags.TryGetValue("--displays", out var list))
                {
                    over = [];
                    foreach (var part in list.Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > ordered.Count)
                            return Wrong(c, $"--displays takes display numbers from 1 to {ordered.Count}, such as 1,2");
                        over.Add(ordered[n - 1]);
                    }
                }
                else over = ordered;
                Desktop.Place(x, w.Id, WindowMoves.Span(over.Select(m => m.WorkArea)));
                x.Flush();
                c.Out.WriteLine($"{w.AppClass}: {w.Title} spans {string.Join(", ", over.Select(m => m.Name))}");
                return Done;
            }
            default:
                return Wrong(c, "placement takes gather [--to N|pointer|active], move [--window ID] [--to next|previous|N] or span [--window ID] [--displays 1,2]");
        }
    }

    private static void MoveWindow(XConnection x, AppWindow w, MonitorInfo from, MonitorInfo to)
    {
        var target = WindowMoves.Carry(w.Frame, from.WorkArea, to.WorkArea);
        Desktop.Place(x, w.Id, target);
        // A maximized or fullscreen window is that again on its new display.
        if (w.Fullscreen) Desktop.SetState(x, w.Id, true, "_NET_WM_STATE_FULLSCREEN");
        else if (w.Maximized) Desktop.SetState(x, w.Id, true, "_NET_WM_STATE_MAXIMIZED_VERT", "_NET_WM_STATE_MAXIMIZED_HORZ");
    }

    private static MonitorInfo? MonitorFor(XConnection x, string text, IReadOnlyList<MonitorInfo> monitors, List<MonitorInfo> ordered,
        MonitorInfo? current, CommandContext c)
    {
        switch (text)
        {
            case "pointer":
                var (px, py) = x.Pointer();
                return Desktop.MonitorAt(monitors, px, py);
            case "active":
                if (Desktop.Active(x) is { } id && Desktop.Describe(x, id) is { } w) return Desktop.MonitorOf(monitors, w.Frame);
                return Desktop.MonitorAt(monitors, x.Pointer().X, x.Pointer().Y);
            case "next" or "previous" when current is not null:
                return monitors[WindowMoves.Neighbour(monitors, current, text == "next" ? 1 : -1)];
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= ordered.Count) return ordered[n - 1];
        var named = monitors.FirstOrDefault(m => m.Name == text);
        if (named is null) Wrong(c, $"--to takes a display number from 1 to {ordered.Count}, a name ({string.Join(", ", monitors.Select(m => m.Name))}), next, previous, pointer or active");
        return named;
    }

    private static AppWindow? WindowFor(XConnection x, Dictionary<string, string> flags, CommandContext c)
    {
        if (flags.TryGetValue("--window", out var text))
        {
            if (!TryWindowId(text, out var parsed)) { Refuse(c, $"'{text}' is not a window id (windows lists them)"); return null; }
            var named = Desktop.Describe(x, parsed);
            if (named is null) Refuse(c, $"0x{parsed:x} is not an application window");
            return named;
        }
        // With the pointer last on the desktop, GNOME's desktop-icons window is the
        // active one and is no application window: the topmost real one is meant.
        var active = Desktop.Active(x) is { } id ? Desktop.Describe(x, id) : null;
        active ??= Desktop.Windows(x, includeMinimized: false).FirstOrDefault();
        if (active is null) Refuse(c, "no window is in front");
        return active;
    }

    /// <summary>Words first, then --flags with their values.</summary>
    private static bool TrySplit(string[] args, out string[] verbs, out Dictionary<string, string> flags, CommandContext c)
    {
        int first = Array.FindIndex(args, a => a.StartsWith("--", StringComparison.Ordinal));
        verbs = first < 0 ? args : args[..first];
        return TryFlags(first < 0 ? [] : args[first..], c, out flags);
    }
}
