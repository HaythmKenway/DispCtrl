using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DispCtrl.Core.Arrangement;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Gui.Services;
using DispCtrl.Linux.Gui.ViewModels;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Gui.Views.Controls;

/// <summary>The desk as a picture: drag, nudge or type a display to where it
/// sits, with its settings beside it, then apply. The Windows app's
/// arrangement, on the same geometry.</summary>
/// <remarks>
/// Every decision about where a display may go is <see cref="ArrangementEditor"/>'s;
/// this control draws and forwards input. A drag follows the pointer exactly,
/// touching nothing but the dragged tile, an outline and a few guide lines, and
/// lands on the nearest legal place (<see cref="ArrangementSlots"/>) on release.
/// Nothing reaches X until Apply, which sends one <c>display arrange</c> and
/// then offers to keep it, putting the desk back after fifteen seconds if not.
/// </remarks>
public partial class ArrangeCanvas : UserControl
{
    private sealed class Tile
    {
        public required Border Element { get; init; }
        public required TextBlock Number { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Mode { get; init; }
        public required Border Badge { get; init; }
        public required Border UpEdge { get; init; }
        public required OutputViewModel View { get; init; }
        public string Token => View.Output.Name;
    }

    private const string RestingHintPhysical =
        "Drag a display, or choose one and press the arrow keys. Tiles are drawn at each display's real size; the thick edge is the top of the picture.";
    private const string RestingHintPixels =
        "Drag a display, or choose one and press the arrow keys. Tiles are drawn by resolution; the thick edge is the top of the picture.";
    private const double WideFrom = 860;

    private readonly List<Tile> _tiles = [];
    private readonly Dictionary<string, PhysicalLayout.Placed> _preview = new(StringComparer.Ordinal);
    private readonly List<Line> _guides = [];
    private IReadOnlyList<OutputViewModel> _outputs = [];
    private ArrangementEditor _editor = new([]);
    private double _scale = 1, _offsetX, _offsetY, _originX, _originY, _zoom = 1;
    private Tile? _dragging, _selected;
    private Point _grabOffset;
    private (int X, int Y) _grabbedAt;
    private string _loaded = "";
    private string? _note;
    private bool _mirrored, _syncing, _applying;

    /// <summary>Raised after an arrangement was applied, so the page reads
    /// the layout back.</summary>
    public event Action? Applied;

    public ArrangeCanvas()
    {
        InitializeComponent();
        Hint.Text = RestingHint;
        Surface.SizeChanged += (_, _) => { if (_dragging is null) Layout(); };
        SizeChanged += (_, e) => OnWidth(e.NewSize.Width);
        Surface.KeyDown += OnKeyDown;
        Surface.PointerWheelChanged += OnWheel;
        Surface.PointerPressed += (_, e) =>
        {
            // The empty background: nothing is chosen, and keys go to the surface.
            if (e.Source == Surface) { Select(null); Surface.Focus(); }
        };
        PosX.ValueChanged += (_, _) => OnPositionTyped();
        PosY.ValueChanged += (_, _) => OnPositionTyped();
        ShowInspector();
    }

    private string RestingHint => ByResolution ? RestingHintPixels : RestingHintPhysical;

    private bool ByResolution =>
        ResolutionToggle.IsChecked == true || !DesktopLayout.AllSized(_outputs.Select(o => o.Output));

    /// <summary>Takes the live layout. The same layout again keeps whatever
    /// is staged; a changed one drops it, since it was staged against
    /// positions that have moved.</summary>
    public void Load(IReadOnlyList<OutputViewModel> outputs, IReadOnlyList<XRandROutput> all, bool scanning)
    {
        if (_dragging is not null || _applying) return;
        UpdateNotes(outputs, all, scanning);
        string loaded = string.Join(";", outputs.Select(o =>
            $"{o.Number}:{o.Label}:{o.Output with { RefreshHz = 0 }}"));
        if (loaded == _loaded) return;
        _loaded = loaded;
        string? keep = _selected?.Token;
        _outputs = outputs.ToList();
        _editor = new ArrangementEditor(_outputs.Select(o => o.Output));
        _mirrored = ArrangementEditor.MirrorGroups(_outputs.Select(o => o.Output)).Count > 0;
        ResolutionToggle.IsEnabled = DesktopLayout.AllSized(_outputs.Select(o => o.Output));
        Build();
        Select(_tiles.FirstOrDefault(t => t.Token == keep) ?? (_tiles.Count == 1 ? _tiles[0] : null), focus: false);
        Stage(_note ?? RestingHintFor(_outputs.Count));
        _note = null;
    }

