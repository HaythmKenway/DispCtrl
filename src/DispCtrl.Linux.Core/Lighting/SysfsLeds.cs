using System.Globalization;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Lighting;

/// <summary>One keyboard backlight the kernel's LED class exposes.</summary>
/// <param name="Name">The folder's name under /sys/class/leds, such as asus::kbd_backlight.</param>
/// <param name="Max">Its own top level; laptops here have 3, others 255.</param>
/// <param name="Colour">Whether it takes a colour (multi_intensity), or only a level.</param>
public sealed record KeyboardLed(string Name, int Current, int Max, bool Colour)
{
    /// <summary>What a person is told about it, so a level-only backlight is not mistaken for an RGB one.</summary>
    public string Describe() => Colour
        ? $"{Name} (kernel LED, brightness {Current} of {Max}, colour)"
        : $"{Name} (kernel LED, level {Current} of {Max}, one colour: only its brightness can change)";
}

/// <summary>The basic lighting path with no OpenRGB: laptop keyboard backlights
/// in /sys/class/leds, which need no daemon, download or udev rules to read.</summary>
/// <remarks>
/// Writing goes to sysfs when this account may (root, or a udev rule) and
/// otherwise through logind's Session.SetBrightness for the "leds" subsystem,
/// as <see cref="Backlight"/> does for panels. logind carries a level only, so
/// a colour that sysfs refuses is reported rather than half applied. A level
/// is never written as 0 unless the person asked for off: unlike a panel, a
/// dark keyboard backlight is a plain "off", but a colour request must not
/// produce one by accident.
/// </remarks>
public static class SysfsLeds
{
    /// <summary>Overridable so the checks can point it at a fake tree.</summary>
    public static string Root { get; set; } = "/sys/class/leds";

    public static IReadOnlyList<KeyboardLed> Enumerate()
    {
        if (!Directory.Exists(Root)) return [];
        var found = new List<KeyboardLed>();
        foreach (var dir in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            if (!name.Contains("kbd_backlight", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryReadInt(Path.Combine(dir, "brightness"), out int current) || !TryReadInt(Path.Combine(dir, "max_brightness"), out int max) || max < 1) continue;
            bool colour = File.Exists(Path.Combine(dir, "multi_intensity")) && File.Exists(Path.Combine(dir, "multi_index"));
            found.Add(new KeyboardLed(name, current, max, colour));
        }
        return found;
    }

    /// <summary>Applies "#RRGGBB", "off" or "mode:NAME" the way these LEDs can.
    /// Problems stop it; notes say what was done instead of what was asked.</summary>
    public static void Apply(string what, int? device, List<string> problems, List<string> notes)
    {
        var leds = Enumerate();
        var chosen = leds.Where((_, i) => device is null || i == device).ToList();
        if (chosen.Count == 0) { problems.Add(device is null ? "no keyboard backlight found" : $"no device {device + 1}"); return; }
        foreach (var led in chosen)
        {
            if (what.StartsWith("mode:", StringComparison.OrdinalIgnoreCase)) { problems.Add($"{led.Name} has no effects: only OpenRGB offers them (dispctrl-linux rgb setup)"); continue; }
            if (!LightingControl.TryColour(what, out var c)) { problems.Add($"'{what}' is not a colour or an effect"); continue; }
            bool off = c is { R: 0, G: 0, B: 0 };
            string? error = null;
            if (off)
            {
                TryWrite(led, "brightness", 0, out error);
            }
            else if (led.Colour)
            {
                if (TryWriteColour(led, c, out error) && led.Current == 0) TryWrite(led, "brightness", led.Max, out error);
            }
            else
            {
                // A level-only backlight cannot show red; say so, and light it if it is dark.
                notes.Add($"{led.Name}: this keyboard backlight has one colour, so only its brightness changes");
                if (led.Current == 0) TryWrite(led, "brightness", led.Max, out error);
            }
            if (error is not null) problems.Add($"{led.Name}: {error}");
        }
    }

    private static bool TryWriteColour(KeyboardLed led, (byte R, byte G, byte B) c, out string? error)
    {
        var dir = Path.Combine(Root, led.Name);
        var order = File.ReadAllText(Path.Combine(dir, "multi_index")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = order.Select(channel => channel.ToLowerInvariant() switch
        {
            "red" => c.R, "green" => c.G, "blue" => c.B, _ => 0,
        }).Select(v => ((int)Math.Round(v / 255.0 * led.Max)).ToString(CultureInfo.InvariantCulture));
        try
        {
            File.WriteAllText(Path.Combine(dir, "multi_intensity"), string.Join(' ', values));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = "the colour needs write access to multi_intensity (install OpenRGB with dispctrl-linux rgb setup, or add a udev rule for it)";
            return false;
        }
    }

    private static bool TryWrite(KeyboardLed led, string file, int value, out string? error)
    {
        var path = Path.Combine(Root, led.Name, file);
        try
        {
            File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
            error = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            if (TrySetThroughLogind(led.Name, value, out var logind)) { error = null; return true; }
            error = $"permission denied writing {path}, and logind refused too ({logind})";
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetThroughLogind(string name, int value, out string? error)
    {
        foreach (var (tool, args) in LogindCalls(name, value.ToString(CultureInfo.InvariantCulture)))
        {
            if (!Shell.TryWhich(tool)) continue;
            var result = Shell.Run(tool, args, timeoutMs: 5000);
            error = result.Ok ? null : result.Stderr.Trim();
            return result.Ok;
        }
        error = "no D-Bus command found (busctl, dbus-send or gdbus)";
        return false;
    }

    internal static IEnumerable<(string Tool, string[] Args)> LogindCalls(string name, string level)
    {
        const string Dest = "org.freedesktop.login1", Session = "/org/freedesktop/login1/session/auto";
        yield return ("busctl", ["call", Dest, Session, "org.freedesktop.login1.Session", "SetBrightness", "ssu", "leds", name, level]);
        yield return ("dbus-send", ["--system", "--print-reply", $"--dest={Dest}", Session,
            "org.freedesktop.login1.Session.SetBrightness", "string:leds", $"string:{name}", $"uint32:{level}"]);
        yield return ("gdbus", ["call", "--system", "--dest", Dest, "--object-path", Session,
            "--method", "org.freedesktop.login1.Session.SetBrightness", "'leds'", $"'{name}'", $"uint32 {level}"]);
    }

    private static bool TryReadInt(string path, out int value)
    {
        value = 0;
        try { return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
