using Avalonia;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Gui.Services;
using Avalonia.Platform.Storage;
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
        AddSupport();
    }

    /// <summary>Updates, the wallpaper and a problem report.</summary>
    private void AddSupport()
    {
        int at = Body.Children.IndexOf(Body.Children.OfType<TextBlock>().First(t => t.Text == "About"));
        TextBlock updateStatus = null!, reportStatus = null!, wallStatus = null!;
        var check = new Button { Content = "Check now" };
        var updates = Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Refresh, "Updates",
            Distro.InSnap ? "The Snap Store updates DispCtrl by itself." : "One request to this project's releases; nothing is downloaded.", check, out updateStatus);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            var reply = await CommandSender.RunAsync(["update"]);
            check.IsEnabled = true;
            updateStatus.Classes.Set("status", reply.ExitCode != 0);
            Views.CardBuilder.Show(updateStatus, reply.ExitCode == 0 ? reply.Stdout.Trim().Split('\n')[^1] : CommandSender.Reason(reply));
        };
        var wall = new Button { Content = "Choose a picture…" };
        wall.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Wallpaper",
                FileTypeFilter = [Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) Views.CardBuilder.Show(wallStatus, await DeskActions.RunAsync("wallpaper", path));
        };
        var wallpaper = Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Image, "Wallpaper", "The desktop's picture (GNOME shows one on every display).", wall, out wallStatus);
        var report = new Button { Content = "Report a problem…" };
        report.Click += async (_, _) => Views.CardBuilder.Show(reportStatus, await DeskActions.RunAsync("report", "--open"));
        var problem = Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Alert, "Report a problem",
            "Opens a GitHub issue with what DispCtrl can see here, serial numbers and your name taken out, for you to read before sending.", report, out reportStatus);
        Body.Children.InsertRange(at, [Views.CardBuilder.Section("Desktop and support"), wallpaper, updates, problem, Support()]);
    }

    private const string PayPal = "https://paypal.me/0x4kil", Upi = "akileswar2002@oksbi", Bitcoin = "13pu8fvcNt6Rx8tXJGfFDVWf59uhZuaFEp";

    /// <summary>Supporting DispCtrl for Linux: PayPal opens in the browser, the
    /// UPI ID and the Bitcoin address are copied, to paste into a payment app.</summary>
    private Border Support()
    {
        TextBlock status = null!;
        async void Copy(string text, string what)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                status.Classes.Set("status", false);
                Views.CardBuilder.Show(status, $"{what} copied: {text}");
            }
        }
        Button Make(string text, string name, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => click();
            return b;
        }
        var buttons = new WrapPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                Make("PayPal", "Support with PayPal", async () =>
                {
                    if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(PayPal));
                }),
                Make("Copy UPI ID", "Copy the UPI ID", () => Copy(Upi, "UPI ID")),
                Make("Copy Bitcoin address", "Copy the Bitcoin address", () => Copy(Bitcoin, "Bitcoin address")),
            },
        };
        return Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.StarFilled, "Support DispCtrl for Linux",
            "Free and open source, made in spare time. A donation keeps it going: PayPal, UPI or Bitcoin.", null, out status, buttons);
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
