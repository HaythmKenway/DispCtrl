using System.Text.Json;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

/// <summary>The unison calibration walkthrough, as the Windows app runs it: every
/// display is taken to the dimmest it should go, the person matches them by eye
/// and captures, then the same for the brightest. The two captures become each
/// display's range.</summary>
/// <remarks>
/// A command line run ends with the command, so the step and where the desk was
/// are kept in a file beside the settings (the Windows app holds them in the
/// window, with an event for the engine). Cancelling restores the levels and
/// the unison slider; a step left behind by a crash is ended with
/// <c>unison calibrate cancel</c>. While it is open, unison refuses to move the
/// displays, so room light and the brightness keys cannot fight the walkthrough.
/// </remarks>
public sealed class UnisonCalibration
{
    public const string Lower = "lower", Upper = "upper";

    public string Step { get; set; } = Lower;
    public int PreviousLevel { get; set; }
    public Dictionary<string, int> Before { get; set; } = new(StringComparer.Ordinal);

    private static string FilePath => Path.Combine(SettingsStore.Directory, "unison-calibration.json");

    public static bool IsActive => File.Exists(FilePath);

    public static UnisonCalibration? Load()
    {
        try { return JsonSerializer.Deserialize<UnisonCalibration>(File.ReadAllText(FilePath)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(SettingsStore.Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, true);
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch (IOException) { }
    }

    /// <summary>The range two captures make, or the reason they do not: the
    /// brightest must be above the dimmest.</summary>
    public static (UnisonRange? Range, string? Error) RangeOf(int dimmest, int brightest) =>
        brightest > dimmest
            ? (new UnisonRange { Min = Math.Clamp(dimmest, 0, 99), Max = Math.Clamp(brightest, 1, 100) }, null)
            : (null, $"the brightest ({brightest}%) must be above the dimmest ({dimmest}%)");
}

public static partial class CommandRunner
{
    /// <summary><c>unison calibrate start|capture|cancel|status</c>.</summary>
    private static int CalibrateCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["status"]:
            {
                var state = UnisonCalibration.Load();
                c.Out.WriteLine(state is null ? "calibration  not running (unison calibrate start)"
                    : $"calibration  step {(state.Step == UnisonCalibration.Lower ? "1 of 2, the dimmest" : "2 of 2, the brightest")}: match the displays by eye, then unison calibrate capture");
                return Done;
            }
            case ["start"]: return CalibrateStart(c);
            case ["capture"]: return CalibrateCapture(c);
            case ["cancel"]: return CalibrateCancel(c);
            default: return Wrong(c, "unison calibrate takes start, capture, cancel or status");
        }
    }

    private static List<BrightnessTarget> Calibrated(CommandContext c)
    {
        var excluded = SettingsStore.Load().Unison.Excluded;
        return Targets(c).Where(t => !excluded.Contains(t.Key)).ToList();
    }

    private static int CalibrateStart(CommandContext c)
    {
        if (UnisonCalibration.IsActive) return Refuse(c, "a calibration is already open: unison calibrate capture, or cancel");
        var targets = Calibrated(c);
        var u = SettingsStore.Load().Unison;
        var state = new UnisonCalibration { PreviousLevel = u.Level };
        foreach (var t in targets)
            if (t.Read() is int now) state.Before[t.Key] = now;
        if (state.Before.Count == 0) return Refuse(c, "no display's own brightness can be read here, so there is nothing to calibrate");
        state.Save();
        int failed = MoveCalibrated(targets, t => u.Ranges.TryGetValue(t.Key, out var r) ? r.Min : 0, c);
        c.Out.WriteLine("Step 1 of 2: every display is at its dimmest. Set each to the dimmest you want it to go (brightness N --ddc|--backlight, or the Brightness page), matching them by eye, then: dispctrl-linux unison calibrate capture");
        return failed == 0 ? Done : Refused;
    }

    private static int CalibrateCapture(CommandContext c)
    {
        if (UnisonCalibration.Load() is not { } state) return Refuse(c, "no calibration is open (unison calibrate start)");
        var targets = Calibrated(c);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in targets)
            if (t.Read() is int now) levels[t.Key] = now;
        if (levels.Count == 0) return Refuse(c, "no display answered, so nothing was captured");

        if (state.Step == UnisonCalibration.Lower)
        {
            SettingsStore.Update(s => { foreach (var (key, level) in levels) s.Unison.Ranges[key] = new UnisonRange { Min = Math.Clamp(level, 0, 99), Max = s.Unison.RangeOf(key).Max }; });
            state.Step = UnisonCalibration.Upper;
            state.Save();
            var u = SettingsStore.Load().Unison;
            int failed = MoveCalibrated(targets, t => u.Ranges.TryGetValue(t.Key, out var r) ? r.Max : 100, c);
            c.Out.WriteLine("Step 2 of 2: every display is at its brightest. Set each to the brightest you want it to go, matching them by eye, then: dispctrl-linux unison calibrate capture");
            return failed == 0 ? Done : Refused;
        }

        var current = SettingsStore.Load().Unison;
        var ranges = new Dictionary<string, UnisonRange>(StringComparer.Ordinal);
        foreach (var (key, level) in levels)
        {
            var (range, error) = UnisonCalibration.RangeOf(current.RangeOf(key).Min, level);
            if (range is null) return Refuse(c, $"{targets.First(t => t.Key == key).Label}: {error}; set it brighter and capture again");
            ranges[key] = range;
        }
        SettingsStore.Update(s => { foreach (var (key, range) in ranges) s.Unison.Ranges[key] = range; s.Unison.Level = 100; });
        UnisonCalibration.Clear();
        foreach (var t in targets)
            if (ranges.TryGetValue(t.Key, out var r)) c.Out.WriteLine($"  {t.Label,-20} {r.Min}-{r.Max}%");
        c.Out.WriteLine("Calibrated. Unison now runs each display between those limits.");
        return Done;
    }

    private static int CalibrateCancel(CommandContext c)
    {
        if (UnisonCalibration.Load() is not { } state)
        {
            UnisonCalibration.Clear();
            c.Out.WriteLine("No calibration was open.");
            return Done;
        }
        var failures = new List<string>();
        foreach (var t in Targets(c))
            if (state.Before.TryGetValue(t.Key, out int level) && t.Write(level) is { } error) failures.Add(error);
        SettingsStore.Update(s => s.Unison.Level = state.PreviousLevel);
        UnisonCalibration.Clear();
        foreach (var f in failures) c.Err.WriteLine($"dispctrl-linux: {f}");
        c.Out.WriteLine("Calibration cancelled; the displays are back where they were. Limits already captured are kept.");
        return failures.Count == 0 ? Done : Refused;
    }

    private static int MoveCalibrated(List<BrightnessTarget> targets, Func<BrightnessTarget, int> endpoint, CommandContext c)
    {
        int failed = 0;
        foreach (var t in targets)
            if (t.Write(endpoint(t)) is { } error) { c.Err.WriteLine($"dispctrl-linux: {error}"); failed++; }
        return failed;
    }
}
