using Avalonia.Platform;
using Avalonia.Threading;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Gui.Services.Tray;
using DispCtrl.Linux.Settings;
using SkiaSharp;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>DispCtrl's icon in the panel's tray (<see cref="StatusNotifierItem"/>),
/// with a menu of everything worth one click: the Windows tray menu and most
/// of the quick panel's toggles. The wheel over it moves every display's
/// brightness, as on Windows.</summary>
/// <remarks>
/// KDE, XFCE, Cinnamon, MATE, Budgie and LXQt show it as they are; GNOME needs
/// the AppIndicator extension, which Ubuntu ships switched on. The menu is
/// rebuilt from settings whenever they change, so a switch flipped on the
/// command line or in the window is ticked here too.
/// </remarks>
public sealed class TrayHost : IDisposable
{
    private readonly StatusNotifierItem _icon;
    private readonly Action _openWindow;
    private readonly Action<string> _openPage;
    private readonly Action _openPanel;
    private readonly Action _quit;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _timer;
    private int _rebuilding;

    private TrayHost(StatusNotifierItem icon, Action openWindow, Action<string> openPage, Action openPanel, Action quit)
    {
        _icon = icon;
        _openWindow = openWindow;
        _openPage = openPage;
        _openPanel = openPanel;
        _quit = quit;
        _icon.Activated += () =>
        {
            try
            {
                bool panel = SettingsStore.Load().Tray.QuickPanel;
                Log.Write($"tray click: {(panel ? "quick panel" : "window")}");
                if (panel) _openPanel();
                else _openWindow();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"tray click failed: {ex}"); }
        };
        _icon.Reregistered += () => Dispatcher.UIThread.Post(Rebuild);
        _icon.Scrolled += OnScroll;

