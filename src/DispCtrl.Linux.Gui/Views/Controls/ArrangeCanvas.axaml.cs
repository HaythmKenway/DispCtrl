using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DispCtrl.Core.Arrangement;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.ViewModels;

namespace DispCtrl.Linux.Gui.Views.Controls;

/// <summary>The desk as a picture: drag a display to where it sits, then
/// apply. The Windows app's arrangement, on the same geometry.</summary>
/// <remarks>
/// A drag only ever offers legal places (<see cref="ArrangementSlots"/>): each
/// side of each other display, at each end and the centre. Dropping closes any
/// gap it left (<see cref="ArrangementSolver.Close"/>), and nothing reaches X
/// until Apply, which sends one <c>display arrange</c> for the whole layout.
/// </remarks>
public partial class ArrangeCanvas : UserControl
{
    private sealed class Tile
    {
        public required Border Element { get; init; }
        public required TextBlock Number { get; init; }
        public required TextBlock Name { get; init; }
        public required OutputViewModel View { get; init; }
        public int X { get; set; }
        public int Y { get; set; }
        public string Token => View.Output.Name;
        public int Width => View.Output.WidthPx;
        public int Height => View.Output.HeightPx;
    }

    private const string RestingHintPhysical =
        "Drag a display to match where it sits; nearby edges and centres line up. Tiles are drawn at each display's real size.";
    private const string RestingHintPixels =
        "Drag a display to match where it sits; nearby edges and centres line up. Tiles are drawn by resolution.";

    private readonly List<Tile> _tiles = [];
    private readonly Dictionary<string, PhysicalLayout.Placed> _preview = new(StringComparer.Ordinal);
    private IReadOnlyList<OutputViewModel> _outputs = [];
    private double _scale = 1, _offsetX, _offsetY, _originX, _originY;
    private Tile? _dragging;
    private Point _grabOffset;
    private (int X, int Y) _grabbedAt;
    private bool _staged;
    private string _loaded = "";
    private string? _note;

    /// <summary>Raised after an arrangement was applied, so the page reads
    /// the layout back.</summary>
    public event Action? Applied;

    public ArrangeCanvas()
    {
        InitializeComponent();
        Hint.Text = RestingHint;
        Surface.SizeChanged += (_, _) => { if (_dragging is null) Layout(); };
        SizeChanged += (_, e) => OnWidth(e.NewSize.Width);
    }

    private string RestingHint => ByResolution ? RestingHintPixels : RestingHintPhysical;

    private bool ByResolution =>
        ResolutionToggle.IsChecked == true || !DesktopLayout.AllSized(_outputs.Select(o => o.Output));

    /// <summary>Takes the live layout. The same layout again keeps whatever
    /// is staged; a changed one drops it, since it was staged against
    /// positions that have moved.</summary>
    public void Load(IReadOnlyList<OutputViewModel> outputs)
    {
        if (_dragging is not null) return;
        string loaded = string.Join(";", outputs.Select(o =>
            $"{o.Number}:{o.Label}:{o.Output with { RefreshHz = 0 }}"));
        if (loaded == _loaded) return;
        _loaded = loaded;
        _outputs = outputs.ToList();
        _staged = false;
        ApplyButton.IsEnabled = false;
        ResetButton.IsEnabled = _outputs.Count > 1;
        ResolutionToggle.IsEnabled = DesktopLayout.AllSized(_outputs.Select(o => o.Output));
        Hint.Text = _outputs.Count switch
        {
            0 => "No display is lit on this X screen.",
            1 => "One display: there is nothing to arrange it against.",
            _ => _note ?? RestingHint,
        };
        _note = null;
        Build();
    }

    private void OnWidth(double width)
    {
        // The buttons go under the hint before the hint is squeezed to a column.
        bool narrow = width < 600;
        Grid.SetRow(Buttons, narrow ? 1 : 0);
        Grid.SetColumn(Buttons, narrow ? 0 : 1);
        Grid.SetColumnSpan(Buttons, narrow ? 2 : 1);
        Buttons.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Buttons.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
        Surface.Height = Math.Clamp(width * 0.38, 180, 400);
    }

