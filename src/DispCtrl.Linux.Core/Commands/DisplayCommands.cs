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
                return Wrong(c, "display takes list, set --output <name> [--x <n> --y <n>] [--primary true], arrange <output>=<x>,<y> ... or arrange reset");
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
        if (!hasX && !hasY && primary is null) return Wrong(c, "display set needs --x and --y, or --primary true");

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
