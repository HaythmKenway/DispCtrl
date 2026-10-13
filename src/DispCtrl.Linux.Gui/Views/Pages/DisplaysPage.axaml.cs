using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.ViewModels;
using DispCtrl.Linux.Hardware;
using FluentAvalonia.UI.Controls;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>The arrangement, then each display: the Windows app's Displays
/// page, with what XRandR and DDC/CI can do.</summary>
public partial class DisplaysPage : UserControl
{
    /// <summary>How long a new mode lasts unless kept: Windows' own fifteen
    /// seconds, long enough to find the button on a picture that came back.</summary>
    private const int KeepSeconds = 15;

    private readonly Desk _desk = Desk.Current;
    private readonly HashSet<OutputViewModel> _watched = [];
    private bool _changing;

    public DisplaysPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);
        Outputs.ItemsSource = _desk.Outputs;

        bool wayland = GammaRamp.OnWayland;
        WaylandNotice.IsVisible = wayland;
        ArrangeSection.IsVisible = !wayland && XRandR.IsAvailable;

        ArrangeSurface.Applied += () => _ = _desk.RefreshOutputsAsync();
        // Only while shown: a page swapped out of the window stops listening.
        AttachedToVisualTree += (_, _) => { _desk.Changed += Refresh; Refresh(); };
        DetachedFromVisualTree += (_, _) => _desk.Changed -= Refresh;
    }

    private void Refresh()
    {
        RescanButton.IsEnabled = !_desk.Scanning;
        int count = _desk.Outputs.Count;
        var main = _desk.Outputs.FirstOrDefault(o => o.IsPrimary);
        Topology.Text = count switch
        {
            0 when _desk.Scanning => "Looking for displays…",
            0 => XRandR.IsAvailable ? "No display is lit on this X screen." : "No X display answered, so the layout cannot be read.",
            1 => "One display.",
            _ => $"{count} displays on one desktop.{(main is null ? "" : $" Main display: {main.Label}.")}",
        };
        ArrangeSurface.Load(_desk.Outputs);

        foreach (var output in _desk.Outputs.Where(o => _watched.Add(o))) output.ModeRequested += OnModeRequested;
        var connected = XRandR.IsAvailable && !GammaRamp.OnWayland ? XRandR.Query().Where(o => o.Connected).ToList() : [];
        ProjectSection.IsVisible = connected.Count >= 2;
        InternalButton.IsVisible = connected.Any(o => DisplayPlans.IsBuiltIn(o.Name));
        ExternalButton.IsVisible = connected.Any(o => DisplayPlans.IsBuiltIn(o.Name));
    }

    private void OnRescan(object? sender, RoutedEventArgs e) => _ = _desk.RescanAsync();

    private async void OnMakeMain(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string output } button) return;
        button.IsEnabled = false;
        var reply = await CommandSender.RunAsync(["display", "set", "--output", output, "--primary", "true"]);
        button.IsEnabled = true;
        MainStatus.Text = reply.ExitCode == 0 ? "" : CommandSender.Reason(reply);
        MainStatus.IsVisible = reply.ExitCode != 0;
        await _desk.RefreshOutputsAsync();
    }

    private void OnModeRequested(OutputViewModel output, int width, int height, double hz, int rotation)
    {
        string turn = rotation switch { 90 => "left", 180 => "inverted", 270 => "right", _ => "normal" };
        var args = new List<string> { "display", "set", "--output", output.Output.Name, "--mode", $"{width}x{height}", "--rotate", turn };
        if (hz > 0) { args.Add("--rate"); args.Add(hz.ToString("0.##", CultureInfo.InvariantCulture)); }
        _ = ChangeAsync([.. args], MainStatus);
    }

    private void OnProject(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string how }) _ = ChangeAsync(["display", "project", how], ProjectStatus);
    }

    /// <summary>Applies a change of mode, turn or display mode, then asks
    /// whether to keep it and puts the desk back if nobody answers: a mode the
    /// monitor cannot show leaves no picture to find the button on.</summary>
    private async Task ChangeAsync(string[] args, TextBlock status)
    {
        if (_changing) return;
        _changing = true;
        try
        {
            var before = await Task.Run(XRandR.Query);
            var reply = await CommandSender.RunAsync(args);
            status.Text = reply.ExitCode == 0 ? "" : CommandSender.Reason(reply);
            status.IsVisible = reply.ExitCode != 0;
            if (reply.ExitCode != 0) { await _desk.RefreshOutputsAsync(); return; }

            if (!await KeepAsync())
            {
                var undo = await Task.Run(() =>
                {
                    var plan = DisplayPlans.Changes(DisplayPlans.Restore(before), XRandR.Query());
                    string? primary = before.FirstOrDefault(o => o.Primary)?.Name;
                    return plan.Count == 0 || XRandR.Configure(plan, primary, out var error) ? null : error;
                });
                status.Text = undo is null ? "Put back as it was." : $"Could not put it back: {undo}";
                status.IsVisible = true;
            }
            await _desk.RefreshOutputsAsync();
        }
        finally { _changing = false; }
    }

    private static async Task<bool> KeepAsync()
    {
        int left = KeepSeconds;
        var dialog = new FAContentDialog
        {
            Title = "Keep these display settings?",
            Content = $"Going back in {left} seconds.",
            PrimaryButtonText = "Keep changes",
            CloseButtonText = "Go back",
            DefaultButton = FAContentDialogButton.Close,
        };
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            left--;
            dialog.Content = $"Going back in {left} second{(left == 1 ? "" : "s")}.";
            if (left <= 0) dialog.Hide(FAContentDialogResult.None);
        });
        timer.Start();
        try { return await dialog.ShowAsync() == FAContentDialogResult.Primary; }
        finally { timer.Stop(); }
    }
}
