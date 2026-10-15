using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Lighting;
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
    private const double Unit = 30;

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
    private bool _dragging, _sending;
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
        foreach (var key in _geometry.Keys)
        {
            var text = new TextBlock
            {
                Text = key.Label, FontSize = key.Label.Length > 4 ? 8 : 10, HorizontalAlignment = HorizontalAlignment.Center,
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
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += (_, _) => EndDrag();
        _canvas.PointerCaptureLost += (_, _) => EndDrag();
        AutomationProperties.SetName(_canvas, "Keyboard keys");
        ToolTip.SetTip(_canvas, "Click or drag across keys to select them; hold Ctrl to add to the selection.");
        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _canvas,
        });

        AutomationProperties.SetName(_picker, "Key colour");
        var apply = Button("Apply", "Colour the selected keys", async () => await Send(_selected.ToDictionary(l => l, _ => Picked())));
        apply.Classes.Add("accent");
        var selection = new WrapPanel
        {
            Children =
            {
                _picker, apply,
                Button("Select all", "Select every key", () => Select(_geometry.Keys.Where(k => k.OnGrid).Select(k => k.Led))),
                Button("Whole row", "Select the rows of the selected keys", WholeRow),
                Button("Clear", "Select nothing", () => Select([])),
            },
        };
        var presets = new WrapPanel
        {
            Children =
            {
                new TextBlock { Text = "Across the keyboard:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) },
                Button("Gradient", "The chosen colour shading to the colour a third of the way round the wheel, left to right",
                    async () => await Preset((x, _) => Shade(Picked(), x * 120))),
                Button("Rainbow", "A rainbow from left to right", async () => await Preset((x, _) => Hue(x * 330, 1, 1))),
                Button("Rows", "Each row of keys its own colour", async () => await Preset((_, y) => Hue(y * 55, 1, 1))),
            },
        };
        Children.Add(selection);
        Children.Add(presets);
        Children.Add(_status);
        _ = FadeColoursAsync();
    }

    /// <summary>Colour changes ease in over 150 ms, once the desktop's own
    /// setting (animations on or off) has been read, off the UI thread.</summary>
    private async Task FadeColoursAsync()
    {
        if (!await AnimationsOn) return;
        foreach (var (cap, _) in _caps.Values)
            cap.Transitions = [new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(150), Easing = new CubicEaseOut() }];
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
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4) };
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
        if (_dragging) AddAt(e.GetPosition(_canvas));
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
            PaintFill(c.Key);
        }
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
                    else if (!_pending.ContainsKey(c.Key)) { _colours[c.Key] = _confirmed[c.Key]; PaintFill(c.Key); }
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