    private string RestingHintFor(int count) => count switch
    {
        0 => "No display is lit on this X screen.",
        1 => "One display: there is nothing to arrange it against. Its settings are on the right.",
        _ => _mirrored ? "These displays show the same picture, so there is no layout to arrange." : RestingHint,
    };

    private void UpdateNotes(IReadOnlyList<OutputViewModel> outputs, IReadOnlyList<XRandROutput> all, bool scanning)
    {
        bool empty = outputs.Count == 0;
        Placeholder.IsVisible = empty;
        PlaceholderRing.IsVisible = scanning;
        PlaceholderText.Text = scanning ? "Looking for displays…"
            : "No display is lit on this X screen. Connect one, or choose Extend under Display mode.";

        var mirrors = ArrangementEditor.MirrorGroups(all);
        var off = ArrangementEditor.SwitchedOff(all);
        var lines = new List<string>();
        if (mirrors.Count > 0)
            lines.Add($"{string.Join(" and ", mirrors.Select(g => string.Join(" + ", g)))} mirror each other. Choose Extend under Display mode to arrange them separately.");
        if (off.Count > 0)
            lines.Add($"{string.Join(", ", off)} {(off.Count == 1 ? "is" : "are")} connected but switched off, so {(off.Count == 1 ? "it has" : "they have")} no place on the desktop. Extend lights {(off.Count == 1 ? "it" : "them")}.");
        Note.Text = string.Join(" ", lines);
        NoteBox.IsVisible = lines.Count > 0;
    }

    private void OnWidth(double width)
    {
        bool wide = width >= WideFrom;
        Grid.SetRow(Inspector, wide ? 0 : 1);
        Grid.SetColumn(Inspector, wide ? 1 : 0);
        Grid.SetColumnSpan(Inspector, wide ? 1 : 2);
        Grid.SetColumnSpan(CanvasBox, wide ? 1 : 2);
        Inspector.Width = wide ? 300 : double.NaN;
        Inspector.Margin = wide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 12, 0, 0);
        InspectorScroll.MaxHeight = wide ? double.PositiveInfinity : 560;
        ResolutionLabel.IsVisible = width >= 520;
        double canvasWidth = wide ? width - 312 : width;
        Surface.Height = Math.Clamp(canvasWidth * 0.42, 200, 420);
    }

    // Where the dragged display will land, outlined while the display itself
    // follows the pointer.
    private readonly Border _ghost = new() { Classes = { "ghost" }, IsVisible = false, IsHitTestVisible = false };
    private (int X, int Y, PhysicalLayout.Placed Landing)? _pending;

