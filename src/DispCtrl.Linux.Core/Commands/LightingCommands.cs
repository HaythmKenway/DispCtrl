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
                    using var rgb = LightingControl.ConnectOrStart(settings);
                    var devices = rgb.Devices();
                    if (devices.Count == 0 && ListKernelLeds(c, "OpenRGB is running and found no lighting devices")) return Done;
                    if (devices.Count == 0) c.Out.WriteLine("OpenRGB is running and found no lighting devices.");
                    foreach (var dev in devices)
                    {
                        string active = dev.ActiveMode >= 0 && dev.ActiveMode < dev.Modes.Count ? dev.Modes[dev.ActiveMode].Name : "?";
                        c.Out.WriteLine($"{dev.Index + 1,2}  {dev.Name} ({dev.TypeName}, {dev.LedCount} LEDs), effect: {active}");
                        if (dev.HasPerKeyLeds) c.Out.WriteLine($"      per-key: {dev.LedCount} LEDs (rgb key NAME COLOUR --device {dev.Index + 1})");
                        c.Out.WriteLine($"      effects: {string.Join(", ", dev.Modes.Select(m => m.Name))}");
                    }
                    if (settings.Last is { } last) c.Out.WriteLine($"\nlast set     {last}");
                    return Done;
                }
                catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
                {
                    if (ListKernelLeds(c, "OpenRGB is not running")) return Done;
                    return Refuse(c, LightingControl.Unreachable(settings));
                }
            case ["colour" or "color", var colour]:
                if (!LightingControl.TryColour(colour, out _)) return Wrong(c, $"'{colour}' is not a colour: #RRGGBB or {string.Join(", ", LightingControl.Named.Keys)}");
                return ReportResult(LightingControl.ApplyDetailed(colour.StartsWith('#') ? colour.ToUpperInvariant() : LightingControl.Named.GetValueOrDefault(colour, "#" + colour.ToUpperInvariant()), device),
                    $"Lighting {colour}.", c);
            case ["key" or "keys", .. var rest] when rest.Length >= 1:
            {
                List<(string Name, (byte R, byte G, byte B) Colour)>? entries;
                if (verbs[0] == "key")
                {
                    if (rest.Length < 2) return Wrong(c, "rgb key takes NAME[,NAME...] COLOUR, such as: rgb key a,s,d,f red");
                    if (!LightingControl.TryColour(rest[^1], out var colour)) return Wrong(c, $"'{rest[^1]}' is not a colour: #RRGGBB or {string.Join(", ", LightingControl.Named.Keys)}");
                    entries = string.Join(' ', rest[..^1]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => (n, colour)).ToList();
                }
                else
                {
                    var spec = string.Join(' ', rest);
                    if (rest.Length == 1 && File.Exists(rest[0])) spec = File.ReadAllText(rest[0]);
                    entries = LightingControl.ParseKeys(spec, out var why);
                    if (entries is null) return Wrong(c, why!);
                }
                return Report(LightingControl.ApplyKeys(entries, device), $"{entries.Count} key{(entries.Count == 1 ? "" : "s")} set.", c);
            }
            case ["preset", "list"]:
                foreach (var p in settings.KeyPresets) c.Out.WriteLine($"{p.Name}  ({p.Keyboard})");
                if (settings.KeyPresets.Count == 0) c.Out.WriteLine("No key presets saved: rgb preset save NAME.");
                return Done;
            case ["preset", "save" or "apply" or "delete", .. var pname] when pname.Length > 0:
            {
                var label = string.Join(' ', pname);
                if (verbs[1] == "delete")
                {
                    int gone = 0;
                    SettingsStore.Update(s => gone = s.Lighting.KeyPresets.RemoveAll(p => p.Name.Equals(label, StringComparison.OrdinalIgnoreCase)));
                    return gone == 0 ? Refuse(c, $"no key preset '{label}'") : Deleted(c, label);
                }
                return verbs[1] == "save"
                    ? Report(LightingControl.SaveKeyPreset(label, device), $"Saved '{label}'.", c)
                    : Report(LightingControl.ApplyKeyPreset(label, device), $"Applied '{label}'.", c);
            }
            case ["order", var order] when OpenRgb.NormaliseOrder(order) is { } normal:
            {
                try
                {
                    using var rgb = LightingControl.ConnectOrStart(settings);
                    var devices = rgb.Devices();
                    var target = device is null ? devices.FirstOrDefault(d => d.LedCount > 1) : devices.FirstOrDefault(d => d.Index == device);
                    if (target is null) return Refuse(c, "no such device: rgb list shows the numbers");
                    SettingsStore.Update(s => s.Lighting.ColourOrders[target.Name] = normal);
                    c.Out.WriteLine($"{target.Name}: colour order {normal}. Try rgb colour red; if the keyboard shows another colour, try another order (RGB, GRB, BGR ...).");
                    return Done;
                }
                catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
                {
                    return Refuse(c, LightingControl.Unreachable(settings));
                }
            }
            case ["off"]:
                return ReportResult(LightingControl.ApplyDetailed("off", device), "Lighting off.", c);
            case ["mode" or "effect", .. var name] when name.Length > 0:
                return ReportResult(LightingControl.ApplyDetailed("mode:" + string.Join(' ', name), device), $"Effect: {string.Join(' ', name)}.", c);
            case ["start"]:
            {
                var started = OpenRgbSetup.Start(settings, m => c.Out.WriteLine(m));
                if (!started.Ok) return Refuse(c, started.Message);
                c.Out.WriteLine(started.Message);
                if (OpenRgbSetup.UdevAdvice(OpenRgbSetup.Find()) is { } advice) c.Out.WriteLine(advice);
                return Done;
            }
            case ["setup"]:
            {
                var install = OpenRgbSetup.Find();
                if (install is null)
                {
                    var got = OpenRgbSetup.Download(m => c.Out.WriteLine(m));
                    if (!got.Ok) return Refuse(c, got.Message);
                    c.Out.WriteLine(got.Message);
                }
                else c.Out.WriteLine($"Found {install.Describe()}.");
                var started = OpenRgbSetup.Start(settings, m => c.Out.WriteLine(m));
                if (!started.Ok) return Refuse(c, started.Message);
                c.Out.WriteLine(started.Message);
                if (OpenRgbSetup.UdevAdvice(OpenRgbSetup.Find()) is { } advice) c.Out.WriteLine(advice);
                return Done;
            }
            case ["udev"]:
            {
                var install = OpenRgbSetup.Find();
                if (Distribution.Distro.InSnap) return Refuse(c, OpenRgbSetup.SnapLimit()!);
                if (install is null) return Refuse(c, "no OpenRGB found: dispctrl-linux rgb setup downloads the official one");
                c.Out.WriteLine(OpenRgbSetup.UdevRulesInstalled() ? "OpenRGB's udev rules are installed (60-openrgb.rules)." : "OpenRGB's udev rules are not installed. As root, once (DispCtrl never runs sudo):");
                if (!OpenRgbSetup.UdevRulesInstalled()) c.Out.WriteLine("  " + OpenRgbSetup.UdevCommand(install));
                return Done;
            }
            case ["autostart", var onOff] when onOff is "on" or "off":
                SettingsStore.Update(s => s.Lighting.AutoStart = onOff == "on");
                c.Out.WriteLine($"Starting OpenRGB by itself: {onOff}.");
                return Done;
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
                return Wrong(c, "rgb takes list, colour COLOUR, order RGB|GRB|BGR..., preset list|save|apply|delete NAME, key NAMES COLOUR, keys NAME=COLOUR,... | FILE, off, mode NAME or server HOST[:PORT], and --device N");
        }

        static int Deleted(CommandContext c, string label)
        {
            c.Out.WriteLine($"Deleted '{label}'.");
            return Done;
        }

        static int Report(List<string> problems, string done, CommandContext c) => ReportResult(new LightingControl.Result(problems, []), done, c);

        static int ReportResult(LightingControl.Result result, string done, CommandContext c)
        {
            foreach (var p in result.Problems) c.Err.WriteLine($"dispctrl-linux: {p}");
            if (result.Problems.Count == 0) c.Out.WriteLine(done);
            foreach (var n in result.Notes) c.Out.WriteLine($"note: {n}");
            return result.Problems.Count == 0 ? Done : Refused;
        }

        static bool ListKernelLeds(CommandContext c, string why)
        {
            var leds = SysfsLeds.Enumerate();
            if (leds.Count == 0) return false;
            c.Out.WriteLine($"{why}: these are the keyboard backlights the kernel exposes (the basic path; dispctrl-linux rgb setup adds every other device and effects).");
            for (int i = 0; i < leds.Count; i++) c.Out.WriteLine($"{i + 1,2}  {leds[i].Describe()}");
            return true;
        }
    }
}
