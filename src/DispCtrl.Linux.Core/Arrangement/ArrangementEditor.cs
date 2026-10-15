using DispCtrl.Core.Arrangement;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Arrangement;

/// <summary>The arrangement being edited: positions staged over what X holds,
/// with undo, keyboard nudging, alignment guides and the rules for what may be
/// applied. Pure, so the window's drag and key handling can be checked without
/// a display.</summary>
/// <remarks>
/// The canvas only draws and forwards input; every decision about where a
/// display may go lives here, so a keyboard nudge and a drag cannot disagree
/// about what is legal.
/// </remarks>
public sealed class ArrangementEditor
{
    /// <summary>Which way an arrow key pushes.</summary>
    public enum Direction { Left, Right, Up, Down }

    /// <summary>A line to draw while dragging: a vertical one at <c>At</c> x,
    /// or a horizontal one at <c>At</c> y, spanning <c>From</c> to <c>To</c>
    /// along the other axis.</summary>
    public readonly record struct Guide(bool Vertical, double At, double From, double To);

    /// <summary>A rectangle in whatever space the caller draws in.</summary>
    public readonly record struct Box(string Token, double X, double Y, double Width, double Height);

    private readonly List<XRandROutput> _lit;
    private readonly Dictionary<string, (int X, int Y)> _live;
    private readonly Dictionary<string, (int X, int Y)> _staged;
    private readonly Stack<Dictionary<string, (int X, int Y)>> _undo = new();
    private string? _primary;

    public ArrangementEditor(IEnumerable<XRandROutput> outputs)
    {
        _lit = outputs.Where(o => o.Active).ToList();
        _live = _lit.ToDictionary(o => o.Name, o => (o.PosX, o.PosY), StringComparer.Ordinal);
        _staged = new Dictionary<string, (int X, int Y)>(_live, StringComparer.Ordinal);
        _primary = _lit.FirstOrDefault(o => o.Primary)?.Name;
    }

    public IReadOnlyDictionary<string, (int X, int Y)> Positions => _staged;

    /// <summary>The display to make main, when that was changed.</summary>
    public string? Primary => _primary;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether the staged desk differs from the live one, with both
    /// shifted to start at 0,0: X's desktop always does, and a layout that only
    /// moved as a whole is the same desk.</summary>
    public bool IsChanged => PrimaryChanged || PositionsChanged;

    public bool PositionsChanged
    {
        get
        {
            var staged = Normalise(_staged);
            var live = Normalise(_live);
            return staged.Any(p => live[p.Key] != p.Value);
        }
    }

    public bool PrimaryChanged => _primary != _lit.FirstOrDefault(o => o.Primary)?.Name;

    public List<ArrangementSolver.Panel> Panels() =>
        _lit.Select(o => new ArrangementSolver.Panel(o.Name, _staged[o.Name].X, _staged[o.Name].Y, o.WidthPx, o.HeightPx,
            o.Name == _primary)).ToList();

    /// <summary>Puts a display at a place already known legal (a drop on a
    /// slot), then slides back together any display it was holding to the
    /// desktop. Returns whether others had to move.</summary>
    public bool Drop(string token, int x, int y)
    {
        if (!_staged.ContainsKey(token) || _staged[token] == (x, y)) return false;
        Remember();
        _staged[token] = (x, y);
        return Close(token);
    }

    /// <summary>Moves a display to the nearest legal place in a direction, or
    /// says nothing is there. One press, one step to the next edge or alignment.</summary>
    public bool Nudge(string token, Direction direction)
    {
        if (!_staged.TryGetValue(token, out var at)) return false;
        ArrangementSlots.Slot? best = null;
        double nearest = double.MaxValue;
        foreach (var slot in ArrangementSlots.For(Panels(), token))
        {
            int dx = slot.X - at.X, dy = slot.Y - at.Y;
            bool ahead = direction switch
            {
                Direction.Left => dx < 0, Direction.Right => dx > 0, Direction.Up => dy < 0, _ => dy > 0,
            };
            if (!ahead) continue;
            // Prefer the step that stays most nearly on the pressed axis, then the shortest.
            double along = direction is Direction.Left or Direction.Right ? Math.Abs(dx) : Math.Abs(dy);
            double across = direction is Direction.Left or Direction.Right ? Math.Abs(dy) : Math.Abs(dx);
            double cost = across * 4 + along;
            if (cost >= nearest) continue;
            nearest = cost;
            best = slot;
        }
        if (best is not { } target) return false;
        Remember();
        _staged[token] = (target.X, target.Y);
        Close(token);
        return true;
    }

    /// <summary>Stages an exact position typed into the inspector; the reason
    /// it cannot be, or null when it was staged.</summary>
    public string? TrySet(string token, int x, int y)
    {
        var moves = new Dictionary<string, (int X, int Y)>(_staged, StringComparer.Ordinal) { [token] = (x, y) };
        var probe = _lit.Select(o => o with { PosX = moves[o.Name].X, PosY = moves[o.Name].Y }).ToList();
        if (DesktopLayout.Plan(probe, new Dictionary<string, (int, int)> { [token] = (x, y) }, out _) is { } why) return why;
        Remember();
        _staged[token] = (x, y);
        return null;
    }

