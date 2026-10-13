using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Brightness in the hardware, dimming in software and night light:
/// the Windows app's Brightness page.</summary>
public partial class BrightnessPage : UserControl
{
    private readonly Desk _desk = Desk.Current;

    // Set once the night light controls hold what settings say. Before that,
    // every assignment raises the same events a person does, and was written
    // back - the gate the Windows app needs on every two-way control.
    private bool _nightLightReady;

    public BrightnessPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        SizeChanged += (_, e) => RestoreLabel.IsVisible = e.NewSize.Width >= 440;
        BrightnessRows.ItemsSource = _desk.Brightness;
        DimRows.ItemsSource = _desk.Dimming;

        NightLightSwitch.IsCheckedChanged += (_, _) => OnNightLightSwitched();
        StrengthSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty) OnStrengthChanged();
        };
        ScheduleSwitch.IsCheckedChanged += (_, _) => OnScheduleChanged();
        FromPicker.SelectedTimeChanged += (_, _) => OnScheduleChanged();
        ToPicker.SelectedTimeChanged += (_, _) => OnScheduleChanged();

        DarkSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_themeReady) SendTheme(["darkmode", DarkSwitch.IsChecked == true ? "on" : "off"]);
        };
        DarkScheduleSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_themeReady) SendTheme(["darkmode", "schedule", DarkScheduleSwitch.IsChecked == true ? "on" : "off"]);
        };

        LoadNightLight();
        _ = LoadThemeAsync();
        AttachedToVisualTree += (_, _) => { _desk.Changed += Refresh; Refresh(); _ = LoadThemeAsync(); };
        DetachedFromVisualTree += (_, _) => _desk.Changed -= Refresh;
    }

    private bool _themeReady;

    /// <summary>Reads the theme from the desktop each time: it is the
    /// desktop's setting, and GNOME's own Settings may have changed it.</summary>
    public async Task LoadThemeAsync()
    {
        _themeReady = false;
        var dark = await Task.Run(Session.DarkMode.Read);
        DarkSwitch.IsChecked = dark == true;
        DarkSwitch.IsEnabled = dark is not null;
        DarkScheduleSwitch.IsChecked = SettingsStore.Load().DarkMode.FollowSchedule;
        DarkStatus.Text = dark is null ? "This desktop keeps its theme where DispCtrl cannot reach it (GNOME, KDE and XFCE can be)." : "";
        DarkStatus.IsVisible = dark is null;
        _themeReady = true;
    }

    private void SendTheme(string[] args) =>
        _desk.Sender.Post("darkmode", args, error =>
        {
            DarkStatus.Text = error ?? "";
            DarkStatus.IsVisible = error is not null;
            if (error is not null) _ = LoadThemeAsync();
        });

    private void Refresh()
    {
        RescanButton.IsEnabled = !_desk.Scanning;
        ScanStatus.Text = _desk.ScanStatus ?? "";
        ScanStatus.IsVisible = !string.IsNullOrEmpty(_desk.ScanStatus);
        bool ramps = GammaRamp.IsAvailable(out var reason);
        DimStatus.Text = ramps ? "" : $"Unavailable: {reason}.";
        DimStatus.IsVisible = !ramps;
    }

    private void OnRescan(object? sender, RoutedEventArgs e) => _ = _desk.RescanAsync();

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        RestoreButton.IsEnabled = false;
        var reply = await CommandSender.RunAsync(["restore"]);
        RestoreButton.IsEnabled = true;
        LoadNightLight();
        SetNightLightStatus(reply.ExitCode == 0 ? null : reply.Stderr.Trim());
        _ = _desk.RescanAsync();
    }

    /// <summary>Reads night light back from the settings, as after Restore
    /// or a refused change.</summary>
    public void LoadNightLight()
    {
        _nightLightReady = false;
        var settings = SettingsStore.Load().NightLight;
        NightLightSwitch.IsChecked = settings.Enabled;
        StrengthSlider.Value = settings.Strength;
        ScheduleSwitch.IsChecked = settings.Scheduled;
        Schedule.TryParse(settings.From, out var from);
        Schedule.TryParse(settings.To, out var to);
        FromPicker.SelectedTime = from.ToTimeSpan();
        ToPicker.SelectedTime = to.ToTimeSpan();
        UpdateStrengthText();
        ScheduleTimes.IsEnabled = settings.Scheduled;

        bool x = GammaRamp.IsAvailable(out var reason);
        NightLightSwitch.IsEnabled = x || settings.Enabled;
        SetNightLightStatus(x ? null : $"Unavailable: {reason}.");
        _nightLightReady = true;
    }

    private void OnNightLightSwitched()
    {
        if (!_nightLightReady) return;
        Send(NightLightSwitch.IsChecked == true ? ["nightlight", "on"] : ["nightlight", "off"]);
    }

    private void OnStrengthChanged()
    {
        UpdateStrengthText();
        if (!_nightLightReady) return;
        int strength = (int)Math.Round(StrengthSlider.Value);
        // A strength is also "on", as on the command line; the switch follows.
        if (NightLightSwitch.IsChecked != true)
        {
            _nightLightReady = false;
            NightLightSwitch.IsChecked = true;
            _nightLightReady = true;
        }
        Send(["nightlight", strength.ToString(CultureInfo.InvariantCulture)]);
    }

    private void OnScheduleChanged()
    {
        ScheduleTimes.IsEnabled = ScheduleSwitch.IsChecked == true;
        if (!_nightLightReady) return;
        if (FromPicker.SelectedTime is not { } from || ToPicker.SelectedTime is not { } to) return;
        // Compared at minute resolution: a picker holds whole minutes, and
        // comparing seconds made the Windows app's TimePicker write back forever.
        var f = TimeOnly.FromTimeSpan(from);
        var t = TimeOnly.FromTimeSpan(to);
        if (f.Hour == t.Hour && f.Minute == t.Minute)
        {
            SetNightLightStatus("The schedule cannot start and end at the same time.");
            return;
        }
        Send(["nightlight", "--from", Schedule.Format(f), "--to", Schedule.Format(t),
              "--schedule", ScheduleSwitch.IsChecked == true ? "on" : "off"]);
    }

    private void Send(string[] args)
    {
        SetNightLightStatus(null);
        _desk.Sender.Post("nightlight", args, error =>
        {
            if (error is not null) LoadNightLight();
            SetNightLightStatus(error);
        });
    }

    private void SetNightLightStatus(string? text)
    {
        NightLightStatus.Text = text ?? "";
        NightLightStatus.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void UpdateStrengthText() =>
        StrengthText.Text = string.Create(CultureInfo.InvariantCulture, $"{Warmth.KelvinFor((int)Math.Round(StrengthSlider.Value)):0} K");
}