    private void Build()
    {
        Surface.Children.Clear();
        _tiles.Clear();
        _selected = null;
        _guides.Clear();
        Surface.Children.Add(_ghost);
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
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0),
            };
            var mode = new TextBlock
            {
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0),
                Opacity = 0.8,
            };
            var badge = new Border
            {
                Classes = { "chip" },
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Classes = { "badge" }, Text = "Main" },
            };
            var edge = new Border { Classes = { "upedge" }, CornerRadius = new CornerRadius(2) };
            var border = new Border
            {
                ClipToBounds = true,
                Child = new Grid
                {
                    Children =
                    {
                        edge,
                        new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { number, name, mode } },
                        badge,
                    },
                },
            };
            border.Classes.Add("devicetile");
            border.Classes.Set("primary", view.IsPrimary);
            border.Classes.Set("mirrored", _mirrored);
            border.Focusable = true;
            ToolTip.SetTip(border, $"{view.Label} ({view.Output.Name}), {view.ModeText}{(view.IsPrimary ? ", the main display" : "")}");
            AutomationProperties.SetName(border,
                $"Display {view.Number}, {view.Label}, {view.ModeText}, {ArrangementEditor.Orientation(view.Output.Rotation).ToLowerInvariant()}{(view.IsPrimary ? ", main display" : "")}");
            var tile = new Tile { Element = border, Number = number, Name = name, Mode = mode, Badge = badge, UpEdge = edge, View = view };
            border.Tag = tile;
            border.PointerPressed += OnPointerPressed;
            border.PointerMoved += OnPointerMoved;
            border.PointerReleased += OnPointerReleased;
            border.PointerCaptureLost += OnPointerCaptureLost;
            border.GotFocus += (_, _) => { if (_selected != tile) Select(tile, focus: false); };
            view.PropertyChanged += OnViewChanged;
            Surface.Children.Add(border);
            _tiles.Add(tile);
        }
        UpdatePreview();
        Layout();
    }

    /// <summary>Fits the whole desk into the surface, centred, then applies
    /// the zoom around the chosen display.</summary>
    private void Layout()
    {
        if (_tiles.Count == 0 || Surface.Bounds.Width <= 0 || Surface.Bounds.Height <= 0) return;
        double minX = _preview.Values.Min(t => t.X), minY = _preview.Values.Min(t => t.Y);
        double maxX = _preview.Values.Max(t => t.X + t.Width), maxY = _preview.Values.Max(t => t.Y + t.Height);
        double fit = Math.Min(Surface.Bounds.Width / Math.Max(1, maxX - minX),
                              Surface.Bounds.Height / Math.Max(1, maxY - minY)) * 0.86;
        _scale = fit * _zoom;
        _originX = minX;
        _originY = minY;
        double centreX = (minX + maxX) / 2, centreY = (minY + maxY) / 2;
        if (_zoom != 1 && _selected is { } s && _preview.TryGetValue(s.Token, out var at))
            (centreX, centreY) = (at.X + at.Width / 2, at.Y + at.Height / 2);
        _offsetX = Surface.Bounds.Width / 2 - (centreX - minX) * _scale;
        _offsetY = Surface.Bounds.Height / 2 - (centreY - minY) * _scale;
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
            double smaller = Math.Min(width, height);
            tile.Number.FontSize = Math.Clamp(smaller * 0.3, 13, 40);
            tile.Name.IsVisible = height > 70 && width > 90;
            tile.Mode.IsVisible = height > 100 && width > 120;
            tile.Badge.IsVisible = tile.View.IsPrimary && width > 70 && height > 50;
            PlaceUpEdge(tile);
            Canvas.SetLeft(tile.Element, _offsetX + (at.X - _originX) * _scale + 1);
            Canvas.SetTop(tile.Element, _offsetY + (at.Y - _originY) * _scale + 1);
        }
    }

    /// <summary>A thick bar on the side that is the top of the picture, so a
    /// turned display reads as turned and a flipped one as flipped.</summary>
    private static void PlaceUpEdge(Tile tile)
    {
        var edge = tile.UpEdge;
        const double thickness = 5;
        (edge.HorizontalAlignment, edge.VerticalAlignment, edge.Width, edge.Height) = tile.View.Output.Rotation switch
        {
            90 => (HorizontalAlignment.Left, VerticalAlignment.Stretch, thickness, double.NaN),
            180 => (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, thickness),
            270 => (HorizontalAlignment.Right, VerticalAlignment.Stretch, thickness, double.NaN),
            _ => (HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, thickness),
        };
        tile.Mode.Text = tile.View.Output.Rotation == 0
            ? tile.View.ModeText
            : $"{tile.View.ModeText} · {ArrangementEditor.Orientation(tile.View.Output.Rotation).ToLowerInvariant()}";
    }

    private PhysicalLayout.Panel PreviewPanel(Tile tile)
    {
        var at = _editor.Positions[tile.Token];
        return DesktopLayout.Physical(tile.View.Output, at.X, at.Y, physical: !ByResolution);
    }

    private void UpdatePreview()
    {
        _preview.Clear();
        // Mirrored displays share one place; the diagram's solver expects each
        // to touch the next, so they are drawn where they are.
        if (_mirrored)
        {
            foreach (var t in _tiles)
            {
                var o = t.View.Output;
                _preview[t.Token] = new PhysicalLayout.Placed(t.Token, o.PosX, o.PosY, o.WidthPx, o.HeightPx);
            }
            return;
        }
        foreach (var at in PhysicalLayout.Resolve(_tiles.Select(PreviewPanel).ToList()))
            _preview[at.Token] = at;
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selected?.View == sender) UpdateInspectorNotes();
    }

    // ------------------------------------------------------------- drag --

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: Tile tile } border) return;
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        Select(tile);
        if (_tiles.Count < 2 || _mirrored) return;
        _dragging = tile;
        _pending = null;
        _grabbedAt = _editor.Positions[tile.Token];
        var pointer = e.GetPosition(Surface);
        _grabOffset = new Point(pointer.X - Canvas.GetLeft(border), pointer.Y - Canvas.GetTop(border));
        e.Pointer.Capture(border);
        border.ZIndex = 10;
        border.Classes.Add("dragging");
        e.Handled = true;
    }

    /// <summary>
    /// The display follows the pointer exactly, as Windows' Settings draws it,
    /// and an outline shows the nearest place it may land, with guide lines
    /// where it lines up with the others; it lands there on release. The work
    /// per move is a few rectangles: measured at about 0.02 ms on six displays.
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || sender is not Border { Tag: Tile tile } border || tile != _dragging) return;
        var pointer = e.GetPosition(Surface);
        double left = pointer.X - _grabOffset.X, top = pointer.Y - _grabOffset.Y;
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        double x = (left - _offsetX) / _scale + _originX;
        double y = (top - _offsetY) / _scale + _originY;

        // The nearest legal place to where it is drawn, measured in the
        // picture, so it lands where it was dropped rather than where its
        // pixels would be.
        var moving = PreviewPanel(_dragging);
        double nearest = double.MaxValue;
        ArrangementSlots.Slot? best = null;
        PhysicalLayout.Placed landing = default;
        foreach (var slot in ArrangementSlots.For(_editor.Panels(), _dragging.Token))
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
            _pending = (target.X, target.Y, landing);
            _ghost.Width = Math.Max(0, landing.Width * _scale - 2);
            _ghost.Height = Math.Max(0, landing.Height * _scale - 2);
            Canvas.SetLeft(_ghost, ToScreenX(landing.X) + 1);
            Canvas.SetTop(_ghost, ToScreenY(landing.Y) + 1);
            _ghost.IsVisible = true;
            ShowGuides(landing);
        }
        e.Handled = true;
    }

    private double ToScreenX(double x) => _offsetX + (x - _originX) * _scale;
    private double ToScreenY(double y) => _offsetY + (y - _originY) * _scale;

    private void ShowGuides(PhysicalLayout.Placed landing)
    {
        var guides = ArrangementEditor.Guides(new(landing.Token, landing.X, landing.Y, landing.Width, landing.Height),
            _preview.Values.Select(p => new ArrangementEditor.Box(p.Token, p.X, p.Y, p.Width, p.Height)), 0.5);
        for (int i = 0; i < guides.Count; i++)
        {
            if (i == _guides.Count)
            {
                var line = new Line { Classes = { "guide" } };
                _guides.Add(line);
                Surface.Children.Add(line);
            }
            var (g, line2) = (guides[i], _guides[i]);
            line2.IsVisible = true;
            if (g.Vertical)
            {
                line2.StartPoint = new Point(ToScreenX(g.At), ToScreenY(g.From) - 8);
                line2.EndPoint = new Point(ToScreenX(g.At), ToScreenY(g.To) + 8);
            }
            else
            {
                line2.StartPoint = new Point(ToScreenX(g.From) - 8, ToScreenY(g.At));
                line2.EndPoint = new Point(ToScreenX(g.To) + 8, ToScreenY(g.At));
            }
        }
        for (int i = guides.Count; i < _guides.Count; i++) _guides[i].IsVisible = false;
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
        _ghost.IsVisible = false;
        foreach (var guide in _guides) guide.IsVisible = false;
        bool rejoined = false;
        if (_pending is { } landed)
        {
            rejoined = _editor.Drop(moved.Token, landed.X, landed.Y);
            _pending = null;
        }
        if (_editor.Positions[moved.Token] == _grabbedAt) { Layout(); return; }
        AfterMove(rejoined
            ? "Lined up, and the displays it left on their own were moved back beside the others. Not applied yet."
            : "Lined up with the nearest display. Not applied yet.");
    }

    private void AfterMove(string hint)
    {
        UpdatePreview();
        Layout();
        SyncPosition();
        Stage(hint);
    }

    /// <summary>Enables what the staged desk allows and says where it stands.</summary>
    private void Stage(string hint)
    {
        bool changed = _editor.IsChanged;
        ApplyButton.IsEnabled = changed && !_applying;
        RevertButton.IsEnabled = changed && !_applying;
        UndoButton.IsEnabled = _editor.CanUndo && !_applying;
        ResetButton.IsEnabled = _tiles.Count > 1 && !_mirrored && !_applying;
        foreach (var tile in _tiles) tile.Element.Classes.Set("primary", tile.Token == _editor.Primary);
        Hint.Text = changed || _editor.CanUndo || hint != "That is how the displays are arranged now." ? hint : RestingHintFor(_tiles.Count);
        if (!changed && _editor.CanUndo) Hint.Text = "That is how the displays are arranged now.";
        UpdateMain();
    }

    // -------------------------------------------------------- selection --

    private void Select(Tile? tile, bool focus = true)
    {
        if (_selected == tile) return;
        _selected?.Element.Classes.Remove("selected");
        _selected = tile;
        tile?.Element.Classes.Add("selected");
        if (focus) tile?.Element.Focus();
        ShowInspector();
        if (_zoom != 1) Layout();
    }

    private void ShowInspector()
    {
        var view = _selected?.View;
        InspectorBody.DataContext = view;
        InspectorEmpty.IsVisible = view is null;
        InspectorEmptyText.Text = _tiles.Count == 0 ? "No display is lit, so there is nothing to show here." : "Choose a display to see and change its settings.";
        InspectorScroll.IsVisible = view is not null;
        SyncPosition();
        UpdateInspectorNotes();
        UpdateMain();
    }

    private void UpdateInspectorNotes()
    {
        var view = _selected?.View;
        BrightnessNone.IsVisible = view is { HasBrightness: false };
        bool ramps = GammaRamp.IsAvailable(out _);
        DimNone.IsVisible = view is { HasDim: false };
        DimNone.Text = ramps ? "No gamma ramp answers for this display, so it cannot be dimmed in software."
            : "Dimming needs gamma ramps, which this session does not offer.";
    }

    private void UpdateMain()
    {
        if (_selected is not { } s) return;
        bool main = s.Token == _editor.Primary;
        MakeMainButton.IsVisible = !main && _tiles.Count > 1;
        MainText.Text = main ? "This is your main display." : _tiles.Count > 1 ? "" : "The only display, so the main one.";
    }

    private void SyncPosition()
    {
        _syncing = true;
        try
        {
            bool has = _selected is not null;
            PosX.IsEnabled = PosY.IsEnabled = has && _tiles.Count > 1 && !_mirrored;
            if (_selected is { } s && _editor.Positions.TryGetValue(s.Token, out var at))
            {
                PosX.Value = at.X;
                PosY.Value = at.Y;
            }
            PositionStatus.IsVisible = false;
        }
        finally { _syncing = false; }
    }

    private void OnPositionTyped()
    {
        if (_syncing || _selected is not { } s || PosX.Value is null || PosY.Value is null) return;
        var error = _editor.TrySet(s.Token, (int)PosX.Value, (int)PosY.Value);
        if (error is not null)
        {
            PositionStatus.Text = $"Not possible: {error}.";
            PositionStatus.IsVisible = true;
            return;
        }
        AfterMove("Moved to the position you typed. Not applied yet.");
    }

    private void OnMakeMain(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } s) return;
        _editor.MakeMain(s.Token);
        Stage($"{s.View.Label} will become the main display. Not applied yet.");
    }

    private void OnOpenBrightness(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.Show("brightness");

    // --------------------------------------------------------- keyboard --

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { OnUndo(null, new RoutedEventArgs()); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: SetZoom(_zoom * 1.25); e.Handled = true; return;
            case Key.OemMinus or Key.Subtract: SetZoom(_zoom / 1.25); e.Handled = true; return;
            case Key.D0 or Key.NumPad0: OnFit(null, new RoutedEventArgs()); e.Handled = true; return;
        }
        var direction = e.Key switch
        {
            Key.Left => ArrangementEditor.Direction.Left,
            Key.Right => ArrangementEditor.Direction.Right,
            Key.Up => ArrangementEditor.Direction.Up,
            Key.Down => ArrangementEditor.Direction.Down,
            _ => (ArrangementEditor.Direction?)null,
        };
        if (direction is not { } d || _selected is not { } s || _tiles.Count < 2 || _mirrored) return;
        e.Handled = true;
        if (_editor.Nudge(s.Token, d)) AfterMove("Moved to the next place along that edge. Not applied yet.");
        else Hint.Text = "There is no place further that way: displays must touch along an edge.";
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.5, 3);
        if (Math.Abs(_zoom - 1) < 0.05) _zoom = 1;
        if (_dragging is null) Layout();
    }

    // ---------------------------------------------------------- buttons --

    private void OnFit(object? sender, RoutedEventArgs e) => SetZoom(1);
    private void OnZoomIn(object? sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);

    private void OnResolutionToggled(object? sender, RoutedEventArgs e)
    {
        if (_dragging is not null || _tiles.Count == 0) return;
        UpdatePreview();
        Layout();
        if (!_editor.IsChanged) Hint.Text = RestingHint;
    }

    private void OnIdentify(object? sender, RoutedEventArgs e) =>
        DisplayIdentifier.Show(_outputs, TimeSpan.FromSeconds(3));

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (!_editor.Undo()) return;
        AfterMove("Undone.");
    }

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        _editor.Revert();
        AfterMove("Back to how the displays are arranged now.");
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _editor.Reset();
        AfterMove("Side by side, top edges aligned. Select Apply to use it.");
        if (!_editor.IsChanged) Hint.Text = "The displays are already side by side.";
    }

    /// <summary>Applies the main display, then the positions with the keep-or-go-back
    /// countdown. Both run off the UI thread; the desk is only read back after.</summary>
    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        _applying = true;
        Surface.IsHitTestVisible = false;
        Stage("Applying…");
        string? failure = null;
        if (_editor.PrimaryChanged && _editor.Primary is { } main)
        {
            var reply = await CommandSender.RunAsync(["display", "set", "--output", main, "--primary", "true"]);
            if (reply.ExitCode != 0) failure = CommandSender.Reason(reply);
        }
        bool kept = true;
        if (failure is null && _editor.PositionsChanged)
            (kept, failure) = await ModeChange.RunAsync(_editor.ArrangeArguments());
        _applying = false;
        Surface.IsHitTestVisible = true;
        if (failure is null)
        {
            _note = kept ? "Arrangement applied and kept." : null;
            _loaded = "";
            Applied?.Invoke();
            return;
        }
        Stage($"Not applied: {failure}");
        Hint.Text = failure.EndsWith('.') ? failure : $"{failure}.";
        if (!kept) Applied?.Invoke();
    }
}
