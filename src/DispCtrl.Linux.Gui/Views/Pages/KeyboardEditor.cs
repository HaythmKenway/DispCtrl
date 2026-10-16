using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Lighting;
using DispCtrl.Linux.Settings;
using static DispCtrl.Linux.Gui.Views.CardBuilder;

namespace DispCtrl.Linux.Gui.Views.Pages;

/// <summary>A keyboard drawn from <see cref="KeyboardLayout"/>, each cap in the
/// colour OpenRGB holds for its LED, to select keys on and colour.</summary>
/// <remarks>
/// Selecting is by click or drag across the caps; Ctrl adds to the selection
/// instead of starting a new one. The colours go out as one
/// <c>rgb keys</c> command, so the window does what the command line does.
/// Smoothness: a drag touches only the caps whose state changed (the key under
/// the pointer comes from a bucketed index, not a scan); colours show at once and
/// go to OpenRGB off the UI thread, one write at a time with the newest colours
/// for each key coalesced behind it, and a refused write puts the old colours
/// back; colour changes fade over 150 ms unless the desktop has animations off.
/// </remarks>
public sealed class KeyboardEditor : StackPanel
{
    private const double Unit = 38;

    private readonly RgbDevice _device;
    private readonly KeyboardGeometry _geometry;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Dictionary<int, (Border Cap, TextBlock Text)> _caps = [];
    private readonly HashSet<int> _selected = [];
    private readonly Dictionary<int, (byte R, byte G, byte B)> _colours = [];
    private readonly ColorPicker _picker = new() { Color = Colors.White };
    private readonly TextBlock _status = new() { Classes = { "status" }, IsVisible = false };
    private readonly Dictionary<int, (byte R, byte G, byte B)> _pending = [];
    private readonly Dictionary<int, (byte R, byte G, byte B)> _confirmed = [];
    private static readonly IBrush Selected = Brushes.DodgerBlue;
    private static readonly IBrush Unselected = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
    private static readonly Task<bool> AnimationsOn = Task.Run(DesktopAnimates);
    private bool _dragging, _sending, _animate, _settingPicker;
    private int _hover = -1;
    private int _lastHit = -1;