    private void Build()
    {
        Surface.Children.Clear();
        _tiles.Clear();
        foreach (var view in _outputs)
        {
            var number = new TextBlock
            {
                Text = view.Number.ToString(CultureInfo.InvariantCulture),
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var name = new TextBlock
            {
                Text = view.Label,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0),
                Opacity = 0.85,
            };
            var border = new Border
            {
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { number, name },
                },
            };
            border.Classes.Add("tile");
            border.Classes.Set("primary", view.IsPrimary);
            ToolTip.SetTip(border, $"{view.Label} ({view.Output.Name}){(view.IsPrimary ? ", the main display" : "")}");
            AutomationProperties.SetName(border, $"Display {view.Number}, {view.Label}");
            var tile = new Tile
            {
                Element = border, Number = number, Name = name, View = view,
                X = view.Output.PosX, Y = view.Output.PosY,
            };
            border.Tag = tile;
            border.PointerPressed += OnPointerPressed;
            border.PointerMoved += OnPointerMoved;
            border.PointerReleased += OnPointerReleased;
            border.PointerCaptureLost += OnPointerCaptureLost;
            Surface.Children.Add(border);
            _tiles.Add(tile);
        }
        UpdatePreview();
        Layout();
    }

    /// <summary>Fits the whole desk into the surface, centred.</summary>
    private void Layout()
    {
        if (_tiles.Count == 0 || Surface.Bounds.Width <= 0 || Surface.Bounds.Height <= 0) return;
        double minX = _preview.Values.Min(t => t.X), minY = _preview.Values.Min(t => t.Y);
        double maxX = _preview.Values.Max(t => t.X + t.Width), maxY = _preview.Values.Max(t => t.Y + t.Height);
        _scale = Math.Min(Surface.Bounds.Width / Math.Max(1, maxX - minX),
                          Surface.Bounds.Height / Math.Max(1, maxY - minY)) * 0.86;
        _originX = minX;
        _originY = minY;
        _offsetX = (Surface.Bounds.Width - (maxX - minX) * _scale) / 2;
        _offsetY = (Surface.Bounds.Height - (maxY - minY) * _scale) / 2;
        PlaceTiles();
    }

    private void PlaceTiles()
    {
        foreach (var tile in _tiles)
        {
            var at = _preview[tile.Token];
            double width = at.Width * _scale, height = at.Height * _scale;
            // A hair inside its slot, so two displays that touch read as two.
            tile.Element.Width = Math.Max(0, width - 2);
            tile.Element.Height = Math.Max(0, height - 2);
            tile.Number.FontSize = Math.Clamp(Math.Min(width, height) * 0.3, 13, 40);
            tile.Name.IsVisible = height > 64 && width > 80;
            Canvas.SetLeft(tile.Element, _offsetX + (at.X - _originX) * _scale + 1);
            Canvas.SetTop(tile.Element, _offsetY + (at.Y - _originY) * _scale + 1);
        }
    }

    private PhysicalLayout.Panel PreviewPanel(Tile tile) =>
        DesktopLayout.Physical(tile.View.Output, tile.X, tile.Y, physical: !ByResolution);

    private void UpdatePreview()
    {
        _preview.Clear();
        foreach (var at in PhysicalLayout.Resolve(_tiles.Select(PreviewPanel).ToList()))
            _preview[at.Token] = at;
    }

    private List<ArrangementSolver.Panel> Panels() =>
        _tiles.Select(t => new ArrangementSolver.Panel(t.Token, t.X, t.Y, t.Width, t.Height, t.View.IsPrimary)).ToList();

    // ------------------------------------------------------------- drag --

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Tile tile } border || _tiles.Count < 2) return;
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        _dragging = tile;
        _grabbedAt = (tile.X, tile.Y);
        var pointer = e.GetPosition(Surface);
        _grabOffset = new Point(pointer.X - Canvas.GetLeft(border), pointer.Y - Canvas.GetTop(border));
        e.Pointer.Capture(border);
        border.ZIndex = 10;
        border.Classes.Add("dragging");
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || sender is not Border { Tag: Tile tile } || tile != _dragging) return;
        var pointer = e.GetPosition(Surface);
        double x = (pointer.X - _grabOffset.X - _offsetX) / _scale + _originX;
        double y = (pointer.Y - _grabOffset.Y - _offsetY) / _scale + _originY;

        // The nearest legal place to the pointer, measured in the picture, so
        // it lands where it is drawn rather than where its pixels would be.
        var moving = PreviewPanel(_dragging);
        double nearest = double.MaxValue;
        ArrangementSlots.Slot? best = null;
        PhysicalLayout.Placed landing = default;
        foreach (var slot in ArrangementSlots.For(Panels(), _dragging.Token))
        {
            var other = _tiles.First(t => t.Token == slot.Against);
            var at = PhysicalLayout.Hang(PreviewPanel(other), moving with { X = slot.X, Y = slot.Y }, _preview[slot.Against]);
            if (_tiles.Any(t => t != _dragging && Intersects(at, _preview[t.Token]))) continue;
            double distance = Math.Pow(at.X - x, 2) + Math.Pow(at.Y - y, 2);
            if (distance >= nearest) continue;
            nearest = distance;
            best = slot;
            landing = at;
        }
        if (best is { } target)
        {
            _dragging.X = target.X;
            _dragging.Y = target.Y;
            _preview[_dragging.Token] = landing;
            PlaceTiles();
        }
        e.Handled = true;
    }

    private static bool Intersects(PhysicalLayout.Placed a, PhysicalLayout.Placed b) =>
        a.X < b.X + b.Width - 0.001 && b.X < a.X + a.Width - 0.001 &&
        a.Y < b.Y + b.Height - 0.001 && b.Y < a.Y + a.Height - 0.001;

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border) Commit(border);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Border border) Commit(border);
    }

    private void Commit(Border border)
    {
        if (_dragging is null || border.Tag != _dragging) return;
        var moved = _dragging;
        _dragging = null;
        border.ZIndex = 0;
        border.Classes.Remove("dragging");
        if ((moved.X, moved.Y) == _grabbedAt) { Layout(); return; }

        // With three or more displays the one dropped may have been holding
        // others to the desktop; slide them back together, as Windows does.
        var closed = ArrangementSolver.Close(Panels());
        var movedAfter = closed.First(p => p.Token == moved.Token);
        bool rejoined = false;
        foreach (var panel in closed)
        {
            var tile = _tiles.First(t => t.Token == panel.Token);
            rejoined |= tile != moved && (tile.X - panel.X, tile.Y - panel.Y) != (moved.X - movedAfter.X, moved.Y - movedAfter.Y);
        }
        foreach (var panel in closed)
        {
            var tile = _tiles.First(t => t.Token == panel.Token);
            tile.X = panel.X;
            tile.Y = panel.Y;
        }
        UpdatePreview();
        Layout();
        Stage(rejoined
            ? "Lined up, and the displays it left on their own were moved back beside the others. Not applied yet."
            : "Lined up with the nearest display. Not applied yet.");
    }

    /// <summary>Whether the staged layout differs from the live one, compared
    /// with both shifted to start at 0,0: X's desktop always does, and a
    /// layout that only moved as a whole is the same desk.</summary>
    private void Stage(string hint)
    {
        var staged = Normalised(_tiles.Select(t => (t.Token, t.X, t.Y)));
        var live = Normalised(_tiles.Select(t => (t.Token, t.View.Output.PosX, t.View.Output.PosY)));
        _staged = staged.Any(p => live[p.Key] != p.Value);
        ApplyButton.IsEnabled = _staged;
        Hint.Text = _staged ? hint : "That is how the displays are arranged now.";
    }

    private static Dictionary<string, (int X, int Y)> Normalised(IEnumerable<(string Token, int X, int Y)> places)
    {
        var list = places.ToList();
        int left = list.Min(p => p.X), top = list.Min(p => p.Y);
        return list.ToDictionary(p => p.Token, p => (p.X - left, p.Y - top), StringComparer.Ordinal);
    }

    // ---------------------------------------------------------- buttons --

    private void OnResolutionToggled(object? sender, RoutedEventArgs e)
    {
        if (!_staged) Hint.Text = RestingHint;
        if (_dragging is not null || _tiles.Count == 0) return;
        UpdatePreview();
        Layout();
    }

    private void OnIdentify(object? sender, RoutedEventArgs e) =>
        DisplayIdentifier.Show(_outputs, TimeSpan.FromSeconds(3));

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        var layout = DesktopLayout.SideBySide(_outputs.Select(o => o.Output).ToList());
        foreach (var tile in _tiles) (tile.X, tile.Y) = layout[tile.Token];
        UpdatePreview();
        Layout();
        Stage("Side by side, top edges aligned. Select Apply to use it.");
        if (!_staged) Hint.Text = "The displays are already side by side.";
    }

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        ResetButton.IsEnabled = false;
        Surface.IsHitTestVisible = false;
        Hint.Text = "Applying…";
        string[] args = ["display", "arrange", .. _tiles.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Token}={t.X},{t.Y}"))];
        var reply = await CommandSender.RunAsync(args);
        ResetButton.IsEnabled = true;
        Surface.IsHitTestVisible = true;
        if (reply.ExitCode == 0)
        {
            _staged = false;
            _note = "Arrangement applied.";
            Hint.Text = _note;
            Applied?.Invoke();
            return;
        }
        ApplyButton.IsEnabled = true;
        Hint.Text = $"Not applied: {CommandSender.Reason(reply)}.";
    }
}
