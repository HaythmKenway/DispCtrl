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
    private readonly TextBlock _setupStatus = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _kernelNote = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _start = new() { Classes = { "accent" } };

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
            var b = Dot(Color.Parse(hex), name);
            b.Click += async (_, _) => await Apply(_allStatus!, "rgb", "colour", hex);
            swatches.Children.Add(b);
        }
        var off = Dot(Color.Parse("#2B2B2B"), "Off");
        off.Content = new FASymbolIcon { Symbol = FASymbol.Cancel, FontSize = 14 };
        off.BorderBrush = Brushes.Gray;
        off.BorderThickness = new Thickness(1);
        off.Click += async (_, _) => await Apply(_allStatus!, "rgb", "off");
        swatches.Children.Add(off);

        var hexBox = new TextBox { Width = 200, PlaceholderText = "Custom colour, e.g. #FF9329", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(hexBox, "Colour");
        hexBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            var text = hexBox.Text?.Trim() ?? "";
            if (text.Length == 0) { Show(_allStatus!, "Type a colour first: #FF9329, or a name such as warm or blue."); return; }
            await Apply(_allStatus!, "rgb", "colour", text);
        };
        var both = new StackPanel { Children = { swatches, hexBox } };
        _controls.Children.Add(Card(FASymbol.ColorBackground, "Colour", "Every LED on every device, in its direct mode.", null, out _allStatus, both));

        var refresh = new Button { Content = new FASymbolIcon { Symbol = FASymbol.Refresh }, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(refresh, "Look for lighting devices again");
        ToolTip.SetTip(refresh, "Look for lighting devices again");
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
        var kernel = devices is null ? SysfsLeds.Enumerate() : [];
        _controls.IsVisible = devices is not null || kernel.Count > 0;
        _start.Content = OpenRgbSetup.Find() is null && OpenRgbSetup.SnapLimit() is null ? "Set up lighting" : "Start lighting";
        _start.IsEnabled = OpenRgbSetup.SnapLimit() is null;
        _setupStatus.Text = OpenRgbSetup.SnapLimit() ?? _setupStatus.Text;
        _setupWhere.Text = $"DispCtrl looks for it at {settings.Host}:{settings.Port} (dispctrl-linux rgb server HOST:PORT to change).";
        _kernelNote.IsVisible = kernel.Count > 0;
        _kernelNote.Text = "OpenRGB is not running, so these are the keyboard backlights the kernel exposes: the basic path, with a colour only where the keyboard has one and otherwise its brightness. "
            + "Setting up lighting adds every other keyboard, mouse, fan and strip, and their effects.";
        if (devices is null)
        {
            _devices.Children.Add(_kernelNote);
            foreach (var led in kernel)
                _devices.Children.Add(Card(FASymbol.Keyboard, led.Name, led.Describe(), null, out _));
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
                    await Apply(status, "rgb", "mode", mode, "--device", device.ToString(CultureInfo.InvariantCulture));
            };
            // A light for each key: the keyboard itself, to colour key by key.
            var header = Card(Icon(d.Type), d.Name, $"{d.Vendor} {d.TypeName}, {d.LedCount} LEDs".Trim(), effects, out status);
            if (!d.HasPerKeyLeds) { _devices.Children.Add(header); continue; }
            // The editor gets a card of its own, the full width: beside the effect
            // picker it was squeezed into the text column and its numpad was cut off.
            var editor = new Border { Classes = { "card" }, Margin = new Thickness(0, 4, 0, 12), Child = new KeyboardEditor(d) };
            header.Margin = new Thickness(0, 0, 0, 0);
            _devices.Children.Add(header);
            _devices.Children.Add(editor);
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
            ("1", "Press Set up lighting: DispCtrl downloads the official OpenRGB (checked against a pinned SHA-256) if none is installed, and starts it headless."),
            ("2", "Press Start lighting when OpenRGB is installed but stopped; DispCtrl also starts it by itself when a lighting command needs it."),
            ("3", "For USB devices OpenRGB needs its udev rules: the status line shows the one command to run as root."),
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
        _start.Click += async (_, _) => await StartLightingAsync();
        var get = new HyperlinkButton { Content = "Get OpenRGB" };
        get.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri("https://openrgb.org"));
        };
        var retry = new HyperlinkButton { Content = "Try again" };
        retry.Click += async (_, _) => await LoadAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { _start, get, retry } };
        var body = new StackPanel { Children = { steps, buttons, _setupStatus } };
        body.Children.Insert(0, _setupWhere);
        var card = Card(FASymbol.ColorBackground, "Lighting needs OpenRGB",
            "OpenRGB knows the keyboards, mice, fans and strips of hundreds of makers. DispCtrl sets their colours through it once it is running.",
            null, out _, body);
        card.Margin = new Thickness(0, 20, 0, 0);
        card.IsVisible = false;
        return card;
    }

    /// <summary>Starts (and, if there is none, first downloads) OpenRGB off the
    /// UI thread, with the steps as status text; the download is about 37 MB.</summary>
    private async Task StartLightingAsync()
    {
        _start.IsEnabled = false;
        void Say(string text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _setupStatus.Text = text);
        try
        {
            var settings = SettingsStore.Load().Lighting;
            var message = await Task.Run(() =>
            {
                if (OpenRgbSetup.Find() is null)
                {
                    var got = OpenRgbSetup.Download(Say);
                    if (!got.Ok) return got.Message;
                }
                var started = OpenRgbSetup.Start(settings, Say);
                return started.Ok ? OpenRgbSetup.UdevAdvice(OpenRgbSetup.Find()) is { } udev ? started.Message + " " + udev : started.Message : started.Message;
            });
            _setupStatus.Text = message;
        }
        finally { _start.IsEnabled = OpenRgbSetup.SnapLimit() is null; }
        await LoadAsync();
    }

    internal static Button Dot(Color colour, string name)
    {
        var b = new Button
        {
            Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0),
            Background = new SolidColorBrush(colour), CornerRadius = new CornerRadius(17), Classes = { "pop", "dot" },
        };
        ToolTip.SetTip(b, name);
        AutomationProperties.SetName(b, $"Lighting {name}");
        return b;
    }

    private static FASymbol Icon(int type) => type switch
    {
        5 or 18 => FASymbol.Keyboard,
        6 or 7 => FASymbol.Target,
        8 or 9 or 12 => FASymbol.Speaker2,
        _ => FASymbol.ColorLine,
    };
}