    public KeyboardEditor(RgbDevice device)
    {
        _device = device;
        _geometry = KeyboardLayout.Build(device);
        Spacing = 8;
        Margin = new Thickness(0, 10, 0, 0);

        for (int i = 0; i < device.LedCount; i++)
        {
            uint c = device.Colours is { } held && i < held.Count ? held[i] : 0;
            _colours[i] = ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }
        foreach (var c in _colours) _confirmed[c.Key] = c.Value;

        _canvas.Width = _geometry.Width * Unit;
        _canvas.Height = _geometry.Height * Unit;
        // Unlabelled keys off the grid are LEDs the layout could not place; drawing them
        // gave a row of blank caps under the keyboard.
        foreach (var key in _geometry.Keys.Where(k => k.OnGrid || k.Label.Length > 0))
        {
            var text = new TextBlock
            {
                Text = key.Label, FontSize = key.Label.Length > 4 ? 9 : 12, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
            var cap = new Border
            {
                Width = key.W * Unit - 3, Height = key.H * Unit - 3, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2),
                Child = text, IsHitTestVisible = false,
            };
            Canvas.SetLeft(cap, key.X * Unit + 1.5);
            Canvas.SetTop(cap, key.Y * Unit + 1.5);
            _canvas.Children.Add(cap);
            _caps[key.Led] = (cap, text);
            Paint(key.Led);
        }
        _canvas.PointerExited += (_, _) => Hover(-1);
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += (_, _) => EndDrag();
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        AutomationProperties.SetName(_canvas, "Keyboard keys");
        ToolTip.SetTip(_canvas, "Click or drag across keys to select them; hold Ctrl to add to the selection.");
        // Scaled to the card's width, never scrolled: a clipped numpad hid keys.
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Child = _canvas });

        AutomationProperties.SetName(_picker, "Custom colour");
        var swatches = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, hex) in new[] { ("White", "#FFFFFF"), ("Red", "#FF0000"), ("Orange", "#FF6000"), ("Yellow", "#FFE000"), ("Green", "#00FF00"),
                     ("Cyan", "#00FFFF"), ("Blue", "#0000FF"), ("Purple", "#8000FF"), ("Pink", "#FF40A0"), ("Off", "#000000") })
        {
            var dot = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(0), CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.Parse(hex)), Classes = { "pop", "dot" },
            };
            ToolTip.SetTip(dot, $"{name}: colour the selected keys at once");
            AutomationProperties.SetName(dot, $"Colour selected keys {name}");
            dot.Click += async (_, _) =>
            {
                _settingPicker = true;
                _picker.Color = Color.Parse(hex);
                _settingPicker = false;
                await Send(_selected.ToDictionary(l => l, _ => Picked()));
            };
            swatches.Children.Add(dot);
        }
        // The custom colour applies as it is chosen, so there is no Apply button to find.
        _picker.ColorChanged += async (_, _) =>
        {
            if (!_settingPicker && _selected.Count > 0) await Send(_selected.ToDictionary(l => l, _ => Picked()));
        };
        swatches.Children.Add(_picker);
        var hint = new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Click or drag across keys to select them (Ctrl adds), then click a colour.",
        };
        var effects = new ComboBox { PlaceholderText = "Fill the whole keyboard…", MinWidth = 220, Margin = new Thickness(0, 0, 8, 4) };
        AutomationProperties.SetName(effects, "Fill the whole keyboard");
        effects.ItemsSource = new[] { "Gradient", "Rainbow", "Rows" };
        effects.SelectionChanged += async (_, _) =>
        {
            var which = effects.SelectedItem as string;
            effects.SelectedItem = null;
            if (which == "Gradient") await Preset((x, _) => Shade(Picked(), x * 120));
            else if (which == "Rainbow") await Preset((x, _) => Hue(x * 330, 1, 1));
            else if (which == "Rows") await Preset((_, y) => Hue(y * 55, 1, 1));
        };
        var selection = new WrapPanel
        {
            Children =
            {
                Button("Select all", "Select every key", () => Select(_geometry.Keys.Where(k => k.OnGrid).Select(k => k.Led))),
                Button("Clear", "Select nothing", () => Select([])),
                effects,
            },
        };
        var more = new Expander
        {
            Header = "Saved looks and colour fixes", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { Presets(), Order(), Calibrate() } },
        };
        Children.Insert(0, hint);
        Children.Insert(0, Looks());
        Children.Add(swatches);
        Children.Add(selection);
        Children.Add(more);
        Children.Add(_status);
        _ = FadeColoursAsync();
    }

    /// <summary>The ready-made looks, one click each: <see cref="KeyLooks"/> works out
    /// every key's colour from the keyboard's layout, so they fit any per-key board.</summary>
    private StackPanel Looks()
    {
        var chips = new WrapPanel();
        var device = (_device.Index + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var look in KeyLooks.Names)
        {
            var chip = new Button { Content = look, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 5), Margin = new Thickness(0, 0, 8, 8), Classes = { "pop" } };
            ToolTip.SetTip(chip, KeyLooks.Describe(look));
            AutomationProperties.SetName(chip, $"{look} look");
            chip.Click += async (_, _) =>
            {
                var error = await DeskActions.RunAsync("rgb", "look", look, "--device", device);
                Show(_status, error);
                if (error is null) await ReloadColours();
            };
            chips.Children.Add(chip);
        }
        return new StackPanel { Children = { new TextBlock { Text = "Looks", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) }, chips } };
    }

    /// <summary>Saved colour sets for this keyboard: pick one to put it back, or
    /// name the colours on the keyboard now to keep them. Written by the same
    /// <c>rgb preset</c> commands the command line uses.</summary>
    private StackPanel Presets()
    {
        var chips = new WrapPanel();
        var name = new TextBox { Width = 220, PlaceholderText = "Save these colours as…  (Enter)", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(name, "Name for this look");
        var device = (_device.Index + 1).ToString(CultureInfo.InvariantCulture);
        var empty = new TextBlock { Classes = { "caption" }, Text = "No saved looks yet. Colour the keyboard, then name it below." };
        void Reload()
        {
            chips.Children.Clear();
            var names = SettingsStore.Load().Lighting.KeyPresets.Where(p => p.Keyboard == _device.Name).Select(p => p.Name).ToList();
            empty.IsVisible = names.Count == 0;
            foreach (var n in names)
            {
                var chip = new Button { Content = n, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 5), Margin = new Thickness(0, 0, 8, 8), Classes = { "pop" } };
                ToolTip.SetTip(chip, "Put this look on the keyboard. Right-click to delete it.");
                AutomationProperties.SetName(chip, $"Use {n}");
                chip.Click += async (_, _) =>
                {
                    var error = await DeskActions.RunAsync("rgb", "preset", "apply", n, "--device", device);
                    Show(_status, error);
                    if (error is null) await ReloadColours();
                };
                var delete = new MenuItem { Header = "Delete" };
                delete.Click += async (_, _) =>
                {
                    var error = await DeskActions.RunAsync("rgb", "preset", "delete", n);
                    Show(_status, error);
                    if (error is null) Reload();
                };
                chip.ContextFlyout = new MenuFlyout { Items = { delete } };
                chips.Children.Add(chip);
            }
        }
        Reload();
        name.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            var label = name.Text?.Trim() ?? "";
            if (label.Length == 0) { Show(_status, "Type a name first, such as Gaming or Night."); return; }
            await WaitForSends();
            var error = await DeskActions.RunAsync("rgb", "preset", "save", label, "--device", device);
            Show(_status, error);
            if (error is null) { name.Text = ""; Reload(); }
        };
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Saved looks", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) },
                empty, chips, name,
            },
        };
    }

    /// <summary>For a keyboard whose colours show as others (red as green): its
    /// LED colour order, which <c>rgb order</c> sets. Redrawn from the device after a change.</summary>
    private StackPanel Order()
    {
        string[] orders = ["RGB", "GRB", "BGR", "BRG", "RBG", "GBR"];
        var box = new ComboBox { ItemsSource = orders, Margin = new Thickness(0, 0, 8, 0), MinWidth = 100 };
        box.SelectedItem = LightingControl.OrderOf(SettingsStore.Load().Lighting, _device.Name);
        AutomationProperties.SetName(box, "Colour order");
        ToolTip.SetTip(box, "Pick the order that makes the keyboard show the colours drawn here");
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is not string order) return;
            if (order == LightingControl.OrderOf(SettingsStore.Load().Lighting, _device.Name)) return;
            var error = await DeskActions.RunAsync("rgb", "order", order, "--device", (_device.Index + 1).ToString(CultureInfo.InvariantCulture));
            if (error is null) error = await DeskActions.RunAsync("rgb", "keys", string.Join(',', _colours.Where(c => _caps.ContainsKey(c.Key)).Select(c => $"led {c.Key}=#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}")), "--device", (_device.Index + 1).ToString(CultureInfo.InvariantCulture));
            Show(_status, error);
        };
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Colours on the keyboard look wrong? Choose its colour order", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                box,
            },
        };
    }

    /// <summary>Finds a keyboard's colour order by asking what it shows: red is sent
    /// on the first byte and green on the second, and the two answers fix all
    /// three. Works for any device, known or not.</summary>
    private StackPanel Calibrate()
    {
        var device = (_device.Index + 1).ToString(CultureInfo.InvariantCulture);
        var say = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var answers = new WrapPanel { IsVisible = false };
        var start = Button("Fix the colours…", "Light the keyboard and answer what colour you see; DispCtrl works out the rest", () => { });
        string? first = null;
        async Task Ask(string text, string wireColour)
        {
            // The order is reset first, so what is sent is what the bytes themselves light.
            var error = await DeskActions.RunAsync("rgb", "colour", wireColour, "--device", device);
            if (error is not null) { Show(_status, error); return; }
            say.Text = text;
            say.IsVisible = answers.IsVisible = true;
        }
        foreach (var name in new[] { "Red", "Green", "Blue" })
        {
            var b = Button(name, $"The keyboard shows {name.ToLowerInvariant()}", async () =>
            {
                char seen = name[0];
                if (first is null)
                {
                    first = seen.ToString();
                    await Ask("Now it should be green on the keyboard drawn here. What colour do you see on the keyboard?", "#00FF00");
                    return;
                }
                char second = seen;
                var third = "RGB".Except([first[0], second]).ToList();
                answers.IsVisible = say.IsVisible = false;
                var wasFirst = first;
                first = null;
                if (third.Count != 1) { Show(_status, "Those two answers cannot both be right. Try again."); return; }
                var order = $"{wasFirst}{second}{third[0]}";
                var error = await DeskActions.RunAsync("rgb", "order", order, "--device", device);
                if (error is null) error = await DeskActions.RunAsync("rgb", "keys", string.Join(',', _colours.Where(c => _caps.ContainsKey(c.Key)).Select(c => $"led {c.Key}=#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}")), "--device", device);
                Show(_status, error ?? $"Colour order set to {order}.");
            });
            answers.Children.Add(b);
        }
        start.Click += async (_, _) =>
        {
            first = null;
            await DeskActions.RunAsync("rgb", "order", "RGB", "--device", device);
            await Ask("The keyboard should now be red. What colour do you see on the keyboard?", "#FF0000");
        };
        return new StackPanel { Spacing = 4, Children = { start, say, answers } };
    }

    private async Task WaitForSends()
    {
        for (int i = 0; i < 50 && _sending; i++) await Task.Delay(40);
    }

    /// <summary>Reads the keyboard's colours back after a saved set was applied, so the drawing matches.</summary>
    private async Task ReloadColours()
    {
        var devices = await Task.Run(() =>
        {
            try { using var rgb = LightingControl.Connect(SettingsStore.Load().Lighting); return rgb.Devices(); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or AggregateException) { return null; }
        });
        if (devices?.FirstOrDefault(d => d.Index == _device.Index) is not { Colours: { } held }) return;
        for (int i = 0; i < _device.LedCount && i < held.Count; i++)
        {
            _colours[i] = ((byte)held[i], (byte)(held[i] >> 8), (byte)(held[i] >> 16));
            _confirmed[i] = _colours[i];
            if (_caps.ContainsKey(i)) PaintFill(i);
        }
    }

    /// <summary>Colour changes ease in over 150 ms, once the desktop's own
    /// setting (animations on or off) has been read, off the UI thread.</summary>
    private async Task FadeColoursAsync()
    {
        if (!await AnimationsOn) return;
        _animate = true;
        foreach (var (cap, _) in _caps.Values)
        {
            cap.RenderTransform = TransformOperations.Parse("scale(1)");
            cap.Transitions =
            [
                new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new CubicEaseOut() },
            ];
        }
    }

    /// <summary>False when the desktop turns animations off (GNOME's reduced
    /// motion); true when it cannot be told.</summary>
    private static bool DesktopAnimates()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(info);
            if (p is null || !p.WaitForExit(1000)) return true;
            return !p.StandardOutput.ReadToEnd().Trim().Equals("false", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return true; }
    }

    private (byte R, byte G, byte B) Picked()
    {
        var c = _picker.Color;
        return (c.R, c.G, c.B);
    }

    private static Button Button(string text, string tip, Func<Task> click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), Classes = { "pop" } };
        ToolTip.SetTip(b, tip);
        AutomationProperties.SetName(b, text);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static Button Button(string text, string tip, Action click) => Button(text, tip, () => { click(); return Task.CompletedTask; });

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _lastHit = -1;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) Select([]);
        AddAt(point.Position);
        e.Pointer.Capture(_canvas);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_dragging) AddAt(p);
        Hover(_geometry.KeyAt(p.X / Unit, p.Y / Unit)?.Led ?? -1);
    }

    /// <summary>The key under the pointer lifts slightly, so it is plain what a click will take.</summary>
    private void Hover(int led)
    {
        if (led == _hover) return;
        Scale(_hover, 1);
        _hover = led;
        Scale(led, 1.1);
        _canvas.Cursor = led >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private void Scale(int led, double to)
    {
        if (_animate && _caps.TryGetValue(led, out var cap)) cap.Cap.RenderTransform = TransformOperations.Parse($"scale({to.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>A quick press-and-release on a key whose colour just changed, the
    /// nearest a desktop gets to feedback under the finger.</summary>
    private async void Pop(IEnumerable<int> leds)
    {
        if (!_animate) return;
        var list = leds.Where(_caps.ContainsKey).Take(200).ToList();
        foreach (var l in list) Scale(l, 0.86);
        await Task.Delay(110);
        foreach (var l in list) Scale(l == _hover ? -1 : l, 1);
        Scale(_hover, 1.1);
    }

    private void EndDrag()
    {
        _dragging = false;
        _lastHit = -1;
    }

    /// <summary>Selects the key at the point; a move within the same key does nothing.</summary>
    private void AddAt(Point p)
    {
        if (_geometry.KeyAt(p.X / Unit, p.Y / Unit) is not { } k || k.Led == _lastHit) return;
        _lastHit = k.Led;
        if (_selected.Add(k.Led)) PaintBorder(k.Led);
        ToolTip.SetTip(_canvas, $"{(k.Label.Length > 0 ? k.Label + ", " : "")}LED {k.Led}");
    }

    /// <summary>Makes <paramref name="leds"/> the selection, repainting only the caps that change.</summary>
    private void Select(IEnumerable<int> leds)
    {
        var next = leds.ToHashSet();
        var changed = _selected.Except(next).Concat(next.Except(_selected)).ToList();
        _selected.Clear();
        _selected.UnionWith(next);
        foreach (var led in changed) PaintBorder(led);
    }

    private void WholeRow()
    {
        var rows = _geometry.Keys.Where(k => _selected.Contains(k.Led) && k.OnGrid).Select(k => k.Y).ToList();
        Select(_selected.Concat(_geometry.Keys.Where(k => k.OnGrid && rows.Any(y => Math.Abs(k.Y - y) < 0.5)).Select(k => k.Led)));
    }

    /// <summary>Colours every key on the grid by where it is: its centre as a
    /// fraction across the keyboard and down it.</summary>
    private async Task Preset(Func<double, double, (byte R, byte G, byte B)> colour)
    {
        double w = Math.Max(_geometry.Keys.Where(k => k.OnGrid).Max(k => k.X + k.W), 1);
        var rows = _geometry.Keys.Where(k => k.OnGrid).Select(k => k.Y).Distinct().Order().ToList();
        await Send(_geometry.Keys.Where(k => k.OnGrid).ToDictionary(k => k.Led,
            k => colour((k.X + k.W / 2) / w, rows.IndexOf(k.Y))));
    }

    /// <summary>Shows the colours at once, then writes them: one write at a time,
    /// and what was asked while it ran is merged (the newest colour per key wins)
    /// into the next. A refused write puts the keys back as the device last had them.</summary>
    private async Task Send(Dictionary<int, (byte R, byte G, byte B)> colours)
    {
        if (colours.Count == 0) { Show(_status, "Select some keys first: click or drag across them."); return; }
        foreach (var c in colours)
        {
            _colours[c.Key] = c.Value;
            _pending[c.Key] = c.Value;
            if (_caps.ContainsKey(c.Key)) PaintFill(c.Key);
        }
        Pop(colours.Keys);
        if (_sending) return;
        _sending = true;
        try
        {
            while (_pending.Count > 0)
            {
                var batch = new Dictionary<int, (byte R, byte G, byte B)>(_pending);
                _pending.Clear();
                var spec = string.Join(',', batch.Select(c => $"led {c.Key}=#{c.Value.R:X2}{c.Value.G:X2}{c.Value.B:X2}"));
                var error = await DeskActions.RunAsync("rgb", "keys", spec, "--device", (_device.Index + 1).ToString(CultureInfo.InvariantCulture));
                Show(_status, error);
                foreach (var c in batch)
                {
                    if (error is null) _confirmed[c.Key] = c.Value;
                    else if (!_pending.ContainsKey(c.Key)) { _colours[c.Key] = _confirmed[c.Key]; if (_caps.ContainsKey(c.Key)) PaintFill(c.Key); }
                }
            }
        }
        finally { _sending = false; }
    }

    private void PaintFill(int led)
    {
        var (cap, text) = _caps[led];
        var (r, g, b) = _colours[led];
        cap.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        // Dark labels on light caps, and the reverse, by the cap's luminance.
        text.Foreground = 0.299 * r + 0.587 * g + 0.114 * b > 140 ? Brushes.Black : Brushes.White;
    }

    private void PaintBorder(int led) => _caps[led].Cap.BorderBrush = _selected.Contains(led) ? Selected : Unselected;

    private void Paint(int led)
    {
        PaintFill(led);
        PaintBorder(led);
    }

    private static (byte R, byte G, byte B) Shade((byte R, byte G, byte B) from, double degrees)
    {
        var (h, s, v) = ToHsv(from);
        return Hue(h + degrees, s, v);
    }

    private static (double H, double S, double V) ToHsv((byte R, byte G, byte B) c)
    {
        var hsv = new Color(255, c.R, c.G, c.B).ToHsv();
        return (hsv.H, hsv.S, hsv.V);
    }

    private static (byte R, byte G, byte B) Hue(double degrees, double saturation, double value)
    {
        var rgb = new HsvColor(1, ((degrees % 360) + 360) % 360, saturation, value).ToRgb();
        return (rgb.R, rgb.G, rgb.B);
    }
}
