using System.Globalization;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>display</c>: the arrangement and the main display, spelled
    /// as <c>dispctrl.exe display set</c> spells them, with outputs named as
    /// XRandR names them.</summary>
    private static int DisplayCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["list"]:
                return DisplayList(c);
            case ["set", .. var rest]:
                return DisplaySet(rest, c);
            case ["modes", .. var rest]:
                return DisplayModes(rest, c);
            case ["project", var how]:
                return Project(how, c);
            case ["only", var output]:
                return Project("only", c, output);
            case ["arrange", "reset"]:
                if (Unreachable() is { } why) return Refuse(c, why);
                var outputs = XRandR.Query();
                return ApplyLayout(DesktopLayout.SideBySide(outputs), null, c);
            case ["arrange", .. var places] when places.Length > 0:
                var moves = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
                foreach (var place in places)
                {
                    if (!TryPlace(place, out var name, out var at))
                        return Wrong(c, $"'{place}' is not a place: write it as <output>=<x>,<y>, e.g. DP-1=0,0");
                    moves[name] = at;
                }
                return ApplyLayout(moves, null, c);
            default:
                return Wrong(c, "display takes list, modes, set, project extend|duplicate|internal|external, only <output>, arrange <output>=<x>,<y> ... or arrange reset");
        }
    }

    /// <summary>Why the layout cannot be read or moved here; null when it can.
    /// Asked after the arguments, so a mistyped command says so first.</summary>
    private static string? Unreachable()
    {
        if (GammaRamp.OnWayland) return "a Wayland session arranges its displays itself: use its own display settings";
        return GammaRamp.IsAvailable(out var why) ? null : $"cannot reach the displays: {why}";
    }

    private static int DisplayList(CommandContext c)
    {
        if (Unreachable() is { } why) return Refuse(c, why);
        var outputs = XRandR.Query();
        if (outputs.Count == 0) return Refuse(c, "RandR reported no outputs");
        var names = XRandR.MonitorNames();
        foreach (var o in outputs.Where(o => o.Connected))
        {
            if (!o.Active)
            {
                c.Out.WriteLine($"{o.Name,-10} off");
                continue;
            }
            string rate = o.RefreshHz > 0 ? string.Create(CultureInfo.InvariantCulture, $" @ {o.RefreshHz:0.##} Hz") : "";
            string turned = o.Rotation == 0 ? "" : $", turned {o.Rotation}°";
            string size = o.WidthMm > 0 ? $"  {o.WidthMm} x {o.HeightMm} mm" : "";
            string model = names.TryGetValue(o.Name, out var n) ? $"  {n}" : "";
            c.Out.WriteLine($"{o.Name,-10} {(o.Primary ? "main" : "    ")}  {o.WidthPx}x{o.HeightPx}{rate} at {o.PosX},{o.PosY}{turned}{size}{model}");
        }
        return Done;
    }

    private static int DisplaySet(string[] args, CommandContext c)
    {
        if (!TryFlags(args, c, out var flags)) return AskedWrongly;
        if (!flags.TryGetValue("--output", out var name)) return Wrong(c, "display set needs --output <name> (display list names them)");

        bool hasX = flags.TryGetValue("--x", out var xText), hasY = flags.TryGetValue("--y", out var yText);
        bool? primary = null;
        if (flags.TryGetValue("--primary", out var p))
        {
            if (!TryOnOff(p, out bool on)) return Wrong(c, "--primary takes true or false");
            if (!on) return Wrong(c, "a display stops being the main one when another becomes it: --primary true on that one");
            primary = true;
        }
        bool hasMode = flags.TryGetValue("--mode", out var modeText);
        bool hasRate = flags.TryGetValue("--rate", out var rateText);
        bool hasTurn = flags.TryGetValue("--rotate", out var turnText);
        bool hasEnabled = flags.TryGetValue("--enabled", out var enabledText);
        if (hasMode || hasRate || hasTurn || hasEnabled)
        {
            if (hasX || hasY || primary is not null) return Wrong(c, "move a display or make it the main one in a separate display set from its mode");
            return DisplaySetMode(name, hasMode ? modeText : null, hasRate ? rateText : null, hasTurn ? turnText : null, hasEnabled ? enabledText : null, c);
        }
        if (!hasX && !hasY && primary is null) return Wrong(c, "display set needs --x and --y, --primary true, --mode, --rate, --rotate or --enabled");

        var moves = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        if (hasX || hasY)
        {
            var current = XRandR.Query().FirstOrDefault(o => o.Name == name);
            if (!TryInt(xText, current?.PosX, out int x) || !TryInt(yText, current?.PosY, out int y))
                return Wrong(c, "--x and --y must be whole numbers of pixels");
            moves[name] = (x, y);
        }
        return ApplyLayout(moves, primary == true ? name : null, c);

        static bool TryInt(string? text, int? fallback, out int value)
        {
            if (text is null) { value = fallback ?? 0; return true; }
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    private static int DisplayModes(string[] args, CommandContext c)
    {
        if (!TryFlags(args, c, out var flags)) return AskedWrongly;
        if (Unreachable() is { } why) return Refuse(c, why);
        var all = XRandR.Modes();
        var names = flags.TryGetValue("--output", out var only) ? [only] : all.Keys.ToList();
        foreach (var name in names)
        {
            if (!all.TryGetValue(name, out var modes)) return Wrong(c, $"no connected output '{name}'");
            var current = XRandR.Query().FirstOrDefault(o => o.Name == name);
            c.Out.WriteLine(name);
            foreach (var size in modes.GroupBy(m => (m.Width, m.Height)))
            {
                var rates = string.Join(", ", size.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.RefreshHz:0.##}")).Distinct());
                bool now = current is { Active: true } o && (o.Rotation is 90 or 270 ? (o.HeightPx, o.WidthPx) : (o.WidthPx, o.HeightPx)) == size.Key;
                c.Out.WriteLine($"  {(now ? "*" : " ")} {size.Key.Width}x{size.Key.Height}  {rates} Hz{(size.Any(m => m.Preferred) ? "  (preferred)" : "")}");
            }
        }
        return Done;
    }

    /// <summary>A display's resolution, refresh rate, turn, or whether it is lit.</summary>
    private static int DisplaySetMode(string name, string? modeText, string? rateText, string? turnText, string? enabledText, CommandContext c)
    {
        int width = 0, height = 0, rotation = 0;
        double hz = 0;
        if (modeText is not null && !TrySize(modeText, out width, out height)) return Wrong(c, "--mode takes a size such as 1920x1080");
        if (rateText is not null && (!double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out hz) || hz <= 0))
            return Wrong(c, "--rate takes a refresh rate in hertz, such as 60 or 143.98");
        if (turnText is not null)
        {
            int? turn = turnText switch { "normal" or "0" => 0, "left" or "90" => 90, "inverted" or "180" => 180, "right" or "270" => 270, _ => null };
            if (turn is null) return Wrong(c, "--rotate takes normal, left, right or inverted");
            rotation = turn.Value;
        }
        bool? enabled = null;
        if (enabledText is not null)
        {
            if (!TryOnOff(enabledText, out bool e)) return Wrong(c, "--enabled takes true or false");
            enabled = e;
        }
        if (Unreachable() is { } why) return Refuse(c, why);

        var outputs = XRandR.Query();
        var modes = XRandR.Modes();
        var target = outputs.FirstOrDefault(o => o.Name == name && o.Connected);
        if (target is null) return Wrong(c, $"no connected output '{name}' (display list names them)");

        List<OutputConfig> plan;
        if (enabled == false)
        {
            if (!target.Active) { c.Out.WriteLine($"{name} is already off"); return Done; }
            if (outputs.Count(o => o.Active) < 2) return Refuse(c, "it is the only display lit");
            plan = [new OutputConfig(name, false)];
        }
        else if (!target.Active)
        {
            // Lit again on the right of the desk, in its preferred mode unless asked.
            var list = modes.GetValueOrDefault(name) ?? [];
            var pick = (modeText is not null ? list.FirstOrDefault(m => m.Width == width && m.Height == height) : null)
                ?? list.FirstOrDefault(m => m.Preferred) ?? list.FirstOrDefault();
            if (pick is null) return Refuse(c, $"{name} offers no mode");
            int right = outputs.Where(o => o.Active).Select(o => o.PosX + o.WidthPx).DefaultIfEmpty(0).Max();
            plan = [new OutputConfig(name, true, pick.Width, pick.Height, hz > 0 ? hz : pick.RefreshHz, right, 0, rotation)];
        }
        else
        {
            bool turned = target.Rotation is 90 or 270;
            int w = modeText is not null ? width : turned ? target.HeightPx : target.WidthPx;
            int h = modeText is not null ? height : turned ? target.WidthPx : target.HeightPx;
            int turn = turnText is not null ? rotation : target.Rotation;
            double rate = hz > 0 ? hz : modeText is not null ? 0 : target.RefreshHz;
            if (!(modes.GetValueOrDefault(name) ?? []).Any(m => m.Width == w && m.Height == h))
                return Wrong(c, $"{name} offers no {w}x{h} mode (display modes --output {name} lists them)");
            if (DisplayPlans.ChangeMode(name, w, h, rate, turn, outputs, out plan) is { } refused) return Refuse(c, refused);
        }
        return Configure(plan, null, c);
    }

    /// <summary>Win+P: extend, duplicate, only the built-in panel, only the
    /// others; or only one named output.</summary>
    private static int Project(string how, CommandContext c, string? output = null)
    {
        if (how != "only" && !DisplayPlans.Projections.Contains(how))
            return Wrong(c, "display project takes extend, duplicate, internal or external");
        if (Unreachable() is { } why) return Refuse(c, why);
        var outputs = XRandR.Query();
        var modes = XRandR.Modes();
        List<OutputConfig> plan;
        string? refused = how switch
        {
            "extend" => Set(DisplayPlans.Extend(outputs, modes), out plan),
            "duplicate" => DisplayPlans.Duplicate(outputs, modes, out plan),
            "internal" => DisplayPlans.Only(true, outputs, modes, out plan),
            "external" => DisplayPlans.Only(false, outputs, modes, out plan),
            _ => DisplayPlans.OnlyOne(output!, outputs, modes, out plan),
        };
        if (refused is not null) return Refuse(c, refused);
        return Configure(plan, null, c);

        static string? Set(List<OutputConfig> made, out List<OutputConfig> plan) { plan = made; return null; }
    }

    private static int Configure(IReadOnlyList<OutputConfig> plan, string? primary, CommandContext c)
    {
        var changes = DisplayPlans.Changes(plan, XRandR.Query());
        if (changes.Count == 0 && primary is null) return DisplayList(c);
        if (!XRandR.Configure(changes, primary, out var error)) return Refuse(c, $"not changed: {error}");
        // Some drivers take a moment to light a CRTC; the list should show the result.
        Thread.Sleep(300);
        return DisplayList(c);
    }

    internal static bool TrySize(string text, out int width, out int height)
    {
        width = height = 0;
        var parts = text.ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
            && width > 0 && height > 0;
    }

    private static int ApplyLayout(IReadOnlyDictionary<string, (int X, int Y)> moves, string? primary, CommandContext c)
    {
        if (Unreachable() is { } why) return Refuse(c, why);
        if (!DesktopLayout.Apply(moves, primary, out var error)) return Refuse(c, $"not arranged: {error}");
        return DisplayList(c);
    }

    private static bool TryPlace(string text, out string name, out (int X, int Y) at)
    {
        name = "";
        at = default;
        int equals = text.LastIndexOf('=');
        if (equals <= 0) return false;
        name = text[..equals];
        var parts = text[(equals + 1)..].Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return false;
        at = (x, y);
        return true;
    }
}
