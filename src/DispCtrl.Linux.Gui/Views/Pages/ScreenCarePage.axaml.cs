using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Keep awake, Stay active and turning the displays off: the
/// Windows app's Screen care page, less OLED care and Focus mode.</summary>
public partial class ScreenCarePage : UserControl
{
    private static readonly string[][] Modes =
    [
        ["awake", "off"], ["awake", "on"], ["awake", "for", "30m"], ["awake", "for", "1h"], ["awake", "for", "2h"], ["awake", "for", "4h"],
    ];

    private readonly Desk _desk = Desk.Current;
    private bool _ready;

    public ScreenCarePage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        AwakeMode.SelectionChanged += (_, _) =>
        {
            if (_ready && AwakeMode.SelectedIndex >= 0) Send("awake", Modes[AwakeMode.SelectedIndex]);
        };
        DisplaysOnSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_ready) Send("awake-displays", ["awake", "displays", DisplaysOnSwitch.IsChecked == true ? "on" : "off"]);
        };
        StayActiveSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_ready) Send("stayactive", ["stayactive", StayActiveSwitch.IsChecked == true ? "on" : "off"]);
        };
        AttachedToVisualTree += (_, _) => Load();
        AddCare();
    }

    private bool _careReady;
    private ToggleSwitch _oled = null!, _focus = null!, _sleep = null!;

    /// <summary>OLED care, Focus mode and monitor sleep: built here rather
    /// than in XAML, as the Automation and Lighting pages are.</summary>
    private void AddCare()
    {
        var s = SettingsStore.Load();
        void Send(string[] args, TextBlock status) { if (_careReady) _desk.Sender.Post(string.Join(' ', args.Take(2)), args, e => CardBuilder.Show(status, e)); }
        string Percent(double v) => $"{Math.Round(v)}%";
        string Minutes(double v) => $"{Math.Round(v)} min";

        Body.Children.Add(CardBuilder.Section("OLED care"));
        TextBlock oledStatus = null!;
        _oled = CardBuilder.Switch(s.OledCare.Enabled);
        _oled.IsCheckedChanged += (_, _) => Send(["oled", _oled.IsChecked == true ? "on" : "off"], oledStatus);
        Body.Children.Add(CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.WeatherSunnyLow, "OLED care",
            "When nothing is touched for a while, every display dims, then dims more, so a still picture does not stay lit at full strength. Any touch brings it back.",
            _oled, out oledStatus));
        TextBlock st = null!;
        Body.Children.Add(CardBuilder.Card(null, "First dimming after", null,
            CardBuilder.Slider(1, 60, s.OledCare.IdleMinutes, Minutes, v => Send(["oled", "--idle-minutes", ((int)v).ToString()], st), "First dimming after"), out st));
        Body.Children.Add(CardBuilder.Card(null, "Dims by", null,
            CardBuilder.Slider(10, 90, s.OledCare.DimPercent, Percent, v => Send(["oled", "--dim", ((int)v).ToString()], st), "OLED care dims by"), out _));
        var second = CardBuilder.Switch(s.OledCare.SecondStage);
        second.IsCheckedChanged += (_, _) => Send(["oled", "--second-stage", second.IsChecked == true ? "on" : "off"], st);
        Body.Children.Add(CardBuilder.Card(null, "Second stage", $"Dimmer still after {s.OledCare.SecondMinutes} min, by {s.OledCare.SecondDimPercent}%.", second, out _));
        var pauseFull = CardBuilder.Switch(s.OledCare.PauseFullscreen);
        pauseFull.IsCheckedChanged += (_, _) => Send(["oled", "--pause-fullscreen", pauseFull.IsChecked == true ? "on" : "off"], st);
        Body.Children.Add(CardBuilder.Card(null, "Not while a window is full screen", "A film or a game.", pauseFull, out _));
        var pauseMedia = CardBuilder.Switch(s.OledCare.PauseMedia);
        pauseMedia.IsCheckedChanged += (_, _) => Send(["oled", "--pause-media", pauseMedia.IsChecked == true ? "on" : "off"], st);
        Body.Children.Add(CardBuilder.Card(null, "Not while media plays", "Any player that says it is playing (MPRIS): browsers, VLC, Spotify.", pauseMedia, out _));
        TextBlock restStatus = null!;
        var rest = new Button { Content = "Rest now" };
        rest.Click += async (_, _) => CardBuilder.Show(restStatus, await DeskActions.RunAsync("oled", "rest"));
        Body.Children.Add(CardBuilder.Card(null, "Rest the displays", "Dims every display now, until you next touch the keyboard or mouse.", rest, out restStatus));

        Body.Children.Add(CardBuilder.Section("Focus mode"));
        TextBlock focusStatus = null!;
        _focus = CardBuilder.Switch(s.Focus.Enabled);
        _focus.IsCheckedChanged += (_, _) => Send(["focus", _focus.IsChecked == true ? "on" : "off"], focusStatus);
        Body.Children.Add(CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.FullScreenMaximize, "Focus mode",
            "Everything but the window in front is dimmed. Clicks still reach every window; a full-screen window lifts it.", _focus, out focusStatus));
        Body.Children.Add(CardBuilder.Card(null, "Everything else is darker by", null,
            CardBuilder.Slider(10, 90, s.Focus.DimPercent, Percent, v => Send(["focus", "--dim", ((int)v).ToString()], focusStatus), "Focus mode dims by"), out _));

        Body.Children.Add(CardBuilder.Section("Monitor sleep"));
        TextBlock sleepStatus = null!;
        _sleep = CardBuilder.Switch(s.MonitorSleep.Enabled);
        _sleep.IsCheckedChanged += (_, _) => Send(["monitor-sleep", _sleep.IsChecked == true ? "on" : "off"], sleepStatus);
        Body.Children.Add(CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.FullScreenMinimize, "Monitor sleep",
            "Every display switches off after this long with nothing touched, even where the desktop's own setting does not. Keep awake's \"displays on\" wins.",
            _sleep, out sleepStatus));
        Body.Children.Add(CardBuilder.Card(null, "After", null,
            CardBuilder.Slider(1, 120, s.MonitorSleep.Minutes, Minutes, v => Send(["monitor-sleep", "--minutes", ((int)v).ToString()], sleepStatus), "Monitor sleep after"), out _));
        _careReady = true;
    }

    /// <summary>Reads keep awake back from settings: the tray, the command line
    /// or the engine ending a timed one may have changed it.</summary>
    public void Load()
    {
        _ready = false;
        var a = SettingsStore.Load().Awake;
        bool on = a.ActiveAt(DateTimeOffset.Now);
        AwakeMode.SelectedIndex = !on ? 0 : a.Mode == Settings.AwakeMode.On ? 1 : -1;
        AwakeMode.PlaceholderText = on && a.Until is { } until ? $"Until {until:HH:mm}" : null;
        AwakeCaption.Text = on && a.Until is { } u
            ? $"Keeping the computer awake until {u:HH:mm}. The engine holds it."
            : "Stops the computer sleeping, for good or for a while. The engine holds it.";
        DisplaysOnSwitch.IsChecked = a.KeepDisplaysOn;
        StayActiveSwitch.IsChecked = a.StayActive;
        _ready = true;
    }

    private void Send(string key, string[] args)
    {
        SetStatus(AwakeStatus, null);
        _desk.Sender.Post(key, args, error =>
        {
            SetStatus(AwakeStatus, error);
            Load();
        });
    }

    private async void OnDisplaysOff(object? sender, RoutedEventArgs e)
    {
        OffButton.IsEnabled = false;
        var reply = await CommandSender.RunAsync(["displays-off"]);
        OffButton.IsEnabled = true;
        SetStatus(OffStatus, reply.ExitCode == 0 ? null : CommandSender.Reason(reply));
    }

    private static void SetStatus(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
    }
}
