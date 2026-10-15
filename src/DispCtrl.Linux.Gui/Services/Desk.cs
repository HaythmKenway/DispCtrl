using System.Collections.ObjectModel;
using System.Globalization;
using DispCtrl.Linux.Gui.ViewModels;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>The displays and everything on them that can be written, found
/// once and shared by every page.</summary>
/// <remarks>
/// One object per target, not one per page: the dimming slider on Displays and
/// the one on Brightness are the same view model, so moving either moves both
/// and neither writes back what the other just sent - the Windows app's rule
/// that a feature shows the same state wherever it appears.
/// </remarks>
public sealed class Desk
{
    public static Desk Current { get; } = new();

    public CommandSender Sender { get; } = new();

    /// <summary>Each lit output, in XRandR's order: the numbers on the
    /// arrangement's tiles and on Identify.</summary>
    public ObservableCollection<OutputViewModel> Outputs { get; } = [];

    /// <summary>Brightness set in the hardware: each DDC/CI monitor and backlight.</summary>
    public ObservableCollection<MonitorRowViewModel> Brightness { get; } = [];

    /// <summary>Software dimming, one per output with a gamma ramp.</summary>
    public ObservableCollection<MonitorRowViewModel> Dimming { get; } = [];

    /// <summary>What the last scan found missing, or null.</summary>
    public string? ScanStatus { get; private set; }

    public bool Scanning { get; private set; }

    /// <summary>The outputs or the scan's state changed; raised on the UI thread.</summary>
    public event Action? Changed;

    private int _generation;

    /// <summary>Every output as X last answered, lit or not, for the pages
    /// that need to know what is connected but switched off.</summary>
    public IReadOnlyList<XRandROutput> AllOutputs { get; private set; } = [];

    /// <summary>The layout only: positions, modes and the main display. Quick,
    /// so it follows an arrangement change without asking DDC/CI again.</summary>
    public async Task RefreshOutputsAsync()
    {
        var (outputs, names, modes) = await Task.Run(() => (XRandR.Query(), XRandR.MonitorNames(), XRandR.Modes()));
        AllOutputs = outputs;
        var lit = outputs.Where(o => o.Active).ToList();
        var byName = Outputs.ToDictionary(o => o.Output.Name, StringComparer.Ordinal);
        var views = new List<OutputViewModel>();
        for (int i = 0; i < lit.Count; i++)
        {
            var o = lit[i];
            names.TryGetValue(o.Name, out var model);
            var view = byName.TryGetValue(o.Name, out var old) ? old : new OutputViewModel();
            view.Update(o, i + 1, model, modes.GetValueOrDefault(o.Name));
            view.Dim = Dimming.FirstOrDefault(d => d.Key == DimKey(o.Name));
            views.Add(view);
        }
        // Updated in place: clearing and refilling the collection rebuilt every
        // expander, which flashed and collapsed the ones that were open.
        for (int i = Outputs.Count - 1; i >= 0; i--)
            if (!views.Contains(Outputs[i])) Outputs.RemoveAt(i);
        for (int i = 0; i < views.Count; i++)
        {
            if (i < Outputs.Count && Outputs[i] == views[i]) continue;
            if (Outputs.Contains(views[i])) Outputs.Move(Outputs.IndexOf(views[i]), i);
            else Outputs.Insert(i, views[i]);
        }
        AttachBrightness();
        Changed?.Invoke();
    }

