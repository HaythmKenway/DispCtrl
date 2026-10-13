using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>DispCtrl's icon in the panel's tray (StatusNotifierItem), with a
/// menu of everything worth one click: the Windows tray menu and most of the
/// quick panel's toggles.</summary>
/// <remarks>
/// KDE, XFCE, Cinnamon, MATE, Budgie and LXQt show it as they are; GNOME needs
/// the AppIndicator extension, which Ubuntu ships switched on. The menu is
/// rebuilt from settings whenever they change, so a switch flipped on the
/// command line or in the window is ticked here too.
/// </remarks>
public sealed class TrayHost : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly Action _openWindow;
    private readonly Action<string> _openPage;
    private readonly Action _openPanel;
    private readonly Action _quit;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _timer;
    private int _rebuilding;

    public TrayHost(Action openWindow, Action<string> openPage, Action openPanel, Action quit)
    {
        _openWindow = openWindow;
        _openPage = openPage;
        _openPanel = openPanel;
        _quit = quit;
        using var png = AssetLoader.Open(new Uri("avares://dispctrl-linux-gui/Assets/dispctrl.png"));
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(png),
            ToolTipText = "DispCtrl",
            Menu = [new NativeMenuItem("DispCtrl") { IsEnabled = false }],
            IsVisible = true,
        };
        _icon.Clicked += (_, _) =>
        {
            if (SettingsStore.Load().Tray.QuickPanel) _openPanel();
            else _openWindow();
        };
        TrayIcon.SetIcons(Application.Current!, [_icon]);

        DeskActions.Changed += Rebuild;
        Desk.Current.Changed += Rebuild;
        _watcher = Watch();
        // The engine starting or stopping, and a timed keep awake running out,
        // change nothing this process watches.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Rebuild());
        _timer.Start();
        Rebuild();
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
            _icon.Menu = Build(state);
            _icon.ToolTipText = Tooltip(state);
        }
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

    private NativeMenu Build(DeskActions.State s)
    {
        var menu = new NativeMenu();
        menu.Add(Item("Open DispCtrl", _openWindow));
        menu.Add(Item("Quick panel", _openPanel));
        menu.Add(new NativeMenuItemSeparator());

        // Brightness: each display's own, in steps.
        var brightness = new NativeMenu();
        foreach (var row in Desk.Current.Brightness.Where(r => r.IsInteractive))
        {
            var steps = new NativeMenu();
            foreach (int pct in new[] { 100, 75, 50, 25, 10, 0 })
            {
                double value = row.Minimum + (row.Maximum - row.Minimum) * pct / 100.0;
                steps.Add(Radio($"{pct}%", Math.Abs(row.Value - value) < (row.Maximum - row.Minimum) / 16.0, () => row.Value = value));
            }
            brightness.Add(new NativeMenuItem(row.Title) { Menu = steps });
        }
        if (brightness.Items.Count == 0)
            brightness.Add(new NativeMenuItem(Desk.Current.Scanning ? "Looking for displays…" : "No display's brightness can be set") { IsEnabled = false });
        menu.Add(new NativeMenuItem("Brightness") { Menu = brightness });

        var n = s.Settings.NightLight;
        menu.Add(Check("Night light", n.Enabled, () => _ = DeskActions.NightLight(!n.Enabled), s.NightLightAvailable || n.Enabled));
        var strength = new NativeMenu();
        foreach (var (label, value) in new[] { ("Light", 25), ("Medium", 50), ("Strong", 75), ("Strongest", 100) })
            strength.Add(Radio($"{label} ({Ramps.Warmth.KelvinFor(value):0} K)", n.Strength == value, () => _ = DeskActions.NightLightStrength(value)));
        menu.Add(new NativeMenuItem("Night light strength") { Menu = strength, IsEnabled = s.NightLightAvailable });
        if (s.Dark is bool dark) menu.Add(Check("Dark mode", dark, () => _ = DeskActions.DarkMode(!dark)));
        menu.Add(new NativeMenuItemSeparator());

        var a = s.Settings.Awake;
        bool awake = a.ActiveAt(DateTimeOffset.Now);
        var awakeMenu = new NativeMenu();
        awakeMenu.Add(Radio("Off", !awake, () => _ = DeskActions.Awake("off")));
        awakeMenu.Add(Radio("Until I switch it off", awake && a.Mode == AwakeMode.On, () => _ = DeskActions.Awake("on")));
        foreach (var (label, span) in new[] { ("For 30 minutes", "30m"), ("For 1 hour", "1h"), ("For 2 hours", "2h"), ("For 4 hours", "4h") })
            awakeMenu.Add(Radio(label, false, () => _ = DeskActions.Awake(span)));
        awakeMenu.Add(new NativeMenuItemSeparator());
        awakeMenu.Add(Check("Keep the displays on", a.KeepDisplaysOn, () => _ = DeskActions.RunAsync("awake", "displays", a.KeepDisplaysOn ? "off" : "on")));
        string awakeLabel = !awake ? "Keep awake" : a.Mode == AwakeMode.Until && a.Until is { } until
            ? $"Keep awake (until {until:HH:mm})" : "Keep awake (on)";
        menu.Add(new NativeMenuItem(awakeLabel) { Menu = awakeMenu });
        menu.Add(Check("Stay active", a.StayActive, () => _ = DeskActions.StayActive(!a.StayActive)));
        menu.Add(Item("Turn off displays", () => _ = DeskActions.DisplaysOff()));
        menu.Add(new NativeMenuItemSeparator());

        // Display mode, only where there is a choice to make.
        var connected = Hardware.XRandR.IsAvailable ? Hardware.XRandR.Query().Where(o => o.Connected).ToList() : [];
        if (connected.Count >= 2)
        {
            var project = new NativeMenu();
            project.Add(Item("Extend", () => _ = DeskActions.Project("extend")));
            project.Add(Item("Duplicate", () => _ = DeskActions.Project("duplicate")));
            if (connected.Any(o => DisplayPlans.IsBuiltIn(o.Name)))
            {
                project.Add(Item("Built-in display only", () => _ = DeskActions.Project("internal")));
                project.Add(Item("Other displays only", () => _ = DeskActions.Project("external")));
            }
            menu.Add(new NativeMenuItem("Display mode") { Menu = project });
        }
        menu.Add(Item("Identify displays", DeskActions.Identify));

        var presets = new NativeMenu();
        foreach (var p in s.Settings.Presets)
        {
            string name = p.Name;
            presets.Add(Item(name, () => _ = DeskActions.ApplyPreset(name)));
        }
        if (s.Settings.Presets.Count > 0) presets.Add(new NativeMenuItemSeparator());
        presets.Add(Item("Save the desk as a preset…", () => _openPage("presets")));
        menu.Add(new NativeMenuItem("Presets") { Menu = presets });

        menu.Add(Check("Snap layouts", s.Settings.Snap.Enabled, () => _ = DeskActions.Snap(!s.Settings.Snap.Enabled)));
        menu.Add(Item("Restore (night light off, no dimming)", () => _ = DeskActions.Restore()));
        menu.Add(new NativeMenuItemSeparator());

        menu.Add(s.Engine
            ? Item("Stop the engine", () => _ = DeskActions.StopEngine())
            : Item("Start the engine", DeskActions.StartEngine));
        menu.Add(Item("Hide this icon", () => _ = DeskActions.RunAsync("tray", "off")));
        menu.Add(Item("Quit DispCtrl", _quit));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action click)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => click();
        return item;
    }

    private static NativeMenuItem Check(string header, bool on, Action click, bool enabled = true)
    {
        var item = Item(header, click);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        item.IsEnabled = enabled;
        return item;
    }

    private static NativeMenuItem Radio(string header, bool on, Action click)
    {
        var item = Item(header, click);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = on;
        return item;
    }

    public void Dispose()
    {
        DeskActions.Changed -= Rebuild;
        Desk.Current.Changed -= Rebuild;
        _timer.Stop();
        _watcher?.Dispose();
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
