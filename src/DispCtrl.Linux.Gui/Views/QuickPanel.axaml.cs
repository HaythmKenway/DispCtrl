using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Gui.Views;

/// <summary>The quick panel: the Windows quick panel's tiles, each display's
/// brightness, night light's strength, display mode and presets, opened from
/// the tray icon beside the pointer and closed by clicking elsewhere.</summary>
public partial class QuickPanel : Window
{
    private readonly Action _openWindow;
    // Set once the toggles hold the state read back; until then their events
    // are the panel filling itself in, not a person.
    private bool _ready;

    public QuickPanel() : this(() => { }) { }

    public QuickPanel(Action openWindow)
    {
        _openWindow = openWindow;
        InitializeComponent();
        BrightnessRows.ItemsSource = Desk.Current.Brightness;

        NightLightTile.IsCheckedChanged += (_, _) => Toggle(NightLightTile, on => DeskActions.NightLight(on));
        DarkTile.IsCheckedChanged += (_, _) => Toggle(DarkTile, on => DeskActions.DarkMode(on));
        AwakeTile.IsCheckedChanged += (_, _) => Toggle(AwakeTile, on => DeskActions.Awake(on ? "on" : "off"));
        StayActiveTile.IsCheckedChanged += (_, _) => Toggle(StayActiveTile, on => DeskActions.StayActive(on));
        SnapTile.IsCheckedChanged += (_, _) => Toggle(SnapTile, on => DeskActions.Snap(on));
        FocusTile.IsCheckedChanged += (_, _) => Toggle(FocusTile, on => DeskActions.RunAsync("focus", on ? "on" : "off"));
        OledTile.IsCheckedChanged += (_, _) => Toggle(OledTile, on => DeskActions.RunAsync("oled", on ? "on" : "off"));
        StrengthSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            int strength = (int)Math.Round(StrengthSlider.Value);
            StrengthText.Text = string.Create(CultureInfo.InvariantCulture, $"{Warmth.KelvinFor(strength):0} K");
            if (_ready) Desk.Current.Sender.Post("nightlight", ["nightlight", strength.ToString(CultureInfo.InvariantCulture)], Show);
        };

        // Clicking anywhere else hides it, as Windows' own panel does. Hidden,
        // not closed: building it again took most of a second on each click.
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Hide(); };
    }

    /// <summary>Opens beside the pointer, which is on the tray icon: below it
    /// for a panel at the top of the screen (GNOME, most Linux desktops),
    /// above it for one at the bottom.</summary>
    public void ShowNearPointer()
    {
        var (px, py) = Pointer();
        var screen = Screens.ScreenFromPoint(new PixelPoint(px, py)) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            int w = (int)(Width * scale), h = (int)(Math.Min(MaxHeight, 640) * scale), gap = (int)(8 * scale);
            int x = Math.Clamp(px - w / 2, area.X + gap, area.Right - w - gap);
            int y = py < area.Y + area.Height / 2 ? area.Y + gap : area.Bottom - h - gap;
            Position = new PixelPoint(x, y);
        }
        Show();
        Activate();
        _ = LoadAsync();
    }

    private static (int X, int Y) Pointer()
    {
        using var x = XConnection.Open();
        if (x is null) return (0, 0);
        return Desktop.Pointer(x);
    }

    private async Task LoadAsync()
    {
        _ready = false;
        var s = await DeskActions.ReadAsync();
        NightLightTile.IsChecked = s.Settings.NightLight.Enabled;
        NightLightTile.IsEnabled = s.NightLightAvailable || s.Settings.NightLight.Enabled;
        DarkTile.IsChecked = s.Dark == true;
        DarkTile.IsEnabled = s.Dark is not null;
        bool awake = s.Settings.Awake.ActiveAt(DateTimeOffset.Now);
        AwakeTile.IsChecked = awake;
        AwakeLabel.Text = awake && s.Settings.Awake.Until is { } until ? $"Awake to {until:HH:mm}" : "Keep awake";
        StayActiveTile.IsChecked = s.Settings.Awake.StayActive;
        SnapTile.IsChecked = s.Settings.Snap.Enabled;
        FocusTile.IsChecked = s.Settings.Focus.Enabled;
        OledTile.IsChecked = s.Settings.OledCare.Enabled;
        StrengthSlider.Value = s.Settings.NightLight.Strength;
        StrengthSlider.IsEnabled = s.NightLightAvailable;

        BrightnessEmpty.Text = Desk.Current.Scanning ? "Looking for displays…"
            : Desk.Current.Brightness.Count == 0 ? "No display's own brightness can be set here." : "";
        BrightnessEmpty.IsVisible = BrightnessEmpty.Text.Length > 0;

        var connected = s.Connected;
        ProjectSection.IsVisible = connected.Count >= 2;
        InternalButton.IsVisible = ExternalButton.IsVisible = connected.Any(o => DisplayPlans.IsBuiltIn(o.Name));

        PresetSection.IsVisible = s.Settings.Presets.Count > 0;
        PresetRows.ItemsSource = s.Settings.Presets.Select(p =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
            var apply = new Button { Content = "Apply", Tag = p.Name };
            Grid.SetColumn(apply, 1);
            Avalonia.Automation.AutomationProperties.SetName(apply, $"Apply {p.Name}");
            apply.Click += async (_, _) => Show(await DeskActions.ApplyPreset(p.Name));
            row.Children.Add(apply);
            return row;
        }).ToList();
        _ready = true;
    }

    private void Toggle(ToggleButton tile, Func<bool, Task<string?>> action)
    {
        if (!_ready) return;
        bool on = tile.IsChecked == true;
        _ = Run();
        async Task Run()
        {
            Show(await action(on));
            await LoadAsync();
        }
    }

    private void Show(string? error)
    {
        StatusText.Text = error ?? "";
        StatusText.IsVisible = error is not null;
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        Hide();
        _openWindow();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Hide();

    private async void OnDisplaysOff(object? sender, RoutedEventArgs e)
    {
        Hide();
        await DeskActions.DisplaysOff();
    }

    private async void OnRest(object? sender, RoutedEventArgs e)
    {
        Hide();
        Show(await DeskActions.RunAsync("oled", "rest"));
    }

    private void OnIdentify(object? sender, RoutedEventArgs e) => DeskActions.Identify();

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        Show(await DeskActions.Restore());
        await LoadAsync();
    }

    private async void OnProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string how }) Show(await DeskActions.Project(how));
    }
}
