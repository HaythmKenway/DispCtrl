using System.Globalization;
using System.Text.RegularExpressions;

namespace DispCtrl.Linux.Hardware;

/// <summary>One monitor as ddcutil enumerates it. ddcutil numbers displays in
/// enumeration order, which is not a stable identity across boots or
/// hot-plugs; a persistent cache should key on ddcutil's own
/// <c>&lt;mfg&gt;-&lt;model&gt;-&lt;product&gt;</c> convention instead.</summary>
public sealed record DdcMonitor(int DisplayNum, string? Model, string? Serial, string? I2CBus);

/// <summary>One VCP feature read back: the current value and, where the
/// monitor reports one, its maximum.</summary>
public sealed record DdcVcpValue(int Current, int? Maximum);

/// <summary>One VCP feature a monitor lists in its capabilities, with the
/// values it names for a feature that takes one of a list.</summary>
public sealed record DdcFeature(byte Code, string Name, IReadOnlyList<(byte Value, string Name)> Values)
{
    public bool IsList => Values.Count > 0;
}

/// <summary>Wraps the <c>ddcutil</c> command rather than binding libddcutil.</summary>
/// <remarks>
/// ddcutil already does what the Windows side had to learn the hard way: the
/// pause between messages, retries, and locking a bus against a second
/// ddcutil. A process per call costs a few hundred milliseconds, which is why
/// the window throttles slider writes rather than sending every step.
/// </remarks>
public static class Ddcutil
{
    public const byte VcpBrightness = 0x10;
    public const byte VcpContrast = 0x12;
    public const byte VcpColourPreset = 0x14;
    public const byte VcpInput = 0x60;
    public const byte VcpVolume = 0x62;
    public const byte VcpMute = 0x8D;
    public const byte VcpSharpness = 0x87;
    public const byte VcpPictureMode = 0xDC;

    /// <summary>Codes never written even when a monitor lists them: the factory
    /// resets (04 to 0A), degauss (01), "new control value" and "active
    /// control" (02, 52, which are the monitor's to set), the orientation it
    /// reports (AA, which turns nothing), its power mode (D6,
    /// which can leave it off with no way back from here), the application
    /// enable key and the version and type reports.</summary>
    private static readonly HashSet<byte> NeverWritten =
        [0x01, 0x02, 0x04, 0x05, 0x06, 0x08, 0x0A, 0x0C, 0x52, 0xAA, 0xAC, 0xAE, 0xB2, 0xB6, 0xC6, 0xC8, 0xC9, 0xD6, 0xDF];

    /// <summary>Whether <paramref name="code"/> may be written to a monitor
    /// whose capabilities are <paramref name="features"/>: it must be listed,
    /// standard (below E0: manufacturer codes mean something different on
    /// every model) and not a reset or a power switch. The Windows rule in
    /// <c>VcpControl.Settables</c>.</summary>
    public static bool IsWritable(IReadOnlyList<DdcFeature> features, byte code) =>
        code < 0xE0 && !NeverWritten.Contains(code) && features.Any(f => f.Code == code);

