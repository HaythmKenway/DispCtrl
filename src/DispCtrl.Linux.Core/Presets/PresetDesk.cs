using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Session;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Presets;

/// <summary>Saves the desk into a preset and puts a preset back.</summary>
/// <remarks>
/// Saving asks every DDC/CI monitor for its brightness and controls, a few
/// seconds each; applying writes only what differs from the desk now. Each
/// part is applied on its own, so a monitor that does not answer costs its
/// own brightness and nothing else, and every failure is reported.
/// </remarks>
public static class PresetDesk
{
    /// <summary>The monitors' own controls a preset keeps: picture, not the
    /// input (switching it away would take the picture with it).</summary>
    public static readonly IReadOnlyList<byte> SavedControls =
        [Ddcutil.VcpContrast, Ddcutil.VcpColourPreset, 0x16, 0x18, 0x1A, Ddcutil.VcpVolume, Ddcutil.VcpSharpness, Ddcutil.VcpPictureMode];

    public static Preset Capture(string name, IReadOnlyCollection<string> skip)
    {
        var preset = new Preset { Name = name, Saved = DateTimeOffset.Now, Skip = skip.ToList() };
        var settings = SettingsStore.Load();

        if (preset.Holds(PresetParts.Layout) && XRandR.IsAvailable)
        {
            var names = XRandR.MonitorNames();
            foreach (var o in XRandR.Query().Where(o => o.Connected))
            {
                bool turned = o.Rotation is 90 or 270;
                preset.Outputs.Add(new PresetOutput
                {
                    Name = o.Name, Model = names.GetValueOrDefault(o.Name), Enabled = o.Active,
                    Width = turned ? o.HeightPx : o.WidthPx, Height = turned ? o.WidthPx : o.HeightPx,
                    RefreshHz = o.RefreshHz, X = o.PosX, Y = o.PosY, Rotation = o.Rotation, Primary = o.Primary,
                });
            }
        }

        var monitors = (preset.Holds(PresetParts.Brightness) || preset.Holds(PresetParts.Controls)) && Ddcutil.IsAvailable
            ? Ddcutil.Detect() : [];
        if (preset.Holds(PresetParts.Brightness))
        {
            foreach (var b in Backlight.Enumerate())
                preset.Brightness.Add(new PresetLevel { Kind = "backlight", Id = b.Name, Percent = (int)Math.Round(b.Fraction * 100) });
            foreach (var m in monitors)
            {
                if (Ddcutil.GetVcp(m.DisplayNum, Ddcutil.VcpBrightness) is not { } level) continue;
                int max = level.Maximum is > 0 ? level.Maximum.Value : 100;
                preset.Brightness.Add(new PresetLevel { Kind = "ddc", Id = Ddcutil.KeyOf(m), Percent = (int)Math.Round(100.0 * level.Current / max) });
            }
        }

        if (preset.Holds(PresetParts.NightLight))
        {
            var n = settings.NightLight;
            preset.NightLight = new NightLightSettings { Enabled = n.Enabled, Strength = n.Strength, Scheduled = n.Scheduled, From = n.From, To = n.To };
            preset.Dim = new Dictionary<string, double>(settings.Dim, StringComparer.Ordinal);
        }

        if (preset.Holds(PresetParts.Theme)) preset.Dark = DarkMode.Read();

        if (preset.Holds(PresetParts.Controls))
        {
            foreach (var m in monitors)
            {
                var features = Ddcutil.Capabilities(m.DisplayNum);
                foreach (var code in SavedControls.Where(c => Ddcutil.IsWritable(features, c)))
                {
                    if (Ddcutil.GetVcp(m.DisplayNum, code) is { } value)
                        preset.Controls.Add(new PresetControl { Monitor = Ddcutil.KeyOf(m), Code = code, Value = value.Current });
                }
            }
        }
        return preset;
    }

