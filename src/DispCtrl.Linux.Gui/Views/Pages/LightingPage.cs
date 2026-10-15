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
    private readonly StackPanel _controls = new();
    private readonly Border _setup;
    private readonly TextBlock _setupWhere = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    public LightingPage()
    {
        var (_, body) = Page(this, "Lighting",
            "Keyboards, mice, fans, strips and boards, through OpenRGB, which knows each maker's devices. DispCtrl sets their colour or effect; presets can too.");

        _setup = Setup();
        body.Children.Add(_setup);
        body.Children.Add(_controls);

        _controls.Children.Add(Section("Every device"));
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
            b.Click += async (_, _) => await Apply(_allStatus!, "rgb", "colour", hex);
            swatches.Children.Add(b);
        }
        var hexBox = new TextBox { Width = 110, PlaceholderText = "#RRGGBB", Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(hexBox, "Colour");
        var set = new Button { Content = "Set" };
        set.Click += async (_, _) =>
        {
            var text = hexBox.Text?.Trim() ?? "";
            if (text.Length == 0) { Show(_allStatus!, "Type a colour first: #FF9329, or a name such as warm or blue."); return; }
            await Apply(_allStatus!, "rgb", "colour", text);
        };
        var off = new Button { Content = "Off", Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(off, "Lighting off");
        off.Click += async (_, _) => await Apply(_allStatus!, "rgb", "off");
        var custom = new StackPanel { Orientation = Orientation.Horizontal, Children = { hexBox, set, off } };
        var both = new StackPanel { Children = { swatches, custom } };
        _controls.Children.Add(Card(FASymbol.ColorBackground, "Colour", "Every LED on every device, in its direct mode.", null, out _allStatus, both));

        var refresh = new Button { Content = "Refresh", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(refresh, "Look for lighting devices again");
        refresh.Click += async (_, _) => await LoadAsync();
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 28, 0, 8) };
        var title = Section("Devices");
        title.Margin = new Thickness(2, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(title);
        Grid.SetColumn(refresh, 1);
        heading.Children.Add(refresh);
        _controls.Children.Add(heading);
        _controls.Children.Add(_devices);
        _controls.IsVisible = false;
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
        // Without OpenRGB nothing here can work: one card that says how to get
        // it, rather than controls that only answer with errors.
        _setup.IsVisible = devices is null;
        _controls.IsVisible = devices is not null;
        _setupWhere.Text = $"DispCtrl looks for it at {settings.Host}:{settings.Port} (dispctrl-linux rgb server HOST:PORT to change).";
        if (devices is null) return;
        if (devices.Count == 0) _devices.Children.Add(Caption("OpenRGB is running and found no lighting devices."));
        foreach (var d in devices)
        {
            var effects = new ComboBox { ItemsSource = d.Modes.Select(m => m.Name).ToList(), SelectedIndex = d.ActiveMode };
            TextBlock status = null!;
            int device = d.Index + 1;
            effects.SelectionChanged += async (_, _) =>
            {
                if (effects.SelectedItem is string mode)
                    await Apply(status, "rgb", "mode", mode, "--device", device.ToString(CultureInfo.InvariantCulture));
            };
            _devices.Children.Add(Card(Icon(d.Type), d.Name, $"{d.Vendor} {d.TypeName}, {d.LedCount} LEDs".Trim(), effects, out status));
        }
    }

    /// <summary>Runs a lighting command; when OpenRGB has stopped answering,
    /// the page goes back to saying how to start it rather than leaving an
    /// error under a device that is no longer reachable.</summary>
    private async Task Apply(TextBlock status, params string[] args)
    {
        var error = await DeskActions.RunAsync(args);
        Show(status, error);
        if (error is not null && error.Contains("does not answer", StringComparison.Ordinal)) await LoadAsync();
    }

    /// <summary>What to do when OpenRGB does not answer: get it, switch its
    /// server on, and look again.</summary>
    private Border Setup()
    {
        var steps = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (n, text) in new[]
        {
            ("1", "Install OpenRGB: from your distribution (sudo apt install openrgb, sudo dnf install openrgb) or Flathub."),
            ("2", "Start its server: openrgb --server (headless, best at sign-in), or in its window Settings, SDK Server, Start server."),
            ("3", "Come back and press Try again. Keep it running: DispCtrl sets the lighting through it."),
        })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
            var badge = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = n, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            // Bound, not looked up: the theme's accent is not reachable until the
            // page is in the window, and a lookup then crashed the page.
            badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("AccentFillColorDefaultBrush"));
            row.Children.Add(badge);
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            steps.Children.Add(row);
        }
        var get = new Button { Content = "Get OpenRGB", Classes = { "accent" } };
        get.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri("https://openrgb.org"));
        };
        var retry = new Button { Content = "Try again" };
        retry.Click += async (_, _) => await LoadAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { get, retry } };
        var body = new StackPanel { Children = { steps, buttons } };
        body.Children.Insert(0, _setupWhere);
        var card = Card(FASymbol.ColorBackground, "Lighting needs OpenRGB",
            "OpenRGB knows the keyboards, mice, fans and strips of hundreds of makers. DispCtrl sets their colours through it once it is running.",
            null, out _, body);
        card.Margin = new Thickness(0, 20, 0, 0);
        card.IsVisible = false;
        return card;
    }

    private static FASymbol Icon(int type) => type switch
    {
        5 or 18 => FASymbol.Keyboard,
        6 or 7 => FASymbol.Target,
        8 or 9 or 12 => FASymbol.Speaker2,
        _ => FASymbol.ColorLine,
    };
}
