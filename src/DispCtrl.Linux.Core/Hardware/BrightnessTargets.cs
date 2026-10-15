namespace DispCtrl.Linux.Hardware;

/// <summary>One display whose own brightness can be set: a laptop's backlight,
/// or a monitor over DDC/CI.</summary>
/// <param name="Key"><c>backlight:NAME</c> or <c>ddc:MODEL|SERIAL</c>: the
/// identity settings keep, which ddcutil's display numbers are not.</param>
public sealed record BrightnessTarget(string Key, string Label, int? DdcDisplay, string? Backlight)
{
    /// <summary>Its level now, 0 to 100; null when it did not answer.</summary>
    public int? Read()
    {
        if (Backlight is { } name)
            return Hardware.Backlight.Enumerate().FirstOrDefault(b => b.Name == name) is { } b ? (int)Math.Round(b.Fraction * 100) : null;
        if (DdcDisplay is int n && Ddcutil.GetVcp(n, Ddcutil.VcpBrightness) is { } v)
            return (int)Math.Round(100.0 * v.Current / (v.Maximum is > 0 ? v.Maximum.Value : 100));
        return null;
    }

    /// <summary>Sets it to <paramref name="percent"/>; the reason when it would not.</summary>
    public string? Write(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (Backlight is { } name)
        {
            var device = Hardware.Backlight.Enumerate().FirstOrDefault(b => b.Name == name);
            if (device is null) return $"no backlight {name}";
            return Hardware.Backlight.TrySet(name, (int)Math.Round(device.Max * percent / 100.0), out var e) ? null : e;
        }
        if (DdcDisplay is int n)
        {
            int max = Ddcutil.GetVcp(n, Ddcutil.VcpBrightness)?.Maximum is int m and > 0 ? m : 100;
            return Ddcutil.SetVcp(n, Ddcutil.VcpBrightness, (int)Math.Round(max * percent / 100.0), out var e) ? null : $"{Label}: {e}";
        }
        return "nothing to write to";
    }
}

/// <summary>Every display whose own brightness can be set here.</summary>
/// <remarks>
/// ddcutil takes a second or more to find monitors, and a brightness key held
/// down asks many times a second; inside the engine the list is kept for a
/// minute. A monitor plugged in meanwhile is found at the next refresh.
/// </remarks>
public static class BrightnessTargets
{
    private static readonly Lock Gate = new();
    private static (DateTime At, IReadOnlyList<BrightnessTarget> List)? _cache;

    public static IReadOnlyList<BrightnessTarget> List(TimeSpan? maxAge = null)
    {
        lock (Gate)
        {
            if (maxAge is { } age && _cache is { } c && DateTime.UtcNow - c.At < age) return c.List;
            var list = new List<BrightnessTarget>();
            foreach (var b in Backlight.Enumerate()) list.Add(new($"backlight:{b.Name}", "Built-in display", null, b.Name));
            if (Ddcutil.IsAvailable)
                foreach (var m in Ddcutil.Detect())
                    list.Add(new($"ddc:{Ddcutil.KeyOf(m)}", m.Model ?? $"Display {m.DisplayNum}", m.DisplayNum, null));
            _cache = (DateTime.UtcNow, list);
            return list;
        }
    }

    /// <summary>The level a target takes for unison's <paramref name="level"/>,
    /// within its own range: a dim panel and a bright one reach their own
    /// lowest and highest together.</summary>
    public static int Scale(int level, int min, int max) =>
        (int)Math.Round(min + (max - min) * Math.Clamp(level, 0, 100) / 100.0);
}
