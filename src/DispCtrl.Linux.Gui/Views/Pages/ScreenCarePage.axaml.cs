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
