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
    public string Fit => Preset.Description is { } about && Preset.Outputs.Count == 0
        ? about
        : Preset.Outputs.Count == 0 || !Preset.Holds(PresetParts.Layout)
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

/// <summary>A ready-made preset on the Presets page, to add.</summary>
public sealed class SuggestedPresetViewModel(Preset preset, bool added)
{
    public string Name => preset.Name;
    public string Description => preset.Description ?? "";
    public bool CanAdd => !added;
    public string ButtonText => added ? "Added" : "Add";

    public FluentAvalonia.UI.Controls.FASymbol Icon => preset.Name switch
    {
        "Day" => FluentAvalonia.UI.Controls.FASymbol.WeatherSunny,
        "Evening" => FluentAvalonia.UI.Controls.FASymbol.WeatherPartlyCloudyNight,
        "Night" => FluentAvalonia.UI.Controls.FASymbol.WeatherMoon,
        "Reading" => FluentAvalonia.UI.Controls.FASymbol.Library,
        "Movie" => FluentAvalonia.UI.Controls.FASymbol.Video,
        "Gaming" => FluentAvalonia.UI.Controls.FASymbol.Keyboard,
        _ => FluentAvalonia.UI.Controls.FASymbol.WeatherSunnyLow,
    };
}
