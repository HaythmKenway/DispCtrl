using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>The engine and where settings live: the Windows app's Settings
/// and About pages, which on Linux have little enough to share one.</summary>
public partial class SettingsPage : UserControl
{
    private const string GuideUrl = "https://github.com/HaythmKenway/DispCtrl/blob/master/docs/LINUX.md";

    public SettingsPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        VersionText.Text = $"DispCtrl for Linux {CommandRunner.Version}";
        SettingsPath.Text = SettingsStore.FilePath;
        ShowEngine(null);
    }

    /// <summary>Shows whether the engine runs; null while not yet known.</summary>
    public void ShowEngine(bool? running)
    {
        EngineState.Text = running switch { true => "Running", false => "Not running", null => "Checking…" };
        EngineDot.Classes.Set("running", running == true);
        EngineDot.Classes.Set("stopped", running == false);
        EngineCaption.Text = running == false
            ? "Changes still apply now, but nothing follows the night light schedule, snaps windows or repairs a display after it is plugged in. Start it, and at every login after, with:"
            : "Follows the night light schedule, snaps windows, and puts each display's ramp back after it is plugged in or changes mode. It runs with your session.";
        EngineCommand.IsVisible = running == false;
    }

    private async void OnGuide(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(GuideUrl));
    }
}
