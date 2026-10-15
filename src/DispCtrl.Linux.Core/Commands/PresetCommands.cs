using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Presets;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>preset</c>: save the whole desk under a name and put it back,
    /// as <c>dispctrl preset</c> does on Windows.</summary>
    private static int PresetCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["list"]:
                return PresetList(c);
            case ["save", var name, .. var rest]:
                return PresetSave(name, rest, c);
            case ["suggested"]:
                foreach (var t in PresetDesk.Suggested())
                    c.Out.WriteLine($"{t.Name,-12} {t.Description}{(FindPreset(t.Name) is null ? "" : "  (added)")}");
                c.Out.WriteLine("preset add NAME adds one; preset add all adds every one not added yet.");
                return Done;
            case ["add", .. var names] when names.Length > 0:
                return PresetAdd(names, c);
            case ["apply", var name]:
                return PresetApply(name, c);
            case ["show", var name]:
                return PresetShow(name, c);
            case ["delete", var name]:
                if (FindPreset(name) is null) return Wrong(c, NoPreset(name));
                SettingsStore.Update(s => s.Presets.RemoveAll(p => Same(p.Name, name)));
                c.Out.WriteLine($"Deleted {name}.");
                return Done;
            case ["rename", var from, var to]:
                if (FindPreset(from) is null) return Wrong(c, NoPreset(from));
                to = to.Trim();
                if (to.Length == 0) return Wrong(c, "a preset needs a name");
                if (!Same(from, to) && FindPreset(to) is not null) return Wrong(c, $"there is already a preset called {to}");
                SettingsStore.Update(s => s.Presets.First(p => Same(p.Name, from)).Name = to);
                c.Out.WriteLine($"{from} is now {to}.");
                return Done;
            case ["set", var name, "--skip", var parts]:
                if (FindPreset(name) is null) return Wrong(c, NoPreset(name));
                if (!TryParts(parts, out var skip)) return Wrong(c, PartsHelp);
                SettingsStore.Update(s => s.Presets.First(p => Same(p.Name, name)).Skip = skip);
                c.Out.WriteLine(skip.Count == 0 ? $"{name} puts every part back." : $"{name} leaves {string.Join(", ", skip)} alone.");
                return Done;
            default:
                return Wrong(c, "preset takes list, save NAME [--skip PARTS], apply NAME, show NAME, set NAME --skip PARTS|none, rename OLD NEW, delete NAME, suggested or add NAME|all");
        }
    }

    private const string PartsHelp = "--skip takes none, or parts separated by commas: layout, brightness, nightlight, theme, controls";

    /// <summary><c>preset add</c>: ready-made presets, by name or all; one whose
    /// name is already taken is left as it is.</summary>
    private static int PresetAdd(string[] names, CommandContext c)
    {
        var suggested = PresetDesk.Suggested();
        List<Preset?> wanted = names is ["all"] ? [.. suggested]
            : names.Select(n => suggested.FirstOrDefault(t => Same(t.Name, n))).ToList();
        int unknown = wanted.FindIndex(t => t is null);
        if (unknown >= 0) return Wrong(c, $"no ready-made preset called '{names[unknown]}' (preset suggested lists them)");
        var added = new List<string>();
        SettingsStore.Update(s =>
        {
            foreach (var t in wanted)
            {
                if (s.Presets.Any(p => Same(p.Name, t!.Name))) continue;
                s.Presets.Add(t!);
                added.Add(t!.Name);
            }
        });
        c.Out.WriteLine(added.Count == 0 ? "Nothing added: those names are taken." : $"Added {string.Join(", ", added)}.");
        return Done;
    }

    private static int PresetList(CommandContext c)
    {
        var presets = SettingsStore.Load().Presets;
        if (presets.Count == 0)
        {
            c.Out.WriteLine("No presets yet: dispctrl-linux preset save NAME keeps the desk as it is now.");
            return Done;
        }
        var outputs = XRandR.IsAvailable ? XRandR.Query() : [];
        var names = XRandR.IsAvailable ? XRandR.MonitorNames() : new Dictionary<string, string>();
        foreach (var p in presets)
        {
            string fit = p.Outputs.Count == 0 ? p.Description is null ? "no layout" : "any desk"
                : PresetDesk.Mismatch(p, outputs, names) is { } why ? $"not this desk: {why}" : "matches the displays attached";
            string parts = string.Join(", ", PresetParts.All.Where(p.Holds));
            c.Out.WriteLine($"{p.Name,-20} {p.Saved:yyyy-MM-dd HH:mm}  {parts}  ({fit})");
        }
        return Done;
    }

    private static int PresetSave(string name, string[] rest, CommandContext c)
    {
        name = name.Trim();
        if (name.Length == 0) return Wrong(c, "a preset needs a name");
        if (!TryFlags(rest, c, out var flags)) return AskedWrongly;
        var existing = FindPreset(name);
        List<string> skip;
        if (flags.TryGetValue("--skip", out var parts))
        {
            if (!TryParts(parts, out skip)) return Wrong(c, PartsHelp);
        }
        else skip = existing?.Skip ?? [];

        var preset = PresetDesk.Capture(existing?.Name ?? name, skip);
        SettingsStore.Update(s =>
        {
            int at = s.Presets.FindIndex(p => Same(p.Name, name));
            if (at >= 0) s.Presets[at] = preset;
            else s.Presets.Add(preset);
        });
        c.Out.WriteLine($"{(existing is null ? "Saved" : "Updated")} {preset.Name}: {Describe(preset)}.");
        return Done;
    }

    private static int PresetApply(string name, CommandContext c)
    {
        if (FindPreset(name) is not { } preset) return Wrong(c, NoPreset(name));
        var problems = PresetDesk.Apply(preset);
        if (preset.Holds(PresetParts.NightLight) && preset.NightLight is not null)
        {
            foreach (var f in c.ApplyRamps().Where(r => !r.Written)) problems.Add($"nightlight: {f.Output}: {f.Error}");
        }
        foreach (var p in problems) c.Err.WriteLine($"dispctrl-linux: {p}");
        c.Out.WriteLine(problems.Count == 0 ? $"Applied {preset.Name}." : $"Applied {preset.Name}, except {problems.Count} part(s) above.");
        return problems.Count == 0 ? Done : Refused;
    }

    private static int PresetShow(string name, CommandContext c)
    {
        if (FindPreset(name) is not { } p) return Wrong(c, NoPreset(name));
        c.Out.WriteLine($"{p.Name}, saved {p.Saved:yyyy-MM-dd HH:mm}");
        if (p.Skip.Count > 0) c.Out.WriteLine($"  leaves alone: {string.Join(", ", p.Skip)}");
        foreach (var o in p.Outputs)
        {
            c.Out.WriteLine(o.Enabled
                ? $"  {o.Name,-10} {o.Width}x{o.Height} @ {o.RefreshHz:0.##} Hz at {o.X},{o.Y}{(o.Rotation != 0 ? $", turned {o.Rotation}°" : "")}{(o.Primary ? ", main" : "")}{(o.Model is null ? "" : $"  {o.Model}")}"
                : $"  {o.Name,-10} off");
        }
        if (p.Description is { } about) c.Out.WriteLine($"  {about}");
        if (p.BrightnessAll is int all) c.Out.WriteLine($"  brightness every display {all}%");
        if (p.PictureMode is { } mode) c.Out.WriteLine($"  picture mode {mode}, on monitors that have one");
        foreach (var b in p.Brightness) c.Out.WriteLine($"  brightness {b.Id.Split('|')[0],-16} {b.Percent}%");
        if (p.NightLight is { } n) c.Out.WriteLine($"  night light {(n.Enabled ? $"on, strength {n.Strength}" : "off")}{(!p.KeepSchedule && n.Scheduled ? $", {n.From} to {n.To}" : "")}");
        if (p.Dim is { Count: > 0 } dim) c.Out.WriteLine($"  dimming {string.Join(", ", dim.Select(d => $"{d.Key} {Percent(d.Value)}%"))}");
        if (p.Dark is bool dark) c.Out.WriteLine($"  dark mode {(dark ? "on" : "off")}");
        foreach (var g in p.Controls.GroupBy(x => x.Monitor))
            c.Out.WriteLine($"  controls {g.Key.Split('|')[0]}: {string.Join(", ", g.Select(x => $"0x{x.Code:X2}={x.Value}"))}");
        return Done;
    }

    private static string Describe(Preset p)
    {
        var parts = new List<string>();
        if (p.Outputs.Count > 0) parts.Add($"{p.Outputs.Count(o => o.Enabled)} display(s) in their places");
        if (p.Brightness.Count > 0) parts.Add($"{p.Brightness.Count} brightness level(s)");
        if (p.NightLight is not null) parts.Add("night light and dimming");
        if (p.Dark is not null) parts.Add("the theme");
        if (p.Controls.Count > 0) parts.Add($"{p.Controls.Count} monitor control(s)");
        return parts.Count == 0 ? "nothing it could read" : string.Join(", ", parts);
    }

    private static bool TryParts(string text, out List<string> parts)
    {
        parts = [];
        if (text is "none" or "") return true;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.ToLowerInvariant() is "night-light" or "nightlight" ? PresetParts.NightLight : part.ToLowerInvariant();
            if (!PresetParts.All.Contains(p)) return false;
            if (!parts.Contains(p)) parts.Add(p);
        }
        return true;
    }

    private static Preset? FindPreset(string name) => SettingsStore.Load().Presets.FirstOrDefault(p => Same(p.Name, name));

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NoPreset(string name) => $"no preset called '{name}' (preset list names them)";

    /// <summary><c>tray</c>: the icon in the panel's tray, kept by the window's
    /// process (dispctrl-linux-gui --tray), which reads this setting.</summary>
    private static int TrayCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["status"]:
                var t = SettingsStore.Load().Tray;
                c.Out.WriteLine($"tray icon    {(t.Enabled ? "shown from sign-in" : "hidden")}");
                c.Out.WriteLine($"left click   {(t.QuickPanel ? "opens the quick panel" : "opens the window")}");
                return Done;
            case ["on" or "off" or "true" or "false"]:
                bool on = args[0] is "on" or "true";
                SettingsStore.Update(s => s.Tray.Enabled = on);
                c.Out.WriteLine(on ? "The tray icon shows from the next sign-in, or now: dispctrl-linux-gui --tray" : "Tray icon hidden.");
                return Done;
            case ["click", var what]:
                if (what is not ("panel" or "window")) return Wrong(c, "tray click takes panel or window");
                SettingsStore.Update(s => s.Tray.QuickPanel = what == "panel");
                c.Out.WriteLine(what == "panel" ? "A left click opens the quick panel." : "A left click opens the window.");
                return Done;
            default:
                return Wrong(c, "tray takes on, off or click panel|window");
        }
    }
}
