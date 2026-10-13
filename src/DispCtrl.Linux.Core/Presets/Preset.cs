using System.Text.Json;
using System.Text.Json.Serialization;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Settings;

/// <summary>A saved desk: the layout, each display's brightness, night light
/// and dimming, the theme and the monitors' own controls, as they were when it
/// was saved. Applying it puts back every part it holds and does not skip.</summary>
/// <remarks>
/// The Windows preset's parts, less the ones Linux has nothing for yet
/// (wallpaper, taskbar, windows). Monitors are remembered by model and serial,
/// outputs by name and model: ddcutil's display numbers change with the order
/// it finds monitors in, and a connector name alone would apply a layout made
/// for one monitor to another plugged into the same port.
/// </remarks>
public sealed class Preset
{
    public string Name { get; set; } = "";

    public DateTimeOffset Saved { get; set; }

    /// <summary>Parts left alone when applied (<see cref="PresetParts"/>).</summary>
    public List<string> Skip { get; set; } = [];

    public List<PresetOutput> Outputs { get; set; } = [];

    public List<PresetLevel> Brightness { get; set; } = [];

    public NightLightSettings? NightLight { get; set; }

    public Dictionary<string, double>? Dim { get; set; }

    /// <summary>Dark mode on; null when the desktop's theme could not be read.</summary>
    public bool? Dark { get; set; }

    public List<PresetControl> Controls { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public bool Holds(string part) => !Skip.Contains(part, StringComparer.Ordinal);

    public void Normalise()
    {
        Name = Name.Trim();
        Skip ??= [];
        Skip = Skip.Where(PresetParts.All.Contains).Distinct().ToList();
        Outputs ??= [];
        Brightness ??= [];
        Controls ??= [];
        NightLight?.Normalise();
    }
}

/// <summary>The parts a preset can hold, by the names the command line uses.</summary>
public static class PresetParts
{
    public const string Layout = "layout", Brightness = "brightness", NightLight = "nightlight", Theme = "theme", Controls = "controls";

    public static readonly IReadOnlyList<string> All = [Layout, Brightness, NightLight, Theme, Controls];
}

/// <summary>One output as the preset found it: lit or off, its mode, place
/// and turn, and whether it was the main display.</summary>
public sealed class PresetOutput
{
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public bool Enabled { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double RefreshHz { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Rotation { get; set; }
    public bool Primary { get; set; }
}

/// <summary>A brightness: a DDC/CI monitor by <c>model|serial</c>, or a
/// backlight by its device name.</summary>
public sealed class PresetLevel
{
    /// <summary>"ddc" or "backlight".</summary>
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public int Percent { get; set; }
}

/// <summary>A monitor's own control (contrast, volume, colour preset ...),
/// by <c>model|serial</c> and VCP code.</summary>
public sealed class PresetControl
{
    public string Monitor { get; set; } = "";
    public int Code { get; set; }
    public int Value { get; set; }
}
