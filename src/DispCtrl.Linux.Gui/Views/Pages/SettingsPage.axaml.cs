using Avalonia;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Diagnostics;
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
        var wallPath = new TextBox { PlaceholderText = "Or type the picture's path", MinWidth = 260 };
        wallPath.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || string.IsNullOrWhiteSpace(wallPath.Text)) return;
            await SetWallpaper(wallPath.Text.Trim().Trim('"').Replace("~/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/"));
        };
        async Task SetWallpaper(string path)
        {
            var reply = await CommandSender.RunHereAsync(["wallpaper", path]);
            wallStatus.Classes.Set("status", reply.ExitCode != 0);
            Views.CardBuilder.Show(wallStatus, reply.ExitCode == 0 ? reply.Stdout.Trim() : CommandSender.Reason(reply));
        }
        wall.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                wallStatus.Classes.Set("status", true);
                Views.CardBuilder.Show(wallStatus, "No file chooser is available here. Type the picture's path in the box and press Enter.");
                return;
            }
            IReadOnlyList<IStorageFile> files;
            try
            {
                files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Wallpaper",
                    FileTypeFilter = [Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll],
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The desktop's file chooser is a separate service (a portal);
                // when it crashed mid-pick, the exception closed the whole window.
                wallStatus.Classes.Set("status", true);
                Views.CardBuilder.Show(wallStatus, $"The desktop's file chooser stopped answering ({ex.GetType().Name}). Type the picture's path in the box and press Enter.");
                return;
            }
            if (files.FirstOrDefault() is not { } file) return;
            // A real path goes straight to the command, which keeps a copy where
            // GNOME can read it; the portal's grant has no path to hand over, so
            // that one is copied through the picker's own stream first.
            try
            {
                var local = file.TryGetLocalPath();
                await SetWallpaper(local is { Length: > 0 } && File.Exists(local) ? local : await KeepWallpaper(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                wallStatus.Classes.Set("status", true);
                Views.CardBuilder.Show(wallStatus, $"{ex.Message} Type the picture's path in the box and press Enter.");
            }
        };
        var wallControls = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { wallPath, wall } };
        var wallpaper = Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Image, "Wallpaper", "The desktop's picture (GNOME shows one on every display).", wallControls, out wallStatus);
        void ReportStatus(string text, bool problem)
        {
            reportStatus.Classes.Set("status", problem);
            Views.CardBuilder.Show(reportStatus, text);
        }
        Button Make(string text, string name)
        {
            var b = new Button { Content = text };
            AutomationProperties.SetName(b, name);
            return b;
        }
        // Each button shows its result under the card and sends nothing by itself:
        // a browser or mail program opens for the person to read and send.
        async Task Send(Button button, string[] command, string opened, string failed)
        {
            button.IsEnabled = false;
            var reply = await CommandSender.RunHereAsync(command);
            button.IsEnabled = true;
            var url = reply.Stdout.Trim().Split('\n')[^1];
            if (reply.ExitCode != 0 || !(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal)))
            {
                ReportStatus(CommandSender.Reason(reply), true);
                return;
            }
            // Through the desktop's launcher (a portal in the snap), not the engine,
            // whose confinement may not open a browser.
            bool launched = TopLevel.GetTopLevel(this)?.Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(url));
            var note = reply.Stderr.Trim().Replace("dispctrl-linux: ", "", StringComparison.Ordinal);
            ReportStatus(launched ? $"{opened}{(note.Length > 0 ? " " + note : "")}" : failed, !launched);
        }
        var report = Make("Open GitHub issue", "Open a GitHub issue");
        report.Click += (_, _) => _ = Send(report, ["report", "--url"],
            "Opened in your browser: read it, add what happened, then submit.",
            "No browser could be opened. Run dispctrl-linux report in a terminal to see the report.");
        var mail = Make("Email the log", "Email the log");
        mail.Click += (_, _) => _ = Send(mail, ["report", "--mail-url"],
            $"Opened a message to {ProblemReport.Email}: read it, then send it yourself.",
            $"No mail program could be opened. Write to {ProblemReport.Email} and attach the saved report (Save report…).");
        var saveReport = Make("Save report…", "Save the report");
        saveReport.Click += async (_, _) =>
        {
            saveReport.IsEnabled = false;
            var reply = await CommandSender.RunHereAsync(["report", "--save"]);
            saveReport.IsEnabled = true;
            ReportStatus(reply.ExitCode == 0 ? reply.Stdout.Trim() : CommandSender.Reason(reply), reply.ExitCode != 0);
        };
        var openLogs = Make("Open log folder", "Open the log folder");
        openLogs.Click += async (_, _) =>
        {
            var folder = AppLog.Directory;
            try { Directory.CreateDirectory(folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            bool launched = TopLevel.GetTopLevel(this)?.Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(folder.TrimEnd('/') + "/"));
            ReportStatus(launched ? $"Log folder: {Redact.Scrub(folder)}" : $"No file manager could be opened. The log is in {Redact.Scrub(folder)}.", !launched);
        };
        var reportButtons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { report, mail, saveReport, openLogs } };
        foreach (var b in reportButtons.Children) b.Margin = new Thickness(0, 0, 8, 8);
        var problem = Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Alert, "Report a problem",
            "Opens a GitHub issue or an email with what DispCtrl can see here and the end of its log, serial numbers and your name taken out, for you to read before sending. Save report… writes the whole log to a file to attach.", reportButtons, out reportStatus);
        Body.Children.InsertRange(at, [Views.CardBuilder.Section("Desktop and support"), wallpaper, updates, problem, Support()]);
    }

    /// <summary>The picture, copied where it stays and the desktop can read it:
    /// the snap's own folder in the home directory, or DispCtrl's data folder.</summary>
    private static async Task<string> KeepWallpaper(IStorageFile file)
    {
        var folder = Commands.CommandRunner.WallpaperFolder();
        Directory.CreateDirectory(folder);
        // One kept at a time: the desktop holds a copy's path, not the copy.
        foreach (var old in Directory.GetFiles(folder)) File.Delete(old);
        var target = Path.Combine(folder, Path.GetFileName(file.Name));
        await using var source = await file.OpenReadAsync();
        await using var destination = File.Create(target);
        await source.CopyToAsync(destination);
        return target;
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
