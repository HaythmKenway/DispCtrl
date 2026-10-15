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
        $"OpenRGB's SDK server does not answer at {s.Host}:{s.Port}: start OpenRGB with its server on (openrgb --server, or Settings > SDK Server)";

    public static OpenRgb Connect(LightingSettings s) => OpenRgb.Connect(s.Host, s.Port);

    /// <summary>Applies <paramref name="what"/> ("#RRGGBB", "off", "mode:NAME")
    /// to the devices chosen; the problems, one line each.</summary>
    public static List<string> Apply(string what, int? device = null)
    {
        var settings = SettingsStore.Load().Lighting;
        var problems = new List<string>();
        try
        {
            using var rgb = Connect(settings);
            var devices = rgb.Devices().Where(d => device is null || d.Index == device).ToList();
            if (devices.Count == 0) { problems.Add(device is null ? "OpenRGB found no lighting devices" : $"no device {device}"); return problems; }
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
                catch (IOException ex) { problems.Add($"{d.Name}: {ex.Message}"); }
            }
            if (device is null && problems.Count == 0) SettingsStore.Update(s => s.Lighting.Last = what);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            problems.Add(Unreachable(settings));
        }
        return problems;
    }
}
