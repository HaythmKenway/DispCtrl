using System.Globalization;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Lighting;
using DispCtrl.Linux.Settings;
using FluentAvalonia.UI.Controls;
using static DispCtrl.Linux.Gui.Views.CardBuilder;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>Keyboard, mouse, fan and strip lighting, through OpenRGB: one
/// colour everywhere, or each device's own effects.</summary>
public sealed class LightingPage : UserControl
{
    private static readonly (string Name, string Hex)[] Swatches =
        [("Warm", "#FF9329"), ("White", "#FFFFFF"), ("Red", "#FF0000"), ("Orange", "#FF6000"), ("Green", "#00FF00"),
         ("Cyan", "#00FFFF"), ("Blue", "#0000FF"), ("Purple", "#8000FF"), ("Pink", "#FF40A0")];

    private readonly StackPanel _devices = List();
    private readonly TextBlock _allStatus;
    private readonly Border _notice;
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap };

    public LightingPage()
    {
        var (_, body) = Page(this, "Lighting",
            "Keyboards, mice, fans, strips and boards, through OpenRGB, which knows each maker's devices. DispCtrl sets their colour or effect; presets can too.");

        var retry = new Button { Content = "Try again", Margin = new Thickness(0, 8, 0, 0) };
        retry.Click += (_, _) => _ = LoadAsync();
        var noticeBody = new StackPanel();
        noticeBody.Children.Add(_noticeText);
        noticeBody.Children.Add(retry);
        _notice = new Border { Classes = { "notice", "info" }, Margin = new Thickness(0, 20, 0, 0), Child = noticeBody, IsVisible = false };
        body.Children.Add(_notice);

        body.Children.Add(Section("Every device"));
        var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (name, hex) in Swatches)
        {
            var b = new Button
            {
                Width = 36, Height = 36, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(hex)), CornerRadius = new CornerRadius(18),
            };
            ToolTip.SetTip(b, name);
            AutomationProperties.SetName(b, $"Lighting {name}");
            b.Click += async (_, _) => Show(_allStatus!, await DeskActions.RunAsync("rgb", "colour", hex));
            swatches.Children.Add(b);
        }
        var hexBox = new TextBox { Width = 110, PlaceholderText = "#RRGGBB", Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(hexBox, "Colour");
        var set = new Button { Content = "Set" };
        set.Click += async (_, _) => Show(_allStatus!, await DeskActions.RunAsync("rgb", "colour", hexBox.Text?.Trim() ?? ""));
        var off = new Button { Content = "Off", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(off, "Lighting off");
        off.Click += async (_, _) => Show(_allStatus!, await DeskActions.RunAsync("rgb", "off"));
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Children = { hexBox, set, off } };
        var both = new StackPanel { Children = { swatches, custom } };
        body.Children.Add(Card(FASymbol.ColorBackground, "Colour", "Every LED on every device, in its direct mode.", null, out _allStatus, both));

        body.Children.Add(Section("Devices"));
        body.Children.Add(_devices);
        AttachedToVisualTree += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var settings = SettingsStore.Load().Lighting;
        IReadOnlyList<RgbDevice>? devices;
        try
        {
            devices = await Task.Run(() =>
            {
                using var rgb = LightingControl.Connect(settings);
                return rgb.Devices();
            });
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException)
        {
            devices = null;
        }
        _devices.Children.Clear();
        _notice.IsVisible = devices is null;
        _noticeText.Text = devices is null
            ? $"{LightingControl.Unreachable(settings)}. OpenRGB is in most distributions' repositories and on Flathub; its udev rules give it the devices."
            : "";
        if (devices is null)
        {
            _devices.Children.Add(Caption("Each device OpenRGB finds is listed here, with its own effects, once it answers."));
            return;
        }
        if (devices.Count == 0) _devices.Children.Add(Caption("OpenRGB is running and found no lighting devices."));
        foreach (var d in devices)
        {
            var effects = new ComboBox { ItemsSource = d.Modes.Select(m => m.Name).ToList(), SelectedIndex = d.ActiveMode };
            TextBlock status = null!;
            int device = d.Index + 1;
            effects.SelectionChanged += async (_, _) =>
            {
                if (effects.SelectedItem is string mode)
                    Show(status, await DeskActions.RunAsync("rgb", "mode", mode, "--device", device.ToString(CultureInfo.InvariantCulture)));
            };
            _devices.Children.Add(Card(Icon(d.Type), d.Name, $"{d.Vendor} {d.TypeName}, {d.LedCount} LEDs".Trim(), effects, out status));
        }
    }

    private static FASymbol Icon(int type) => type switch
    {
        5 or 18 => FASymbol.Keyboard,
        6 or 7 => FASymbol.Target,
        8 or 9 or 12 => FASymbol.Speaker2,
        _ => FASymbol.ColorLine,
    };
}