    /// <summary>Finds every writable target on this machine now. Off the UI
    /// thread: ddcutil takes seconds to detect and a few hundred milliseconds
    /// per read, and the window used to freeze for all of it.</summary>
    public async Task RescanAsync()
    {
        int generation = ++_generation;
        Scanning = true;
        SetStatus("Looking for displays…");
        Brightness.Clear();
        Dimming.Clear();
        _controls.Clear();

        var settings = SettingsStore.Load();
        var (ramps, names) = await Task.Run(() => (GammaRamp.Outputs(), XRandR.MonitorNames()));
        foreach (var output in ramps)
        {
            double dim = settings.Dim.TryGetValue(output.Name, out var d) ? d : 1;
            string label = names.TryGetValue(output.Name, out var model) ? model
                : OutputViewModel.BuiltInConnector(output.Name) ? "Built-in display" : output.Name;
            Dimming.Add(new MonitorRowViewModel(
                title: label == output.Name ? label : $"{label} ({output.Name})",
                subtitle: "Through the gamma ramp; the monitor's own brightness is unchanged",
                minimum: RampTarget.LowestDim * 100, maximum: 100, initialValue: dim * 100, unit: "%",
                Sender, key: DimKey(output.Name),
                command: v => ["dim", (v / 100).ToString("0.##", CultureInfo.InvariantCulture), "--output", output.Name],
                icon: FASymbol.WeatherSunnyLow));
        }
        if (generation != _generation) return;
        await RefreshOutputsAsync();

        foreach (var device in await Task.Run(Backlight.Enumerate))
        {
            if (generation != _generation) return;
            Brightness.Add(new MonitorRowViewModel(
                title: "Built-in display",
                subtitle: $"The panel's backlight ({device.Name})",
                minimum: 0, maximum: 100, initialValue: Math.Round(device.Fraction * 100), unit: "%",
                Sender, key: $"backlight:{device.Name}",
                command: v => ["brightness", ((int)v).ToString(CultureInfo.InvariantCulture), "--backlight", device.Name],
                icon: FASymbol.WeatherSunnyHigh) { Model = BuiltIn });
        }

        if (Ddcutil.IsAvailable)
        {
            SetStatus("Asking DDC/CI monitors (a few seconds)…");
            var monitors = await Task.Run(() => Ddcutil.Detect()
                .Select(m => (Monitor: m, Level: Ddcutil.GetVcp(m.DisplayNum, Ddcutil.VcpBrightness)))
                .ToList());
            if (generation != _generation) return;
            foreach (var (monitor, level) in monitors)
            {
                if (level is null) continue;
                _controls[monitor.Model ?? ""] = await Task.Run(() => ReadControls(monitor.DisplayNum));
                if (generation != _generation) return;
                Brightness.Add(new MonitorRowViewModel(
                    title: monitor.Model ?? $"Display {monitor.DisplayNum}",
                    subtitle: "Set in the monitor itself, over DDC/CI",
                    minimum: 0, maximum: level.Maximum ?? 100, initialValue: level.Current,
                    unit: (level.Maximum ?? 100) == 100 ? "%" : "",
                    Sender, key: $"ddc:{monitor.DisplayNum}",
                    command: v => ["brightness", ((int)v).ToString(CultureInfo.InvariantCulture), "--ddc", monitor.DisplayNum.ToString(CultureInfo.InvariantCulture)],
                    icon: FASymbol.WeatherSunnyHigh) { Model = monitor.Model });
            }
            Scanning = false;
            SetStatus(monitors.Count == 0
                ? "No monitor answered over DDC/CI. A built-in panel never does; for an external one, see dispctrl-linux doctor."
                : null);
        }
        else
        {
            Scanning = false;
            SetStatus(Distribution.Distro.InSnap
                ? "External monitors' own brightness cannot be changed from the snap: strict confinement keeps it from DDC/CI. The .deb, .rpm or tarball can."
                : "ddcutil is not installed, so external monitors' own brightness cannot be changed.");
        }
        AttachBrightness();
        Changed?.Invoke();
    }

    private const string BuiltIn = "\0built-in";

    // Each DDC/CI monitor's controls by model, read once a scan: capabilities
    // take seconds, and the card should not wait for them again on each refresh.
    private readonly Dictionary<string, IReadOnlyList<ControlRowViewModel>> _controls = new(StringComparer.Ordinal);

    private List<ControlRowViewModel> ReadControls(int display)
    {
        var features = Ddcutil.Capabilities(display);
        var rows = new List<ControlRowViewModel>();
        foreach (var code in ControlRowViewModel.Offered)
        {
            if (!Ddcutil.IsWritable(features, code)) continue;
            var feature = features.First(f => f.Code == code);
            if (Ddcutil.GetVcp(display, code) is not { } value) continue;
            rows.Add(new ControlRowViewModel(Sender, display, feature, value));
        }
        return rows;
    }

    /// <summary>Puts each brightness control on its display's card, where the
    /// pairing is certain: the backlight on the one built-in output, a DDC/CI
    /// monitor on the one output whose EDID carries the same model name.</summary>
    private void AttachBrightness()
    {
        foreach (var output in Outputs)
        {
            string? model = output.IsBuiltIn ? BuiltIn : output.Model;
            bool unique = model is not null && Outputs.Count(o => (o.IsBuiltIn ? BuiltIn : o.Model) == model) == 1;
            var rows = unique ? Brightness.Where(b => b.Model == model).ToList() : [];
            output.Brightness = rows.Count == 1 ? rows[0] : null;
            output.Controls = rows.Count == 1 && rows[0].Model is { } m && _controls.TryGetValue(m, out var c) ? c : [];
        }
    }

    private void SetStatus(string? text)
    {
        ScanStatus = text;
        Changed?.Invoke();
    }

    private static string DimKey(string output) => $"dim:{output}";
}
