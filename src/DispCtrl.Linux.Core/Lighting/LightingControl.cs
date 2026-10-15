using System.Globalization;
using System.Net.Sockets;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Lighting;

/// <summary>What the rgb command, presets and the window ask of the lighting:
/// one colour, off, or one of the devices' own effects, on one device or all.</summary>
public static class LightingControl
{
    /// <summary>Names a person may give instead of #RRGGBB.</summary>
    public static readonly IReadOnlyDictionary<string, string> Named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "#FFFFFF", ["warm"] = "#FF9329", ["amber"] = "#FFB000", ["red"] = "#FF0000", ["orange"] = "#FF6000",
        ["yellow"] = "#FFE000", ["green"] = "#00FF00", ["cyan"] = "#00FFFF", ["blue"] = "#0000FF", ["purple"] = "#8000FF",
        ["pink"] = "#FF40A0", ["off"] = "#000000",
    };

    public static bool TryColour(string text, out (byte R, byte G, byte B) colour)
    {
        colour = default;
        if (Named.TryGetValue(text, out var named)) text = named;
        var hex = text.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        colour = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Why OpenRGB cannot be reached, said so a person can act on it.</summary>
    public static string Unreachable(LightingSettings s) =>
        OpenRgbSetup.SnapLimit() is { } snap
            ? $"OpenRGB's SDK server does not answer at {s.Host}:{s.Port}: {snap}"
            : OpenRgbSetup.Find() is not null
                ? $"OpenRGB's SDK server does not answer at {s.Host}:{s.Port}: dispctrl-linux rgb start starts it"
                : $"OpenRGB's SDK server does not answer at {s.Host}:{s.Port} and no OpenRGB is installed: dispctrl-linux rgb setup downloads the official one";

    public static OpenRgb Connect(LightingSettings s) => OpenRgb.Connect(s.Host, s.Port);

    /// <summary>Connects, starting OpenRGB once first when it is not running and settings allow, so a command just works.</summary>
    public static OpenRgb ConnectOrStart(LightingSettings s)
    {
        try { return Connect(s); }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            if (!OpenRgbSetup.AutoStart(s)) throw;
            return Connect(s);
        }
    }

    /// <summary>What a lighting command did: the problems that stopped it, and notes on what was done instead of what was asked.</summary>
    public sealed record Result(List<string> Problems, List<string> Notes);

    /// <summary>Applies <paramref name="what"/> ("#RRGGBB", "off", "mode:NAME")
    /// to the devices chosen; the problems, one line each.</summary>
    public static List<string> Apply(string what, int? device = null) => ApplyDetailed(what, device).Problems;

    /// <summary>OpenRGB first; when it cannot be reached or finds nothing, the kernel's keyboard backlight.</summary>
    public static Result ApplyDetailed(string what, int? device = null)
    {
        var settings = SettingsStore.Load().Lighting;
        var problems = new List<string>();
        var notes = new List<string>();
        try
        {
            using var rgb = ConnectOrStart(settings);
            var devices = rgb.Devices().Where(d => device is null || d.Index == device).ToList();
            if (devices.Count == 0)
            {
                if (SysfsLeds.Enumerate().Count > 0) return ApplyKernel(what, device, settings);
                problems.Add(device is null ? "OpenRGB found no lighting devices" : $"no device {device + 1}");
                return new(problems, notes);
            }
            foreach (var d in devices)
            {
                try
                {
                    if (what.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = what[5..];
                        var mode = d.Modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? d.Modes.FirstOrDefault(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                        if (mode is null) { problems.Add($"{d.Name} has no {name} effect ({string.Join(", ", d.Modes.Select(m => m.Name))})"); continue; }
                        rgb.SetMode(d, mode);
                    }
                    else if (TryColour(what, out var c)) rgb.SetColour(d, c.R, c.G, c.B);
                    else problems.Add($"'{what}' is not a colour or an effect");
                }
                catch (IOException ex)
                {
                    Diagnostics.AppLog.Warn("openrgb", $"{d.Name}: {ex.Message}");
                    problems.Add($"{d.Name}: {ex.Message}");
                }
            }
            if (device is null && problems.Count == 0) SettingsStore.Update(s => s.Lighting.Last = what);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            if (SysfsLeds.Enumerate().Count > 0) return ApplyKernel(what, device, settings);
            Diagnostics.AppLog.Warn("openrgb", $"unreachable: {ex.GetBaseException().Message}");
            problems.Add(Unreachable(settings));
        }
        return new(problems, notes);
    }

    /// <summary>Lights individual keys: each entry a key's name (A, "left shift",
    /// F5, "led 12") and its colour; the other keys keep theirs. On the device
    /// chosen, or the first keyboard with a light for each key.</summary>
    public static List<string> ApplyKeys(IReadOnlyList<(string Name, (byte R, byte G, byte B) Colour)> entries, int? device = null)
    {
        var settings = SettingsStore.Load().Lighting;
        var problems = new List<string>();
        try
        {
            using var rgb = ConnectOrStart(settings);
            var devices = rgb.Devices();
            var target = device is null ? devices.FirstOrDefault(d => d.HasPerKeyLeds) : devices.FirstOrDefault(d => d.Index == device);
            if (target is null) { problems.Add(device is null ? "no keyboard with a light for each key" : $"no device {device + 1}"); return problems; }
            if (target.LedCount < 2) { problems.Add($"{target.Name} has one light: use rgb colour"); return problems; }
            var colours = new Dictionary<int, (byte, byte, byte)>();
            foreach (var (name, colour) in entries)
            {
                var leds = KeyboardLayout.Find(target, name);
                if (leds.Count == 0) problems.Add($"{target.Name} has no key '{name}'");
                foreach (var led in leds) colours[led] = colour;
            }
            if (colours.Count == 0) return problems;
            try { rgb.SetLeds(target, colours); }
            catch (IOException ex) { problems.Add($"{target.Name}: {ex.Message}"); }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            Diagnostics.AppLog.Warn("openrgb", $"unreachable: {ex.GetBaseException().Message}");
            problems.Add(Unreachable(settings));
        }
        return problems;
    }

    /// <summary>Entries of "NAME=COLOUR" separated by commas, semicolons or new
    /// lines (the last space also parts a name from its colour); null with the
    /// reason when one is wrong.</summary>
    public static List<(string Name, (byte R, byte G, byte B) Colour)>? ParseKeys(string spec, out string? error)
    {
        error = null;
        var entries = new List<(string, (byte, byte, byte))>();
        foreach (var raw in spec.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = raw.LastIndexOf('=');
            if (at < 0) at = raw.LastIndexOf(' ');
            if (at <= 0) { error = $"'{raw}' is not NAME=COLOUR"; return null; }
            var name = raw[..at].Trim();
            var text = raw[(at + 1)..].Trim();
            if (!TryColour(text, out var colour)) { error = $"'{text}' is not a colour: #RRGGBB or {string.Join(", ", Named.Keys)}"; return null; }
            entries.Add((name, colour));
        }
        if (entries.Count == 0) error = "no keys given: NAME=COLOUR, comma separated";
        return error is null ? entries : null;
    }

    private static Result ApplyKernel(string what, int? device, LightingSettings settings)
    {
        var problems = new List<string>();
        var notes = new List<string> { "OpenRGB is not available, so this used the keyboard backlight the kernel exposes (dispctrl-linux rgb setup adds every other device)" };
        SysfsLeds.Apply(what, device, problems, notes);
        if (device is null && problems.Count == 0) SettingsStore.Update(s => s.Lighting.Last = what);
        return new(problems, notes);
    }
}
