using System.Diagnostics;
using System.Globalization;
using DispCtrl.Linux.Automation;
using DispCtrl.Linux.Presets;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>features</c>: named lists of steps.</summary>
    private static int FeaturesCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["list"]:
                var features = SettingsStore.Load().Features;
                if (features.Count == 0) c.Out.WriteLine("No features yet: features add NAME \"STEP; STEP\".");
                foreach (var f in features) c.Out.WriteLine($"{f.Name,-20} {string.Join("; ", f.Steps)}");
                return Done;
            case ["add", var name, .. var rest] when rest.Length > 0:
            {
                var steps = string.Join(' ', rest).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var step in steps)
                    if (FeatureRunner.Problem(step) is { } why) return Wrong(c, $"'{step}': {why}");
                SettingsStore.Update(s =>
                {
                    s.Features.RemoveAll(f => Same(f.Name, name));
                    s.Features.Add(new Feature { Name = name.Trim(), Steps = steps });
                });
                c.Out.WriteLine($"{name}: {steps.Count} step(s).");
                return Done;
            }
            case ["remove", var name]:
            {
                int removed = 0;
                SettingsStore.Update(s => removed = s.Features.RemoveAll(f => Same(f.Name, name)));
                return removed == 0 ? Wrong(c, $"no feature '{name}'") : Write(c.Out, $"Removed {name}.");
            }
            case ["run", var name, .. var rest]:
            {
                if (SettingsStore.Load().Features.FirstOrDefault(f => Same(f.Name, name)) is not { } feature) return Wrong(c, $"no feature '{name}' (features lists them)");
                bool dry = rest is ["--dry-run"];
                var problems = FeatureRunner.Run(feature, step => RunStep(step, c), dry, c.Out);
                foreach (var p in problems) c.Err.WriteLine($"dispctrl-linux: {p}");
                return problems.Count == 0 ? Done : Refused;
            }
            default:
                return Wrong(c, "features takes list, add NAME \"STEP; STEP\", remove NAME or run NAME [--dry-run]; steps are commands, wait MS or run PROGRAM");
        }
    }

    /// <summary>One step of a feature, in this same context.</summary>
    private static string? RunStep(string[] args, CommandContext c)
    {
        var err = new StringWriter();
        int code = Run(args, c with { Err = err });
        return code == Done ? null : err.ToString().Replace("dispctrl-linux: ", "", StringComparison.Ordinal).Trim();
    }

    /// <summary><c>triggers</c>: when this happens, run that feature.</summary>
    private static int TriggersCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        switch (verbs)
        {
            case [] or ["list"]:
                var list = SettingsStore.Load().Triggers;
                if (list.Count == 0) c.Out.WriteLine("No triggers yet: triggers add EVENT FEATURE [--match X] [--minutes N].");
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    string when = t.Event == "idle" ? $"idle {t.Minutes} min" : string.IsNullOrEmpty(t.Match) ? t.Event : $"{t.Event} {t.Match}";
                    c.Out.WriteLine($"{i + 1,2}  {(t.Enabled ? "  " : "- ")}{when,-30} -> {t.Feature}");
                }
                c.Out.WriteLine($"\nEvents: {string.Join(", ", Trigger.Events)}.");
                return AwakeNote(c);
            case ["add", var ev, var feature]:
            {
                if (!Trigger.Events.Contains(ev)) return Wrong(c, $"'{ev}' is not an event: {string.Join(", ", Trigger.Events)}");
                if (!SettingsStore.Load().Features.Any(f => Same(f.Name, feature))) return Wrong(c, $"no feature '{feature}' (features add makes one)");
                int minutes = 10;
                if (flags.TryGetValue("--minutes", out var m) && (!int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) || minutes is < 1 or > 1440))
                    return Wrong(c, "--minutes takes 1 to 1440");
                flags.TryGetValue("--match", out var match);
                if (ev == "at-time" && (match is null || !Schedule.TryParse(match, out _))) return Wrong(c, "at-time needs --match HH:MM");
                SettingsStore.Update(s => s.Triggers.Add(new Trigger { Event = ev, Feature = feature, Match = match, Minutes = minutes }));
                c.Out.WriteLine($"When {ev}{(match is null ? "" : $" {match}")}{(ev == "idle" ? $" for {minutes} min" : "")}: {feature}.");
                return AwakeNote(c);
            }
            case ["remove", var n]:
            {
                bool ok = false;
                SettingsStore.Update(s => { if (TryIndex(n, s.Triggers.Count, out int i)) { s.Triggers.RemoveAt(i); ok = true; } });
                return ok ? Write(c.Out, $"Removed trigger {n}.") : Wrong(c, $"no trigger {n} (triggers lists them)");
            }
            case ["on" or "off", var n]:
            {
                bool ok = false, on = verbs[0] == "on";
                SettingsStore.Update(s => { if (TryIndex(n, s.Triggers.Count, out int i)) { s.Triggers[i].Enabled = on; ok = true; } });
                return ok ? Write(c.Out, $"Trigger {n} {verbs[0]}.") : Wrong(c, $"no trigger {n} (triggers lists them)");
            }
            default:
                return Wrong(c, "triggers takes list, add EVENT FEATURE [--match X] [--minutes N], remove N or on|off N");
        }
    }

    private static bool TryIndex(string text, int count, out int index)
    {
        index = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n - 1 : -1;
        return index >= 0 && index < count;
    }

    /// <summary><c>preset desk NAME on|off</c>: apply by itself when exactly its displays connect.</summary>
    private static int PresetDeskCommand(string name, string value, CommandContext c)
    {
        if (FindPreset(name) is not { } p) return Wrong(c, NoPreset(name));
        if (!TryOnOff(value, out bool on)) return Wrong(c, "preset desk takes a name and on or off");
        if (on && p.Outputs.Count(o => o.Enabled) == 0) return Wrong(c, $"{p.Name} holds no layout, so no desk can be recognised by it");
        SettingsStore.Update(s => s.Presets.First(x => Same(x.Name, name)).Desk = on);
        c.Out.WriteLine(on ? $"{p.Name} applies by itself when exactly its displays are connected." : $"{p.Name} is no longer a desk profile.");
        return on ? AwakeNote(c) : Done;
    }

    /// <summary><c>preset rule</c>: a preset while an app is in front.</summary>
    private static int PresetRuleCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        switch (verbs)
        {
            case [] or ["list"]:
                var rules = SettingsStore.Load().AppRules;
                if (rules.Count == 0) c.Out.WriteLine("No app rules: preset rule add APP PRESET [--then previous|keep|PRESET] [--delay S].");
                for (int i = 0; i < rules.Count; i++)
                    c.Out.WriteLine($"{i + 1,2}  {(rules[i].Enabled ? "  " : "- ")}{rules[i].App,-16} -> {rules[i].Preset}, then {rules[i].Then} (after {rules[i].DelaySeconds} s)");
                return Done;
            case ["add", var app, var preset]:
            {
                if (FindPreset(preset) is null) return Wrong(c, NoPreset(preset));
                string then = flags.GetValueOrDefault("--then") ?? "previous";
                if (then is not ("previous" or "keep") && FindPreset(then) is null) return Wrong(c, "--then takes previous, keep or a preset's name");
                int delay = 3;
                if (flags.TryGetValue("--delay", out var d) && (!int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out delay) || delay is < 0 or > 600))
                    return Wrong(c, "--delay takes seconds, 0 to 600");
                SettingsStore.Update(s => s.AppRules.Add(new AppRule { App = app, Preset = preset, Then = then, DelaySeconds = delay }));
                c.Out.WriteLine($"While {app} is in front: {preset}; when it leaves: {then}.");
                return AwakeNote(c);
            }
            case ["remove", var n]:
            {
                bool ok = false;
                SettingsStore.Update(s => { if (TryIndex(n, s.AppRules.Count, out int i)) { s.AppRules.RemoveAt(i); ok = true; } });
                return ok ? Write(c.Out, $"Removed rule {n}.") : Wrong(c, $"no rule {n}");
            }
            default:
                return Wrong(c, "preset rule takes list, add APP PRESET [--then previous|keep|PRESET] [--delay S] or remove N");
        }
    }

    /// <summary><c>preset launch NAME PROGRAM [ARGS]</c>: apply, run, and put the
    /// desk back when the program ends. Always here, never in the engine: it
    /// waits for the program.</summary>
    private static int PresetLaunch(string name, string[] program, CommandContext c)
    {
        if (FindPreset(name) is not { } preset) return Wrong(c, NoPreset(name));
        bool keep = program.Contains("--keep");
        program = program.Where(a => a != "--keep").ToArray();
        if (program.Length == 0) return Wrong(c, "preset launch needs a program: preset launch Gaming steam");
        var before = keep ? null : PresetDesk.Capture("(before launch)", PresetParts.All.Where(p => !preset.Holds(p)).ToList());
        foreach (var p in PresetDesk.Apply(preset)) c.Err.WriteLine($"dispctrl-linux: {p}");
        c.ApplyRamps();
        int exit;
        try
        {
            var info = new ProcessStartInfo(program[0]) { UseShellExecute = false };
            foreach (var a in program[1..]) info.ArgumentList.Add(a);
            using var process = Process.Start(info)!;
            process.WaitForExit();
            exit = process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            c.Err.WriteLine($"dispctrl-linux: {program[0]}: {ex.Message}");
            exit = -1;
        }
        if (before is not null)
        {
            foreach (var p in PresetDesk.Apply(before)) c.Err.WriteLine($"dispctrl-linux: putting back: {p}");
            c.ApplyRamps();
            c.Out.WriteLine("The desk is back as it was.");
        }
        return exit == 0 ? Done : Refused;
    }
}
