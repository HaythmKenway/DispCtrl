using Avalonia.Controls.Primitives;
using DispCtrl.Linux.Gui.Views;
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
        AddWindowTools();
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

    private readonly StackPanel _windows = Views.CardBuilder.List();

    /// <summary>The open windows, each with pin and move, and gather: the
    /// Windows app's window tools.</summary>
    private void AddWindowTools()
    {
        Body.Children.Add(Views.CardBuilder.Section("Window tools"));
        TextBlock status = null!;
        var gather = new Button { Content = "Gather here" };
        gather.Click += async (_, _) => { CardBuilder.Show(status, await Services.DeskActions.RunAsync("placement", "gather", "--to", "pointer")); await LoadWindowsAsync(); };
        Body.Children.Add(Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.ViewAll, "Gather every window",
            "Onto the display the pointer is on. Ctrl+Alt+G does it from anywhere.", gather, out status));
        TextBlock unpinStatus = null!;
        var unpin = new Button { Content = "Unpin all" };
        unpin.Click += async (_, _) => { CardBuilder.Show(unpinStatus, await Services.DeskActions.RunAsync("pin", "off", "--all")); await LoadWindowsAsync(); };
        Body.Children.Add(Views.CardBuilder.Card(FluentAvalonia.UI.Controls.FASymbol.Pin, "Windows on top",
            "A pinned window stays above the others. Ctrl+Alt+P pins or unpins the one in front.", unpin, out unpinStatus));
        Body.Children.Add(Views.CardBuilder.Section("Open windows"));
        Body.Children.Add(_windows);
        AttachedToVisualTree += (_, _) => _ = LoadWindowsAsync();
    }

    private async Task LoadWindowsAsync()
    {
        var list = await Task.Run(() =>
        {
            using var x = X11.XConnection.Open();
            return x is null ? null : X11.Desktop.Windows(x, includeMinimized: false).Where(w => w.AppClass != "dispctrl-linux").ToList();
        });
        _windows.Children.Clear();
        if (list is null) { _windows.Children.Add(Views.CardBuilder.Caption("No X display: window tools need an X11 session.")); return; }
        foreach (var w in list.Take(30))
        {
            string id = $"0x{w.Id:x}";
            TextBlock status = null!;
            var pin = new ToggleButton { Content = w.Above ? "Pinned" : "Pin", IsChecked = w.Above };
            pin.Click += async (_, _) => { CardBuilder.Show(status, await Services.DeskActions.RunAsync("pin", pin.IsChecked == true ? "on" : "off", "--window", id)); pin.Content = pin.IsChecked == true ? "Pinned" : "Pin"; };
            var move = new Button { Content = "Next display" };
            move.Click += async (_, _) => CardBuilder.Show(status, await Services.DeskActions.RunAsync("placement", "move", "--window", id, "--to", "next"));
            var span = new Button { Content = "Span" };
            span.Click += async (_, _) => CardBuilder.Show(status, await Services.DeskActions.RunAsync("placement", "span", "--window", id));
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { pin, move, span } };
            _windows.Children.Add(Views.CardBuilder.Card(null, string.IsNullOrWhiteSpace(w.Title) ? w.AppClass ?? id : w.Title, w.AppClass, row, out status));
        }
    }
}
