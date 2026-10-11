using Avalonia.Controls;
using Avalonia.Interactivity;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>The arrangement, then each display: the Windows app's Displays
/// page, with what XRandR can do.</summary>
public partial class DisplaysPage : UserControl
{
    private readonly Desk _desk = Desk.Current;

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
}
