using System.Globalization;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>oled</c>: idle dimming in two stages, paused for full-screen
    /// windows and playing media; <c>oled rest</c> dims now.</summary>
    private static int OledCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        if (verbs is ["rest"])
        {
            if (c.Engine is not { } engine)
                return Refuse(c, $"only the running engine can rest the displays: {Distribution.Distro.EngineStartCommand}");
            engine.OledRest();
            c.Out.WriteLine("Resting the displays until the next touch of the keyboard or mouse.");
            return Done;
        }
        bool? on = verbs switch { ["on"] => true, ["off"] => false, ["toggle"] => !SettingsStore.Load().OledCare.Enabled, [] => null, _ => null };
        if (verbs is not ([] or ["on"] or ["off"] or ["toggle"])) return Wrong(c, OledHelp);
        int? Get(string flag, int lo, int hi, out bool bad)
        {
            bad = false;
            if (!flags.TryGetValue(flag, out var t)) return null;
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= lo && v <= hi) return v;
            bad = true;
            return null;
        }
        var idle = Get("--idle-minutes", 1, 120, out bool b1);
        var dim = Get("--dim", 10, 90, out bool b2);
        var second = Get("--second-minutes", 2, 240, out bool b3);
        var secondDim = Get("--second-dim", 10, 95, out bool b4);
        if (b1 || b2 || b3 || b4) return Wrong(c, OledHelp);
        bool? stage2 = null, pauseFull = null, pauseMedia = null;
        foreach (var (flag, set) in new (string, Action<bool>)[] { ("--second-stage", v => stage2 = v), ("--pause-fullscreen", v => pauseFull = v), ("--pause-media", v => pauseMedia = v) })
        {
            if (!flags.TryGetValue(flag, out var t)) continue;
            if (!TryOnOff(t, out bool v)) return Wrong(c, $"{flag} takes on or off");
            set(v);
        }
        var saved = SettingsStore.Update(s =>
        {
            var o = s.OledCare;
            if (on is bool e) o.Enabled = e;
            if (idle is int i) o.IdleMinutes = i;
            if (dim is int d) o.DimPercent = d;
            if (second is int sm) o.SecondMinutes = sm;
            if (secondDim is int sd) o.SecondDimPercent = sd;
            if (stage2 is bool st) o.SecondStage = st;
            if (pauseFull is bool pf) o.PauseFullscreen = pf;
            if (pauseMedia is bool pm) o.PauseMedia = pm;
        }).OledCare;
        c.Out.WriteLine($"oled care    {(saved.Enabled ? "on" : "off")}");
        c.Out.WriteLine($"stage one    {saved.DimPercent}% dimmer after {saved.IdleMinutes} min idle");
        c.Out.WriteLine(saved.SecondStage ? $"stage two    {saved.SecondDimPercent}% dimmer after {saved.SecondMinutes} min" : "stage two    off");
        c.Out.WriteLine($"pauses for   {(saved.PauseFullscreen ? "full-screen windows" : "")}{(saved.PauseFullscreen && saved.PauseMedia ? " and " : "")}{(saved.PauseMedia ? "playing media" : "")}");
        return saved.Enabled ? AwakeNote(c) : Done;
    }

    private const string OledHelp = "oled takes on, off, toggle or rest, and --idle-minutes 1-120 --dim 10-90 --second-stage on|off --second-minutes N --second-dim 10-95 --pause-fullscreen on|off --pause-media on|off";

    /// <summary><c>focus</c>: dim everything but the window in front.</summary>
    private static int FocusCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        int? dim = null;
        if (flags.TryGetValue("--dim", out var t))
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) || d is < 10 or > 90) return Wrong(c, "--dim takes 10 to 90");
            dim = d;
        }
        bool? on = verbs switch { ["on"] => true, ["off"] => false, ["toggle"] => !SettingsStore.Load().Focus.Enabled, _ => null };
        if (verbs is not ([] or ["on"] or ["off"] or ["toggle"])) return Wrong(c, "focus takes on, off or toggle, and --dim 10-90");
        var f = SettingsStore.Update(s => { if (on is bool e) s.Focus.Enabled = e; if (dim is int d) s.Focus.DimPercent = d; }).Focus;
        c.Out.WriteLine($"focus mode   {(f.Enabled ? "on" : "off")}, everything else {f.DimPercent}% darker");
        if (c.Engine?.FocusProblem() is { } problem) c.Out.WriteLine($"note         {problem}");
        return f.Enabled ? AwakeNote(c) : Done;
    }

    /// <summary><c>roomlight</c>: brightness follows the ambient light sensor.</summary>
    private static int RoomLightCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        int? min = null, max = null;
        foreach (var (flag, set) in new (string, Action<int>)[] { ("--min", v => min = v), ("--max", v => max = v) })
        {
            if (!flags.TryGetValue(flag, out var t)) continue;
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v is < 0 or > 100) return Wrong(c, $"{flag} takes 0 to 100");
            set(v);
        }
        bool? on = verbs switch { ["on"] => true, ["off"] => false, _ => null };
        if (verbs is not ([] or ["on"] or ["off"])) return Wrong(c, "roomlight takes on or off, and --min N --max N (unison's level in the dark and in bright light)");
        var r = SettingsStore.Update(s => { if (on is bool e) s.RoomLight.Enabled = e; if (min is int a) s.RoomLight.Min = a; if (max is int b) s.RoomLight.Max = b; }).RoomLight;
        using var sensor = Session.RoomLight.Open(out var why);
        c.Out.WriteLine($"room light   {(r.Enabled ? "on" : "off")}, unison from {r.Min}% in the dark to {r.Max}% in bright light");
        c.Out.WriteLine(sensor?.Lux() is double lux ? $"sensor       {lux:0} lux now, so {Session.RoomLight.LevelFor(lux, r.Min, r.Max)}%" : $"sensor       {why}");
        return r.Enabled ? AwakeNote(c) : Done;
    }

    /// <summary><c>monitor-sleep</c>: the displays off after a while idle.</summary>
    private static int MonitorSleepCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        int? minutes = null;
        if (flags.TryGetValue("--minutes", out var t))
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) || m is < 1 or > 600) return Wrong(c, "--minutes takes 1 to 600");
            minutes = m;
        }
        bool? on = verbs switch { ["on"] => true, ["off"] => false, _ => null };
        if (verbs is not ([] or ["on"] or ["off"])) return Wrong(c, "monitor-sleep takes on or off, and --minutes N");
        var m2 = SettingsStore.Update(s => { if (on is bool e) s.MonitorSleep.Enabled = e; if (minutes is int n) s.MonitorSleep.Minutes = n; }).MonitorSleep;
        c.Out.WriteLine(m2.Enabled ? $"The displays switch off after {m2.Minutes} min with nothing touched (keep awake's displays-on setting wins)." : "Monitor sleep off.");
        return m2.Enabled ? AwakeNote(c) : Done;
    }
}
