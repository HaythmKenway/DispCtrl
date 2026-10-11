using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Snap layouts and Snap Assist: the Windows app's Windows page,
/// for what Windows 11 does itself and Linux does not.</summary>
public partial class WindowsPage : UserControl
{
    private readonly CommandSender _sender = Desk.Current.Sender;
    private bool _snapReady;
    private string _sentShortcut = "";

    public WindowsPage()
    {
        InitializeComponent();
        PageWidth.Track(this, Root);

        SnapSwitch.IsCheckedChanged += (_, _) => SendSnap(["snap", SnapSwitch.IsChecked == true ? "on" : "off"]);
        SnapDragSwitch.IsCheckedChanged += (_, _) => SendSnap(["snap", "drag", SnapDragSwitch.IsChecked == true ? "on" : "off"]);
        SnapAssistSwitch.IsCheckedChanged += (_, _) => SendSnap(["snap", "assist", SnapAssistSwitch.IsChecked == true ? "on" : "off"]);
        SnapGapSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            SnapGapText.Text = $"{(int)SnapGapSlider.Value} px";
            SendSnap(["snap", "gap", ((int)SnapGapSlider.Value).ToString(CultureInfo.InvariantCulture)]);
        };
        // A shortcut is sent when it is finished, not per keystroke: Super+ alone
        // is not a shortcut, and saying so while it is typed would be noise.
        SnapShortcutBox.LostFocus += (_, _) => SendShortcut();
        SnapShortcutBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) SendShortcut(); };

        LoadSnap();
    }

    private void LoadSnap()
    {
        _snapReady = false;
        var snap = SettingsStore.Load().Snap;
        SnapSwitch.IsChecked = snap.Enabled;
        SnapDragSwitch.IsChecked = snap.DragToTop;
        SnapAssistSwitch.IsChecked = snap.Assist;
        SnapShortcutBox.Text = snap.Shortcut;
        _sentShortcut = snap.Shortcut;
        SnapGapSlider.Value = snap.Gap;
        SnapGapText.Text = $"{snap.Gap} px";
        bool x = !GammaRamp.OnWayland;
        SetStatus(SnapStatusText, x ? null : "Snap layouts need an X11 session: on Wayland only the compositor may move windows.");
        _snapReady = true;
        _ = RefreshSnapProblemAsync();
    }

    /// <summary>The engine knows whether the shortcut is really held (another
    /// program may have it); shown under the shortcut.</summary>
    private async Task RefreshSnapProblemAsync()
    {
        var reply = await CommandSender.RunAsync(["snap", "status"]);
        var line = reply.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("engine", StringComparison.Ordinal)) ?? "";
        const string marker = "shortcut not held: ";
        int at = line.IndexOf(marker, StringComparison.Ordinal);
        SetStatus(SnapShortcutStatus, at >= 0 ? line[(at + marker.Length)..] : null);
    }

    private void SendSnap(string[] args)
    {
        if (!_snapReady) return;
        _sender.Post(string.Join(' ', args.Take(2)), args, error =>
        {
            SetStatus(SnapStatusText, error);
            if (error is not null) LoadSnap();
            else _ = RefreshSnapProblemAsync();
        });
    }

    private void SendShortcut()
    {
        if (!_snapReady) return;
        var text = (SnapShortcutBox.Text ?? "").Trim();
        if (text == _sentShortcut) return;
        if (text.Length > 0 && X11.Shortcut.Parse(text) is null)
        {
            SetStatus(SnapShortcutStatus, "Write it as a modifier and a key, such as Super+Z or Ctrl+Alt+S.");
            return;
        }
        _sentShortcut = text;
        _sender.Post("snap shortcut", ["snap", "shortcut", text.Length == 0 ? "none" : text], error =>
        {
            SetStatus(SnapShortcutStatus, error);
            // The engine takes a moment to try the new grab.
            if (error is null) DispatcherTimer.RunOnce(() => _ = RefreshSnapProblemAsync(), TimeSpan.FromSeconds(1.5));
        });
    }

    private static void SetStatus(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
    }
}
