using DispCtrl.Core.Arrangement;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Arrangement;

/// <summary>Where each lit output sits on the X desktop, checked by the same
/// rules the Windows app applies before a layout is written.</summary>
/// <remarks>
/// X itself accepts gaps and overlaps, which Windows refuses; they are refused
/// here as well. A gap leaves the pointer unable to cross between two screens,
/// and an overlap mirrors part of one onto the other, and neither is what
/// someone dragging a picture of their desk meant. The geometry is
/// <c>DispCtrl.Core.Arrangement</c>'s, compiled into this assembly.
/// </remarks>
public static class DesktopLayout
{
    /// <summary>Each lit output as a panel of the arrangement solver.</summary>
    public static List<ArrangementSolver.Panel> Panels(IEnumerable<XRandROutput> outputs) =>
        outputs.Where(o => o.Active)
            .Select(o => new ArrangementSolver.Panel(o.Name, o.PosX, o.PosY, o.WidthPx, o.HeightPx, o.Primary))
            .ToList();

    /// <summary>The output as a panel of the physical-size diagram, at
    /// <paramref name="x"/>, <paramref name="y"/>.</summary>
    /// <param name="physical">False to draw it by resolution, as Windows'
    /// own Settings does.</param>
    /// <remarks>XRandR reports the panel's size unrotated, and the geometry
    /// rotated, so a portrait monitor swaps its millimetres.</remarks>
    public static PhysicalLayout.Panel Physical(XRandROutput output, int x, int y, bool physical)
    {
        bool rotated = output.Rotation is 90 or 270;
        double widthMm = rotated ? output.HeightMm : output.WidthMm;
        double heightMm = rotated ? output.WidthMm : output.HeightMm;
        bool sized = physical && widthMm > 0 && heightMm > 0;
        return new PhysicalLayout.Panel(output.Name, x, y, output.WidthPx, output.HeightPx,
            sized ? widthMm / output.WidthPx : 1, sized ? heightMm / output.HeightPx : 1);
    }

    /// <summary>Whether every lit output reports its size, so the diagram can
    /// be drawn in millimetres rather than pixels.</summary>
    public static bool AllSized(IEnumerable<XRandROutput> outputs) =>
        outputs.Where(o => o.Active).All(o => o.WidthMm > 0 && o.HeightMm > 0);

    /// <summary>
    /// The layout with <paramref name="moves"/> applied over
    /// <paramref name="outputs"/>' current positions, shifted so the desktop
    /// starts at 0,0; or why it cannot be applied.
    /// </summary>
    /// <remarks>
    /// X has no negative coordinates: its screen starts at 0,0 and every CRTC
    /// lies inside it. The Windows CLI takes positions relative to the main
    /// display, which may be negative; the same numbers are accepted here and
    /// shifted as a whole, which keeps every relationship.
    /// </remarks>
    public static string? Plan(IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, (int X, int Y)> moves,
        out Dictionary<string, (int X, int Y)> layout)
    {
        layout = [];
        var lit = outputs.Where(o => o.Active).ToList();
        foreach (var name in moves.Keys)
        {
            if (lit.Any(o => o.Name == name)) continue;
            return outputs.Any(o => o.Name == name && o.Connected)
                ? $"{name} is connected but switched off, so it has no place on the desktop"
                : $"no connected output '{name}' (connected: {string.Join(", ", lit.Select(o => o.Name))})";
        }

        var panels = lit.Select(o => new ArrangementSolver.Panel(o.Name,
            moves.TryGetValue(o.Name, out var at) ? at.X : o.PosX,
            moves.TryGetValue(o.Name, out at) ? at.Y : o.PosY,
            o.WidthPx, o.HeightPx, o.Primary)).ToList();

        for (int i = 0; i < panels.Count; i++)
        for (int j = i + 1; j < panels.Count; j++)
        {
            var (a, b) = (panels[i], panels[j]);
            if (a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom)
                return $"{a.Token} and {b.Token} would overlap";
        }
        if (!ArrangementSolver.IsValid(panels))
            return "every display must touch another along an edge, with no gap, so the pointer can cross between them";

        int left = panels.Count == 0 ? 0 : panels.Min(p => p.X);
        int top = panels.Count == 0 ? 0 : panels.Min(p => p.Y);
        foreach (var p in panels) layout[p.Token] = (p.X - left, p.Y - top);
        return null;
    }

    /// <summary>Side by side in today's left-to-right order, top edges
    /// aligned: the Reset button on Windows.</summary>
    public static Dictionary<string, (int X, int Y)> SideBySide(IReadOnlyList<XRandROutput> outputs)
    {
        var layout = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        int x = 0;
        foreach (var o in outputs.Where(o => o.Active).OrderBy(o => o.PosX).ThenBy(o => o.PosY))
        {
            layout[o.Name] = (x, 0);
            x += o.WidthPx;
        }
        return layout;
    }

    /// <summary>Plans, writes and reads back: true only when X holds every
    /// output where it was asked to.</summary>
    public static bool Apply(IReadOnlyDictionary<string, (int X, int Y)> moves, string? primary, out string? error)
    {
        var outputs = XRandR.Query();
        if (outputs.Count == 0) { error = "RandR reported no outputs"; return false; }
        if (primary is not null && !outputs.Any(o => o.Name == primary && o.Active))
        {
            error = $"no lit output '{primary}' to make the main display";
            return false;
        }
        if (Plan(outputs, moves, out var layout) is { } why) { error = why; return false; }

        // Only what changes is sent: an unchanged output left out of the call
        // is not touched at all.
        var changed = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var (name, at) in layout)
        {
            var output = outputs.First(o => o.Name == name);
            if ((output.PosX, output.PosY) != at) changed[name] = at;
        }
        string? newPrimary = primary is not null && !outputs.First(o => o.Name == primary).Primary ? primary : null;
        if (changed.Count == 0 && newPrimary is null) { error = null; return true; }
        if (!XRandR.SetPositions(changed, newPrimary, out error)) return false;

        var after = XRandR.Query();
        foreach (var (name, at) in layout)
        {
            if (after.FirstOrDefault(o => o.Name == name) is { } o && (o.PosX, o.PosY) == at) continue;
            error = $"X did not keep {name} at {at.X},{at.Y}";
            return false;
        }
        if (newPrimary is not null && after.FirstOrDefault(o => o.Name == newPrimary) is not { Primary: true })
        {
            error = $"X did not make {newPrimary} the main display";
            return false;
        }
        return true;
    }
}
