using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>The engine and where settings live: the Windows app's Settings
/// and About pages, which on Linux have little enough to share one.</summary>
public partial class SettingsPage : UserControl
{
    private const string GuideUrl = "https://github.com/HaythmKenway/DispCtrl-linux/blob/master/docs/LINUX.md";

    public SettingsPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        VersionText.Text = $"DispCtrl for Linux {CommandRunner.Version}";
        SettingsPath.Text = SettingsStore.FilePath;
        EngineCommand.Text = Distro.EngineStartCommand;
        ShowEngine(null);

        var tray = SettingsStore.Load().Tray;
        TraySwitch.IsChecked = tray.Enabled;
        TrayClick.SelectedIndex = tray.QuickPanel ? 0 : 1;
        TraySwitch.IsCheckedChanged += async (_, _) =>
        {
            bool on = TraySwitch.IsChecked == true;
            await DeskActions.RunAsync("tray", on ? "on" : "off");
            // This process keeps the icon: start it here rather than at the next sign-in.
            if (on && Application.Current is App app) app.EnsureTray();
        };
        TrayClick.SelectionChanged += (_, _) => _ = DeskActions.RunAsync("tray", "click", TrayClick.SelectedIndex == 1 ? "window" : "panel");
    }

    private bool? _running;

    private async void OnEngineButton(object? sender, RoutedEventArgs e)
    {
        EngineButton.IsEnabled = false;
        if (_running == true) await DeskActions.StopEngine();
        else DeskActions.StartEngine();
        await Task.Delay(1500);
        EngineButton.IsEnabled = true;
        ShowEngine(await Task.Run(() => Engine.EngineClient.IsRunning()));
    }

    /// <summary>Shows whether the engine runs; null while not yet known.</summary>
    public void ShowEngine(bool? running)
    {
        _running = running;
        EngineButton.Content = running == true ? "Stop" : "Start";
        EngineButton.IsVisible = running is not null;
        EngineState.Text = running switch { true => "Running", false => "Not running", null => "Checking…" };
        EngineDot.Classes.Set("running", running == true);
        EngineDot.Classes.Set("stopped", running == false);
        EngineCaption.Text = running == false
            ? "Changes still apply now, but nothing follows the night light schedule, snaps windows or repairs a display after it is plugged in. "
              + (Distro.EngineStartsAtSignIn ? "It starts by itself when you sign in; to start it now, run:" : "Start it, and at every sign-in after, with:")
            : "Follows the night light schedule, snaps windows, and puts each display's ramp back after it is plugged in or changes mode. It runs with your session.";
        EngineCommand.IsVisible = running == false;
    }

    private async void OnGuide(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(GuideUrl));
    }
}