    /// <summary>A short name for each control the command line and the window
    /// offer by name.</summary>
    public static readonly IReadOnlyDictionary<string, byte> ControlNames = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["brightness"] = VcpBrightness,
        ["contrast"] = VcpContrast,
        ["colour-preset"] = VcpColourPreset,
        ["color-preset"] = VcpColourPreset,
        ["input"] = VcpInput,
        ["input-source"] = VcpInput,
        ["volume"] = VcpVolume,
        ["mute"] = VcpMute,
        ["sharpness"] = VcpSharpness,
        ["picture-mode"] = VcpPictureMode,
        ["red"] = 0x16,
        ["green"] = 0x18,
        ["blue"] = 0x1A,
    };

    /// <summary>The identity a preset remembers a monitor by: ddcutil's display
    /// number changes with the order monitors are found in, its model and
    /// serial do not.</summary>
    public static string KeyOf(DdcMonitor monitor) => $"{monitor.Model}|{monitor.Serial}";

    public static bool IsAvailable => Shell.TryWhich("ddcutil");

    // ddcutil also emits an "Invalid display" block for a bus with no working
    // DDC/CI (an eDP laptop panel: an EDID, but "DDC communication failed").
    // It must end the previous block, not merge into it - merged, the panel's
    // fields overwrote the real monitor's model, serial and bus.
    private static readonly Regex DisplayHeader = new(@"^Display (?<num>\d+)$", RegexOptions.Compiled);
    private static readonly Regex InvalidDisplayHeader = new(@"^Invalid display$", RegexOptions.Compiled);
    private static readonly Regex I2CBus = new(@"^\s*I2C bus:\s*(?<bus>\S+)", RegexOptions.Compiled);
    private static readonly Regex MonitorLine = new(@"^\s*Monitor:\s*(?<mfg>[^:]*):(?<model>[^:]*):(?<serial>.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<DdcMonitor> Detect()
    {
        var result = Shell.Run("ddcutil", ["detect", "--brief"]);
        return result.Ok ? ParseDetect(result.Stdout) : [];
    }

    internal static IReadOnlyList<DdcMonitor> ParseDetect(string stdout)
    {
        var results = new List<DdcMonitor>();
        int? currentNum = null;
        string? i2cBus = null;
        string? model = null;
        string? serial = null;

        void Flush()
        {
            if (currentNum is int n) results.Add(new DdcMonitor(n, model, serial, i2cBus));
            currentNum = null; i2cBus = null; model = null; serial = null;
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var header = DisplayHeader.Match(line);
            if (header.Success)
            {
                Flush();
                currentNum = int.Parse(header.Groups["num"].Value, CultureInfo.InvariantCulture);
                continue;
            }
            if (InvalidDisplayHeader.IsMatch(line))
            {
                // No display number to control it by, and a channel that did
                // not answer is never written to.
                Flush();
                continue;
            }
            if (currentNum is null) continue;

            var bus = I2CBus.Match(line);
            if (bus.Success) { i2cBus = bus.Groups["bus"].Value; continue; }

            var monitor = MonitorLine.Match(line);
            if (monitor.Success)
            {
                model = NullIfEmpty(monitor.Groups["model"].Value.Trim());
                serial = NullIfEmpty(monitor.Groups["serial"].Value.Trim());
            }
        }
        Flush();
        return results;
    }

    // "VCP 10 C 62 100" - current 62, max 100. Some read-only codes omit the max.
    // "VCP 60 SNC x0f" - a value from a list, in hex, with no maximum.
    private static readonly Regex VcpLine = new(
        @"VCP\s+[0-9A-Fa-f]{2}\s+[A-Z]+\s+(?:x(?<hex>[0-9A-Fa-f]+)|(?<cur>\d+)(?:\s+(?<max>\d+))?)",
        RegexOptions.Compiled);

    private static readonly Regex FeatureLine = new(@"^\s*Feature:\s*(?<code>[0-9A-Fa-f]{2})\s*\((?<name>.*)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex ValueLine = new(@"^\s*(?<value>[0-9A-Fa-f]{2}):\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>The features a monitor lists, from <c>ddcutil capabilities</c>;
    /// empty when it does not answer. A few seconds: ask once and keep it.</summary>
    public static IReadOnlyList<DdcFeature> Capabilities(int displayNum)
    {
        var result = Shell.Run("ddcutil", ["--display", displayNum.ToString(CultureInfo.InvariantCulture), "capabilities"]);
        return result.Ok ? ParseCapabilities(result.Stdout) : [];
    }

    internal static IReadOnlyList<DdcFeature> ParseCapabilities(string stdout)
    {
        var features = new List<DdcFeature>();
        bool inFeatures = false;
        byte code = 0;
        string? name = null;
        List<(byte, string)> values = [];

        void Flush()
        {
            if (name is not null) features.Add(new DdcFeature(code, name, values));
            name = null;
            values = [];
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("VCP Features:", StringComparison.Ordinal)) { inFeatures = true; continue; }
            if (!inFeatures) continue;
            // Anything back at the left margin ends the list.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { Flush(); inFeatures = false; continue; }

            var feature = FeatureLine.Match(line);
            if (feature.Success)
            {
                Flush();
                code = byte.Parse(feature.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                name = feature.Groups["name"].Value.Trim();
                continue;
            }
            var value = ValueLine.Match(line);
            if (value.Success && name is not null)
            {
                var label = value.Groups["name"].Value;
                // ddcutil's word for a value outside MCCS; offering it would
                // write something nobody can name.
                if (label.Equals("Unrecognized value", StringComparison.OrdinalIgnoreCase)) continue;
                values.Add((byte.Parse(value.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), label));
            }
        }
        Flush();
        return features;
    }

    public static DdcVcpValue? GetVcp(int displayNum, byte vcpCode)
    {
        // --brief is required: the default output is prose ("VCP code 0x10
        // (Brightness ): current value = 70, max value = 100") which this
        // never matched, so every read silently returned nothing.
        var result = Shell.Run("ddcutil", ["--display", displayNum.ToString(CultureInfo.InvariantCulture), "getvcp", $"x{vcpCode:x2}", "--brief"]);
        return result.Ok ? ParseVcp(result.Stdout) : null;
    }

    internal static DdcVcpValue? ParseVcp(string stdout)
    {
        var match = VcpLine.Match(stdout);
        if (!match.Success) return null;
        if (match.Groups["hex"].Success)
            return new DdcVcpValue(int.Parse(match.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), null);
        int current = int.Parse(match.Groups["cur"].Value, CultureInfo.InvariantCulture);
        int? max = match.Groups["max"].Success ? int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture) : null;
        return new DdcVcpValue(current, max);
    }

    /// <summary>Writes a VCP code. Callers check <see cref="IsWritable"/> first,
    /// except for brightness (0x10), which every DDC/CI monitor lists - the
    /// same rule as <c>VcpControl.Settables</c> on Windows: never a code the
    /// monitor did not list, never a manufacturer-specific one.</summary>
    public static bool SetVcp(int displayNum, byte vcpCode, int value, out string? error)
    {
        var result = Shell.Run("ddcutil", ["--display", displayNum.ToString(CultureInfo.InvariantCulture), "setvcp", $"x{vcpCode:x2}", value.ToString(CultureInfo.InvariantCulture)]);
        error = result.Ok ? null : FirstLine(result.Stderr) ?? FirstLine(result.Stdout) ?? $"ddcutil exited with {result.ExitCode}";
        return result.Ok;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string? FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
