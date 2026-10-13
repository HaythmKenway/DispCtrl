using System.Globalization;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>monitor</c>: a monitor's own controls over DDC/CI - only
    /// those it lists, never a reset, a power switch or a manufacturer code.</summary>
    private static int MonitorCommand(string[] args, CommandContext c)
    {
        if (!Ddcutil.IsAvailable) return Refuse(c, Distro.DdcutilAdvice);
        if (args is [] or ["list"])
        {
            var monitors = Ddcutil.Detect();
            if (monitors.Count == 0) return Refuse(c, "no monitor answered over DDC/CI (a built-in panel never does)");
            foreach (var m in monitors) c.Out.WriteLine($"{m.DisplayNum,-3} {m.Model ?? "(model unknown)",-20} {m.I2CBus}");
            return Done;
        }
        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int display) || display < 1)
            return Wrong(c, "monitor takes ddcutil's display number first (monitor list names them)");

        switch (args[1..])
        {
            case [] or ["list"]:
                return MonitorControls(display, c);
            case ["get", var control]:
                if (!TryControl(control, out byte code)) return Wrong(c, UnknownControl(control));
                if (Ddcutil.GetVcp(display, code) is not { } value) return Refuse(c, $"display {display} did not answer for 0x{code:X2}");
                var feature = Ddcutil.Capabilities(display).FirstOrDefault(f => f.Code == code);
                c.Out.WriteLine($"0x{code:X2} {ValueText(feature, value)}");
                return Done;
            case ["set", var setControl, var setValue]:
                return MonitorSet(display, setControl, setValue, c);
            default:
                return Wrong(c, "monitor <n> takes list, get <control> or set <control> <value>");
        }
    }

    /// <summary><c>input &lt;n&gt; [source]</c>: the input sources a monitor lists, or switch to one.</summary>
    private static int InputCommand(string[] args, CommandContext c)
    {
        if (!Ddcutil.IsAvailable) return Refuse(c, Distro.DdcutilAdvice);
        if (args.Length is 0 or > 2 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int display) || display < 1)
            return Wrong(c, "input takes ddcutil's display number, and a source to switch to (input 1 lists them)");
        if (args.Length == 2) return MonitorSet(display, "input", args[1], c);

        var feature = Ddcutil.Capabilities(display).FirstOrDefault(f => f.Code == Ddcutil.VcpInput);
        if (feature is null) return Refuse(c, $"display {display} lists no input sources");
        int? current = Ddcutil.GetVcp(display, Ddcutil.VcpInput)?.Current;
        foreach (var (v, name) in feature.Values)
            c.Out.WriteLine($"{(current == v ? "*" : " ")} 0x{v:X2}  {name}");
        return Done;
    }

    private static int MonitorControls(int display, CommandContext c)
    {
        var features = Ddcutil.Capabilities(display);
        if (features.Count == 0) return Refuse(c, $"display {display} did not give its capabilities");
        foreach (var f in features)
        {
            bool writable = Ddcutil.IsWritable(features, f.Code);
            if (!writable && f.Code != Ddcutil.VcpInput) continue;
            var value = Ddcutil.GetVcp(display, f.Code);
            string name = Ddcutil.ControlNames.FirstOrDefault(n => n.Value == f.Code && !n.Key.Contains("color", StringComparison.Ordinal)).Key ?? f.Name;
            c.Out.WriteLine($"0x{f.Code:X2}  {name,-16} {(value is null ? "unreadable" : ValueText(f, value))}");
            if (f.IsList) c.Out.WriteLine($"      {string.Join(", ", f.Values.Select(v => $"{v.Name} (0x{v.Value:X2})"))}");
        }
        return Done;
    }

    private static int MonitorSet(int display, string control, string valueText, CommandContext c)
    {
        if (!TryControl(control, out byte code)) return Wrong(c, UnknownControl(control));
        var features = Ddcutil.Capabilities(display);
        if (features.Count == 0) return Refuse(c, $"display {display} did not give its capabilities, so nothing is written to it");
        if (!Ddcutil.IsWritable(features, code))
            return Refuse(c, features.Any(f => f.Code == code)
                ? $"0x{code:X2} is not written: resets, power and manufacturer codes are left to the monitor's own menu"
                : $"display {display} does not list 0x{code:X2}, so it is not written");

        var feature = features.First(f => f.Code == code);
        int value;
        if (feature.IsList)
        {
            var match = feature.Values.FirstOrDefault(v => v.Name.Replace(" ", "", StringComparison.Ordinal)
                .Equals(valueText.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
            if (match.Name is not null) value = match.Value;
            else if (TryNumber(valueText, out value) && feature.Values.Any(v => v.Value == value)) { }
            else return Wrong(c, $"0x{code:X2} takes one of: {string.Join(", ", feature.Values.Select(v => v.Name))}");
        }
        else
        {
            if (!TryNumber(valueText, out value) || value < 0) return Wrong(c, $"0x{code:X2} takes a number");
            int max = Ddcutil.GetVcp(display, code)?.Maximum ?? 100;
            if (value > max) return Wrong(c, $"0x{code:X2} goes up to {max}");
        }
        if (!Ddcutil.SetVcp(display, code, value, out var error)) return Refuse(c, $"display {display} refused 0x{code:X2}: {error}");
        c.Out.WriteLine($"display {display}: {feature.Name} {(feature.IsList ? feature.Values.First(v => v.Value == value).Name : value.ToString(CultureInfo.InvariantCulture))}");
        return Done;
    }

    private static bool TryControl(string text, out byte code)
    {
        if (Ddcutil.ControlNames.TryGetValue(text, out code)) return true;
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
    }

    private static bool TryNumber(string text, out int value) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string UnknownControl(string control) =>
        $"'{control}' is not a control: a code such as 0x12, or {string.Join(", ", Ddcutil.ControlNames.Keys.Where(k => !k.Contains("color", StringComparison.Ordinal)))}";

    private static string ValueText(Hardware.DdcFeature? feature, DdcVcpValue value)
    {
        if (feature is { IsList: true })
        {
            var named = feature.Values.FirstOrDefault(v => v.Value == value.Current);
            return named.Name is not null ? $"{named.Name} (0x{value.Current:X2})" : $"0x{value.Current:X2}";
        }
        return value.Maximum is int max ? $"{value.Current} of {max}" : value.Current.ToString(CultureInfo.InvariantCulture);
    }
}