    public void MakeMain(string token)
    {
        if (_primary == token || !_staged.ContainsKey(token)) return;
        Remember();
        _primary = token;
    }

    /// <summary>Side by side, tops aligned: the Reset button.</summary>
    public void Reset()
    {
        Remember();
        foreach (var (name, at) in DesktopLayout.SideBySide(_lit)) _staged[name] = at;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var before = _undo.Pop();
        _primary = _primaryUndo.Pop();
        _staged.Clear();
        foreach (var (name, at) in before) _staged[name] = at;
        return true;
    }

    /// <summary>Drops every staged change.</summary>
    public void Revert()
    {
        _undo.Clear();
        _primaryUndo.Clear();
        _staged.Clear();
        foreach (var (name, at) in _live) _staged[name] = at;
        _primary = _lit.FirstOrDefault(o => o.Primary)?.Name;
    }

    /// <summary>The arguments of one <c>display arrange</c> for the staged desk.</summary>
    public string[] ArrangeArguments() =>
        ["display", "arrange", .. _staged.Select(p => FormattableString.Invariant($"{p.Key}={p.Value.X},{p.Value.Y}"))];

    /// <summary>Lines that show where the moving box lines up with the others:
    /// shared left, centre or right edges, and top, middle or bottom edges.
    /// <paramref name="tolerance"/> is in the same space as the boxes.</summary>
    public static List<Guide> Guides(Box moving, IEnumerable<Box> others, double tolerance)
    {
        var guides = new List<Guide>();
        double[] xs = [moving.X, moving.X + moving.Width / 2, moving.X + moving.Width];
        double[] ys = [moving.Y, moving.Y + moving.Height / 2, moving.Y + moving.Height];
        foreach (var o in others)
        {
            if (o.Token == moving.Token) continue;
            double[] oxs = [o.X, o.X + o.Width / 2, o.X + o.Width];
            double[] oys = [o.Y, o.Y + o.Height / 2, o.Y + o.Height];
            foreach (double x in xs)
            foreach (double ox in oxs)
                if (Math.Abs(x - ox) <= tolerance)
                    guides.Add(new Guide(true, ox, Math.Min(moving.Y, o.Y), Math.Max(moving.Y + moving.Height, o.Y + o.Height)));
            foreach (double y in ys)
            foreach (double oy in oys)
                if (Math.Abs(y - oy) <= tolerance)
                    guides.Add(new Guide(false, oy, Math.Min(moving.X, o.X), Math.Max(moving.X + moving.Width, o.X + o.Width)));
        }
        return guides.Distinct().ToList();
    }

    /// <summary>Displays showing the same picture: lit at one place and size,
    /// as Duplicate leaves them. A drag cannot arrange these.</summary>
    public static List<List<string>> MirrorGroups(IEnumerable<XRandROutput> outputs) =>
        outputs.Where(o => o.Active).GroupBy(o => (o.PosX, o.PosY, o.WidthPx, o.HeightPx))
            .Where(g => g.Count() > 1).Select(g => g.Select(o => o.Name).ToList()).ToList();

    /// <summary>Connected but switched off: they have no place on the desktop.</summary>
    public static List<string> SwitchedOff(IEnumerable<XRandROutput> outputs) =>
        outputs.Where(o => o.Connected && !o.Active).Select(o => o.Name).ToList();

    /// <summary>What the turn looks like to a person.</summary>
    public static string Orientation(int rotation) => rotation switch
    {
        90 => "Portrait, turned left",
        180 => "Landscape, upside down",
        270 => "Portrait, turned right",
        _ => "Landscape",
    };

    /// <summary>Pixels per inch from the size the panel reports; null when it reports none.</summary>
    public static int? PixelsPerInch(XRandROutput o)
    {
        bool turned = o.Rotation is 90 or 270;
        int px = turned ? o.HeightPx : o.WidthPx, mm = turned ? o.HeightMm : o.WidthMm;
        return px > 0 && mm > 0 ? (int)Math.Round(px / (mm / 25.4)) : null;
    }

    private void Remember()
    {
        var snapshot = new Dictionary<string, (int X, int Y)>(_staged, StringComparer.Ordinal);
        _undo.Push(snapshot);
        _primaryUndo.Push(_primary);
    }

    private readonly Stack<string?> _primaryUndo = new();

    private bool Close(string moved)
    {
        var before = Panels();
        var closed = ArrangementSolver.Close(before);
        bool others = false;
        foreach (var p in closed)
        {
            if (p.Token != moved && _staged[p.Token] != (p.X, p.Y)) others = true;
            _staged[p.Token] = (p.X, p.Y);
        }
        return others;
    }

    private static Dictionary<string, (int X, int Y)> Normalise(IReadOnlyDictionary<string, (int X, int Y)> places)
    {
        if (places.Count == 0) return [];
        int left = places.Values.Min(p => p.X), top = places.Values.Min(p => p.Y);
        return places.ToDictionary(p => p.Key, p => (p.Value.X - left, p.Value.Y - top), StringComparer.Ordinal);
    }
}
