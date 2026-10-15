using System.Globalization;
using System.Net.Sockets;
using DispCtrl.Linux.Lighting;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>rgb</c>: keyboard, mouse, fan and strip lighting through OpenRGB.</summary>
    private static int RgbCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        int? device = null;
        if (flags.TryGetValue("--device", out var d))
        {
            if (!int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1) return Wrong(c, "--device takes a device number from rgb's list");
            device = n - 1;
        }
        var settings = SettingsStore.Load().Lighting;
        switch (verbs)
        {
            case [] or ["list"]:
                try
                {
                    using var rgb = LightingControl.Connect(settings);
                    var devices = rgb.Devices();
                    if (devices.Count == 0) c.Out.WriteLine("OpenRGB is running and found no lighting devices.");
                    foreach (var dev in devices)
                    {
                        string active = dev.ActiveMode >= 0 && dev.ActiveMode < dev.Modes.Count ? dev.Modes[dev.ActiveMode].Name : "?";
                        c.Out.WriteLine($"{dev.Index + 1,2}  {dev.Name} ({dev.TypeName}, {dev.LedCount} LEDs), effect: {active}");
                        c.Out.WriteLine($"      effects: {string.Join(", ", dev.Modes.Select(m => m.Name))}");
                    }
                    if (settings.Last is { } last) c.Out.WriteLine($"\nlast set     {last}");
                    return Done;
                }
                catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
                {
                    return Refuse(c, LightingControl.Unreachable(settings));
                }
            case ["colour" or "color", var colour]:
                if (!LightingControl.TryColour(colour, out _)) return Wrong(c, $"'{colour}' is not a colour: #RRGGBB or {string.Join(", ", LightingControl.Named.Keys)}");
                return Report(LightingControl.Apply(colour.StartsWith('#') ? colour.ToUpperInvariant() : LightingControl.Named.GetValueOrDefault(colour, "#" + colour.ToUpperInvariant()), device),
                    $"Lighting {colour}.", c);
            case ["off"]:
                return Report(LightingControl.Apply("off", device), "Lighting off.", c);
            case ["mode" or "effect", .. var name] when name.Length > 0:
                return Report(LightingControl.Apply("mode:" + string.Join(' ', name), device), $"Effect: {string.Join(' ', name)}.", c);
            case ["server", var address]:
            {
                var parts = address.Split(':');
                int port = OpenRgb.DefaultPort;
                if (parts.Length > 2 || parts[0].Length == 0 || (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535)))
                    return Wrong(c, "rgb server takes HOST or HOST:PORT, such as 127.0.0.1:6742");
                SettingsStore.Update(s => { s.Lighting.Host = parts[0]; s.Lighting.Port = port; });
                c.Out.WriteLine($"OpenRGB's server: {parts[0]}:{port}.");
                return Done;
            }
            default:
                return Wrong(c, "rgb takes list, colour COLOUR, off, mode NAME or server HOST[:PORT], and --device N");
        }

        static int Report(List<string> problems, string done, CommandContext c)
        {
            foreach (var p in problems) c.Err.WriteLine($"dispctrl-linux: {p}");
            if (problems.Count == 0) c.Out.WriteLine(done);
            return problems.Count == 0 ? Done : Refused;
        }
    }
}
