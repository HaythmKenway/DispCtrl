using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.ViewModels;

/// <summary>One preset on the Presets page.</summary>
public sealed class PresetRowViewModel(Preset preset, string? mismatch)
{
    public Preset Preset { get; } = preset;
    public string Name => Preset.Name;

    public string Details
    {
        get
        {
            var parts = PresetParts.All.Where(Preset.Holds).Select(Label);
            return $"Saved {Preset.Saved:d MMM, HH:mm}  ·  {string.Join(", ", parts)}";
        }
    }

    /// <summary>Whether its displays are the ones attached, said plainly.</summary>
    public string Fit => Preset.Outputs.Count == 0 || !Preset.Holds(PresetParts.Layout)
        ? "Holds no layout"
        : mismatch is null ? "Matches the displays attached" : $"Not this desk: {mismatch}";

    public bool Fits => mismatch is null;

    public static string Label(string part) => part switch
    {
        PresetParts.Layout => "layout",
        PresetParts.Brightness => "brightness",
        PresetParts.NightLight => "night light and dimming",
        PresetParts.Theme => "theme",
        PresetParts.Controls => "monitor controls",
        _ => part,
    };
}