    /// <summary>Puts the preset back; returns what could not be, one line each.
    /// Night light and dimming are saved to settings: the caller applies the ramps.</summary>
    public static List<string> Apply(Preset preset)
    {
        var problems = new List<string>();

        if (preset.Holds(PresetParts.Layout) && preset.Outputs.Count > 0)
        {
            if (!XRandR.IsAvailable) problems.Add("layout: no X display");
            else if (LayoutPlan(preset, XRandR.Query(), XRandR.MonitorNames(), out var plan, out var primary) is { } why) problems.Add($"layout: {why}");
            else
            {
                var changes = DisplayPlans.Changes(plan, XRandR.Query());
                bool primaryChanges = primary is not null && XRandR.Query().FirstOrDefault(o => o.Name == primary) is { Primary: false };
                if ((changes.Count > 0 || primaryChanges) && !XRandR.Configure(changes, primary, out var error)) problems.Add($"layout: {error}");
            }
        }

        var monitors = (preset.Holds(PresetParts.Brightness) || preset.Holds(PresetParts.Controls))
            && (preset.Brightness.Any(b => b.Kind == "ddc") || preset.Controls.Count > 0) && Ddcutil.IsAvailable
            ? Ddcutil.Detect() : [];

        if (preset.Holds(PresetParts.Brightness))
        {
            foreach (var level in preset.Brightness)
            {
                if (level.Kind == "backlight")
                {
                    var device = Backlight.Enumerate().FirstOrDefault(b => b.Name == level.Id);
                    if (device is null) { problems.Add($"brightness: no backlight {level.Id}"); continue; }
                    // Already there: a percentage rounds, and writing it back moved the panel a step.
                    if ((int)Math.Round(device.Fraction * 100) == level.Percent) continue;
                    if (!Backlight.TrySet(device.Name, (int)Math.Round(device.Max * level.Percent / 100.0), out var e)) problems.Add($"brightness: {e}");
                }
                else if (monitors.FirstOrDefault(m => Ddcutil.KeyOf(m) == level.Id) is { } m)
                {
                    var now = Ddcutil.GetVcp(m.DisplayNum, Ddcutil.VcpBrightness);
                    int max = now?.Maximum is int mx and > 0 ? mx : 100;
                    if (now is not null && (int)Math.Round(100.0 * now.Current / max) == level.Percent) continue;
                    if (!Ddcutil.SetVcp(m.DisplayNum, Ddcutil.VcpBrightness, (int)Math.Round(max * level.Percent / 100.0), out var e))
                        problems.Add($"brightness: {m.Model}: {e}");
                }
                else problems.Add($"brightness: {level.Id.Split('|')[0]} is not connected");
            }
        }

        if (preset.Holds(PresetParts.NightLight) && preset.NightLight is { } night)
        {
            SettingsStore.Update(s =>
            {
                s.NightLight.Enabled = night.Enabled;
                s.NightLight.Strength = night.Strength;
                s.NightLight.Scheduled = night.Scheduled;
                s.NightLight.From = night.From;
                s.NightLight.To = night.To;
                s.Dim = new Dictionary<string, double>(preset.Dim ?? [], StringComparer.Ordinal);
            });
        }

        if (preset.Holds(PresetParts.Theme) && preset.Dark is bool dark && DarkMode.Read() != dark && !DarkMode.Set(dark, out var themeError))
            problems.Add($"theme: {themeError}");

        if (preset.Holds(PresetParts.Controls))
        {
            foreach (var group in preset.Controls.GroupBy(c => c.Monitor))
            {
                if (monitors.FirstOrDefault(m => Ddcutil.KeyOf(m) == group.Key) is not { } m)
                {
                    problems.Add($"controls: {group.Key.Split('|')[0]} is not connected");
                    continue;
                }
                var features = Ddcutil.Capabilities(m.DisplayNum);
                foreach (var control in group)
                {
                    if (!Ddcutil.IsWritable(features, (byte)control.Code)) continue;
                    if (Ddcutil.GetVcp(m.DisplayNum, (byte)control.Code)?.Current == control.Value) continue;
                    if (!Ddcutil.SetVcp(m.DisplayNum, (byte)control.Code, control.Value, out var e))
                        problems.Add($"controls: {m.Model} 0x{control.Code:X2}: {e}");
                }
            }
        }
        return problems;
    }

    /// <summary>The plan that puts the preset's layout back on the outputs
    /// connected now; refused when one it lit is missing or holds another
    /// monitor.</summary>
    internal static string? LayoutPlan(Preset preset, IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, string> names,
        out List<OutputConfig> plan, out string? primary)
    {
        plan = [];
        primary = null;
        if (Mismatch(preset, outputs, names) is { } why) return why;
        foreach (var o in preset.Outputs)
        {
            if (!outputs.Any(x => x.Name == o.Name && x.Connected)) continue;
            plan.Add(o.Enabled
                ? new OutputConfig(o.Name, true, o.Width, o.Height, o.RefreshHz, o.X, o.Y, o.Rotation)
                : new OutputConfig(o.Name, false));
            if (o.Primary && o.Enabled) primary = o.Name;
        }
        if (!plan.Any(p => p.Enabled)) return "it would leave no display lit";
        return null;
    }

    /// <summary>Why the preset's displays are not the ones attached now; null
    /// when every display it lit is connected with the same monitor.</summary>
    public static string? Mismatch(Preset preset, IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, string> names)
    {
        foreach (var o in preset.Outputs.Where(o => o.Enabled))
        {
            if (!outputs.Any(x => x.Name == o.Name && x.Connected)) return $"{o.Model ?? o.Name} is not connected";
            if (o.Model is not null && names.GetValueOrDefault(o.Name) is { } now && now != o.Model)
                return $"{o.Name} now holds {now}, not {o.Model}";
        }
        return null;
    }
}
