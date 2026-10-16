using System.Globalization;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary>Inside the engine the monitors found are kept for a minute, so
    /// a held brightness key is not a ddcutil detect per press.</summary>
    private static IReadOnlyList<BrightnessTarget> Targets(CommandContext c) =>
        BrightnessTargets.List(c.InEngine ? TimeSpan.FromMinutes(1) : null);

    /// <summary><c>unison</c>: one level for every display, each within its own
    /// range, as the Windows app's unison slider.</summary>
    private static int UnisonCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["status"]:
            {
                var u = SettingsStore.Load().Unison;
                c.Out.WriteLine($"unison       {(u.Enabled ? "on" : "off")}, level {u.Level}{(UnisonCalibration.IsActive ? " (calibrating)" : "")}");
                foreach (var t in Targets(c))
                {
                    var r = u.RangeOf(t.Key);
                    bool excluded = u.Excluded.Contains(t.Key);
                    c.Out.WriteLine($"  {t.Label,-20} {(excluded ? "left out" : $"{r.Min}-{r.Max}%")}  {t.Key}");
                }
                return Done;
            }
            case ["on" or "off" or "true" or "false"]:
                bool on = args[0] is "on" or "true";
                SettingsStore.Update(s => s.Unison.Enabled = on);
                c.Out.WriteLine(on ? "Unison on: brightness keys and the tray's wheel move every display together." : "Unison off.");
                return Done;
            case ["calibrate", .. var calibrate]:
                return CalibrateCommand(calibrate, c);
            case ["up" or "down", .. var rest]:
            {
                if (!TryStep(rest, out int step)) return Wrong(c, "unison up|down takes a step from 1 to 50");
                int level = SettingsStore.Load().Unison.Level + (args[0] == "up" ? step : -step);
                return SetUnison(Math.Clamp(level, 0, 100), c);
            }
            case ["range", var key, var min, var max]:
            {
                if (!int.TryParse(min, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
                    || !int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi) || lo is < 0 or > 100 || hi < lo || hi > 100)
                    return Wrong(c, "unison range takes a display and its lowest and highest, from 0 to 100, lowest first");
                if (FindTarget(key, c) is not { } t) return Wrong(c, NoTarget(key));
                SettingsStore.Update(s => s.Unison.Ranges[t.Key] = new UnisonRange { Min = lo, Max = hi });
                c.Out.WriteLine($"{t.Label}: {lo}% to {hi}% in unison.");
                return Done;
            }
            case ["include" or "exclude", var key]:
            {
                if (FindTarget(key, c) is not { } t) return Wrong(c, NoTarget(key));
                bool include = args[0] == "include";
                SettingsStore.Update(s => { s.Unison.Excluded.Remove(t.Key); if (!include) s.Unison.Excluded.Add(t.Key); });
                c.Out.WriteLine(include ? $"{t.Label} moves with unison." : $"{t.Label} is left out of unison.");
                return Done;
            }
            case [var value] when int.TryParse(value.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level):
                if (level is < 0 or > 100) return Wrong(c, "unison takes a level from 0 to 100");
                return SetUnison(level, c);
            default:
                return Wrong(c, "unison takes on, off, a level from 0 to 100, up|down [step], range DISPLAY MIN MAX, calibrate start|capture|cancel or include|exclude DISPLAY");
        }
    }

    /// <summary>Room light's level, written as unison's.</summary>
    public static void SetUnisonFromRoom(int level, CommandContext c) => SetUnison(level, c);

    /// <summary>Every display not left out, each to its share of
    /// <paramref name="level"/> within its range.</summary>
    internal static int SetUnison(int level, CommandContext c)
    {
        if (UnisonCalibration.IsActive) return Refuse(c, "unison is paused for calibration: unison calibrate capture, or cancel");
        var u = SettingsStore.Update(s => s.Unison.Level = level).Unison;
        var failures = new List<string>();
        int written = 0;
        foreach (var t in Targets(c))
        {
            if (u.Excluded.Contains(t.Key)) continue;
            var r = u.RangeOf(t.Key);
            if (t.Write(BrightnessTargets.Scale(level, r.Min, r.Max)) is { } error) failures.Add(error);
            else written++;
        }
        foreach (var f in failures) c.Err.WriteLine($"dispctrl-linux: {f}");
        if (written == 0 && failures.Count == 0)
            return Refuse(c, Distro.InSnap
                ? "no display's own brightness can be set here (the snap reaches the laptop's backlight only)"
                : "no display's own brightness can be set here: no backlight, and no monitor answered over DDC/CI");
        c.Out.WriteLine($"unison {level}%");
        return failures.Count == 0 ? Done : Refused;
    }

    /// <summary><c>brightness up|down [step]</c>, and <c>brightness N --all</c>:
    /// every display's own brightness, through unison when it is on.</summary>
    private static int BrightnessAll(string[] args, CommandContext c)
    {
        if (args[0] is "up" or "down")
        {
            if (!TryStep(args[1..], out int step)) return Wrong(c, "brightness up|down takes a step from 1 to 50");
            int sign = args[0] == "up" ? 1 : -1;
            var settings = SettingsStore.Load();
            if (settings.Unison.Enabled) return SetUnison(Math.Clamp(settings.Unison.Level + sign * step, 0, 100), c);
            var failures = new List<string>();
            var levels = new List<string>();
            foreach (var t in Targets(c))
            {
                if (t.Read() is not int now) continue;
                int next = Math.Clamp(now + sign * step, 0, 100);
                if (t.Write(next) is { } e) failures.Add(e);
                else levels.Add($"{t.Label} {next}%");
            }
            foreach (var f in failures) c.Err.WriteLine($"dispctrl-linux: {f}");
            if (levels.Count == 0 && failures.Count == 0) return Refuse(c, "no display's own brightness can be set here");
            c.Out.WriteLine(string.Join(", ", levels));
            return failures.Count == 0 ? Done : Refused;
        }
        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) || level is < 0 or > 100)
            return Wrong(c, "brightness must be a whole number from 0 to 100");
        var errors = Targets(c).Select(t => t.Write(level)).OfType<string>().ToList();
        foreach (var e in errors) c.Err.WriteLine($"dispctrl-linux: {e}");
        c.Out.WriteLine($"every display {level}%");
        return errors.Count == 0 ? Done : Refused;
    }

    private static bool TryStep(string[] rest, out int step)
    {
        step = 5;
        if (rest.Length == 0) return true;
        return rest.Length == 1 && int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) && step is >= 1 and <= 50;
    }

    /// <summary>A display by its key, its whole label, or a part of its label
    /// that only it has ("DELL", "built-in").</summary>
    private static BrightnessTarget? FindTarget(string key, CommandContext c)
    {
        var all = Targets(c);
        var exact = all.FirstOrDefault(t => t.Key == key || t.Key.EndsWith(":" + key, StringComparison.Ordinal)
            || t.Label.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var partial = all.Where(t => t.Label.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    private static string NoTarget(string key) => $"no display '{key}' (unison lists them, with their keys)";
}