        DeskActions.Changed += Rebuild;
        Desk.Current.Changed += Rebuild;
        _watcher = Watch();
        // The engine starting or stopping, and a timed keep awake running out,
        // change nothing this process watches.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Rebuild());
        _timer.Start();
        Rebuild();
    }

    /// <summary>Puts the icon in the tray; throws when there is no session bus
    /// or no tray to register with.</summary>
    public static async Task<TrayHost> StartAsync(Action openWindow, Action<string> openPage, Action openPanel, Action quit, bool waitForHost = false)
    {
        var icon = await StatusNotifierItem.StartAsync(Icons(), waitForHost);
        return new TrayHost(icon, openWindow, openPage, openPanel, quit);
    }

    /// <summary>DispCtrl's icon at the sizes trays ask for, as ARGB32 in network
    /// byte order, unpremultiplied: what StatusNotifierItem's IconPixmap carries.</summary>
    private static (int, int, byte[])[] Icons()
    {
        using var png = AssetLoader.Open(new Uri("avares://dispctrl-linux-gui/Assets/dispctrl.png"));
        using var source = SKBitmap.Decode(png);
        return [.. new[] { 22, 32, 48, 64 }.Select(size =>
        {
            using var scaled = source.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            var argb = new byte[size * size * 4];
            int i = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = scaled.GetPixel(x, y);
                argb[i++] = c.Alpha; argb[i++] = c.Red; argb[i++] = c.Green; argb[i++] = c.Blue;
            }
            return (size, size, argb);
        })];
    }

    /// <summary>The wheel over the icon: every display's hardware brightness by
    /// five, as the Windows tray icon does.</summary>
    private void OnScroll(int delta)
    {
        int step = delta > 0 ? 5 : -5;
        foreach (var row in Desk.Current.Brightness.Where(r => r.IsInteractive))
        {
            double span = row.Maximum - row.Minimum;
            row.Value = Math.Clamp(row.Value + span * step / 100.0, row.Minimum, row.Maximum);
        }
    }

    private FileSystemWatcher? Watch()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Directory);
            var watcher = new FileSystemWatcher(SettingsStore.Directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            void Changed(object? s, FileSystemEventArgs e) => Dispatcher.UIThread.Post(Rebuild);
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Rebuild()
    {
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return;
        _ = RebuildAsync();
    }

    private async Task RebuildAsync()
    {
        try
        {
            var state = await DeskActions.ReadAsync();
            if (!state.Settings.Tray.Enabled) { _quit(); return; }
            _icon.SetMenu(Build(state));
            _icon.SetToolTip(Tooltip(state));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"tray menu rebuild failed: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _rebuilding, 0); }
    }

    private static string Tooltip(DeskActions.State s)
    {
        var parts = new List<string> { "DispCtrl" };
        if (s.Settings.NightLight.Enabled) parts.Add("night light on");
        if (s.Settings.Awake.ActiveAt(DateTimeOffset.Now)) parts.Add("keeping awake");
        if (s.Settings.Awake.StayActive) parts.Add("stay active");
        if (!s.Engine) parts.Add("engine not running");
        return string.Join(" · ", parts);
    }

    private List<TrayMenuItem> Build(DeskActions.State s)
    {
        var menu = new List<TrayMenuItem>
        {
            new("Open DispCtrl", _openWindow),
            new("Quick panel", _openPanel),
            TrayMenuItem.Separator(),
        };

        // Brightness: each display's own, in steps.
        var brightness = new TrayMenuItem("Brightness");
        var every = new TrayMenuItem(s.Settings.Unison.Enabled ? "Every display (unison)" : "Every display");
        foreach (int pct in new[] { 100, 75, 50, 25, 10 })
        {
            string level = pct.ToString(System.Globalization.CultureInfo.InvariantCulture);
            every.Add(s.Settings.Unison.Enabled
                ? Radio($"{pct}%", s.Settings.Unison.Level == pct, () => _ = DeskActions.RunAsync("unison", level))
                : new TrayMenuItem($"{pct}%", () => _ = DeskActions.RunAsync("brightness", level, "--all")));
        }
        brightness.Add(every);
        brightness.Add(TrayMenuItem.Separator());
        foreach (var row in Desk.Current.Brightness.Where(r => r.IsInteractive))
        {
            var steps = new TrayMenuItem(row.Title);
            foreach (int pct in new[] { 100, 75, 50, 25, 10, 0 })
            {
                double value = row.Minimum + (row.Maximum - row.Minimum) * pct / 100.0;
                steps.Add(Radio($"{pct}%", Math.Abs(row.Value - value) < (row.Maximum - row.Minimum) / 16.0, () => row.Value = value));
            }
            brightness.Add(steps);
        }
        if (brightness.Children.Count == 0)
            brightness.Add(new TrayMenuItem(Desk.Current.Scanning ? "Looking for displays…" : "No display's brightness can be set") { Enabled = false });
        menu.Add(brightness);

        var n = s.Settings.NightLight;
        menu.Add(Check("Night light", n.Enabled, () => _ = DeskActions.NightLight(!n.Enabled), s.NightLightAvailable || n.Enabled));
        var strength = new TrayMenuItem("Night light strength") { Enabled = s.NightLightAvailable };
        foreach (var (label, value) in new[] { ("Light", 25), ("Medium", 50), ("Strong", 75), ("Strongest", 100) })
            strength.Add(Radio($"{label} ({Ramps.Warmth.KelvinFor(value):0} K)", n.Strength == value, () => _ = DeskActions.NightLightStrength(value)));
        menu.Add(strength);
        if (s.Dark is bool dark) menu.Add(Check("Dark mode", dark, () => _ = DeskActions.DarkMode(!dark)));
        menu.Add(TrayMenuItem.Separator());

        var a = s.Settings.Awake;
        bool awake = a.ActiveAt(DateTimeOffset.Now);
        string awakeLabel = !awake ? "Keep awake" : a.Mode == AwakeMode.Until && a.Until is { } until
            ? $"Keep awake (until {until:HH:mm})" : "Keep awake (on)";
        var awakeMenu = new TrayMenuItem(awakeLabel)
            .Add(Radio("Off", !awake, () => _ = DeskActions.Awake("off")))
            .Add(Radio("Until I switch it off", awake && a.Mode == AwakeMode.On, () => _ = DeskActions.Awake("on")));
        foreach (var (label, span) in new[] { ("For 30 minutes", "30m"), ("For 1 hour", "1h"), ("For 2 hours", "2h"), ("For 4 hours", "4h") })
            awakeMenu.Add(new TrayMenuItem(label, () => _ = DeskActions.Awake(span)));
        awakeMenu.Add(TrayMenuItem.Separator());
        awakeMenu.Add(Check("Keep the displays on", a.KeepDisplaysOn, () => _ = DeskActions.RunAsync("awake", "displays", a.KeepDisplaysOn ? "off" : "on")));
        menu.Add(awakeMenu);
        menu.Add(Check("Stay active", a.StayActive, () => _ = DeskActions.StayActive(!a.StayActive)));
        menu.Add(Check("Focus mode", s.Settings.Focus.Enabled, () => _ = DeskActions.RunAsync("focus", "toggle")));
        menu.Add(Check("OLED care", s.Settings.OledCare.Enabled, () => _ = DeskActions.RunAsync("oled", "toggle")));
        menu.Add(new TrayMenuItem("Rest the displays now", () => _ = DeskActions.RunAsync("oled", "rest")) { Enabled = s.Engine });
        menu.Add(new TrayMenuItem("Turn off displays", () => _ = DeskActions.DisplaysOff()));
        menu.Add(TrayMenuItem.Separator());

        // Display mode, only where there is a choice to make.
        var connected = s.Connected;
        if (connected.Count >= 2)
        {
            var project = new TrayMenuItem("Display mode")
                .Add(new TrayMenuItem("Extend", () => _ = DeskActions.Project("extend")))
                .Add(new TrayMenuItem("Duplicate", () => _ = DeskActions.Project("duplicate")));
            if (connected.Any(o => DisplayPlans.IsBuiltIn(o.Name)))
            {
                project.Add(new TrayMenuItem("Built-in display only", () => _ = DeskActions.Project("internal")));
                project.Add(new TrayMenuItem("Other displays only", () => _ = DeskActions.Project("external")));
            }
            menu.Add(project);
        }
        menu.Add(new TrayMenuItem("Identify displays", DeskActions.Identify));

        var presets = new TrayMenuItem("Presets");
        foreach (var p in s.Settings.Presets)
        {
            string name = p.Name;
            presets.Add(new TrayMenuItem(name, () => _ = DeskActions.ApplyPreset(name)));
        }
        if (s.Settings.Presets.Count > 0) presets.Add(TrayMenuItem.Separator());
        presets.Add(new TrayMenuItem("Save the desk as a preset…", () => _openPage("presets")));
        menu.Add(presets);

        var lighting = new TrayMenuItem("Lighting");
        foreach (var (label, colour) in new[] { ("Warm", "warm"), ("White", "white"), ("Red", "red"), ("Blue", "blue"), ("Purple", "purple") })
            lighting.Add(Radio(label, string.Equals(s.Settings.Lighting.Last, Lighting.LightingControl.Named[colour], StringComparison.OrdinalIgnoreCase),
                () => _ = DeskActions.RunAsync("rgb", "colour", colour)));
        lighting.Add(TrayMenuItem.Separator());
        lighting.Add(Radio("Off", s.Settings.Lighting.Last == "off", () => _ = DeskActions.RunAsync("rgb", "off")));
        menu.Add(lighting);

        menu.Add(Check("Snap layouts", s.Settings.Snap.Enabled, () => _ = DeskActions.Snap(!s.Settings.Snap.Enabled)));
        menu.Add(new TrayMenuItem("Restore (night light off, no dimming)", () => _ = DeskActions.Restore()));
        menu.Add(TrayMenuItem.Separator());

        menu.Add(s.Engine
            ? new TrayMenuItem("Stop the engine", () => _ = DeskActions.StopEngine())
            : new TrayMenuItem("Start the engine", DeskActions.StartEngine));
        menu.Add(new TrayMenuItem("Hide this icon", () => _ = DeskActions.RunAsync("tray", "off")));
        menu.Add(new TrayMenuItem("Quit DispCtrl", _quit));
        return menu;
    }

    private static TrayMenuItem Check(string label, bool on, Action click, bool enabled = true) =>
        new(label, click) { Toggle = TrayToggle.Check, Checked = on, Enabled = enabled };

    private static TrayMenuItem Radio(string label, bool on, Action click) =>
        new(label, click) { Toggle = TrayToggle.Radio, Checked = on };

    public void Dispose()
    {
        DeskActions.Changed -= Rebuild;
        Desk.Current.Changed -= Rebuild;
        _timer.Stop();
        _watcher?.Dispose();
        _icon.Dispose();
    }
}
