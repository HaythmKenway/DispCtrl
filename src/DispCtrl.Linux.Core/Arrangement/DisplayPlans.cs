using DispCtrl.Core.Arrangement;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Arrangement;

/// <summary>Win+P's arrangements and a change of mode or turn, as plans for
/// <see cref="XRandR.Configure"/>: pure, so the checks can try them without
/// an X server.</summary>
public static class DisplayPlans
{
    /// <summary>The Win+P names the command line takes.</summary>
    public static readonly IReadOnlyList<string> Projections = ["extend", "duplicate", "internal", "external"];

    /// <summary>A laptop's own panel, by the connector types only a built-in
    /// panel uses.</summary>
    public static bool IsBuiltIn(string output) =>
        output.StartsWith("eDP", StringComparison.Ordinal)
        || output.StartsWith("LVDS", StringComparison.Ordinal)
        || output.StartsWith("DSI", StringComparison.Ordinal);

    /// <summary>Every connected output lit: those lit now stay where they are,
    /// those that were off join on the right in their preferred mode.</summary>
    public static List<OutputConfig> Extend(IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, List<XRandRMode>> modes) =>
        SideBySide(outputs.Where(o => o.Connected), outputs, modes);

    /// <summary>Only the built-in panel (internal) or only the others (external);
    /// refused when that would leave nothing lit.</summary>
    public static string? Only(bool builtIn, IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, List<XRandRMode>> modes,
        out List<OutputConfig> plan)
    {
        var keep = outputs.Where(o => o.Connected && IsBuiltIn(o.Name) == builtIn).ToList();
        plan = [];
        if (keep.Count == 0) return builtIn ? "there is no built-in display" : "no external display is connected";
        plan = SideBySide(keep, outputs, modes);
        plan.AddRange(outputs.Where(o => o.Connected && !keep.Contains(o)).Select(o => new OutputConfig(o.Name, false)));
        return null;
    }

    /// <summary>One named output lit, every other off.</summary>
    public static string? OnlyOne(string name, IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, List<XRandRMode>> modes,
        out List<OutputConfig> plan)
    {
        plan = [];
        var keep = outputs.FirstOrDefault(o => o.Name == name && o.Connected);
        if (keep is null) return $"no connected output '{name}'";
        plan = SideBySide([keep], outputs, modes);
        plan.AddRange(outputs.Where(o => o.Connected && o.Name != name).Select(o => new OutputConfig(o.Name, false)));
        return null;
    }

    /// <summary>Every connected output showing the same picture at 0,0, at the
    /// largest size all of them offer.</summary>
    public static string? Duplicate(IReadOnlyList<XRandROutput> outputs, IReadOnlyDictionary<string, List<XRandRMode>> modes,
        out List<OutputConfig> plan)
    {
        plan = [];
        var connected = outputs.Where(o => o.Connected).ToList();
        if (connected.Count < 2) return "duplicating needs two connected displays";
        var sizes = connected
            .Select(o => (modes.TryGetValue(o.Name, out var m) ? m : []).Select(x => (x.Width, x.Height)).ToHashSet())
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });
        if (sizes.Count == 0) return "these displays have no resolution in common";
        var (w, h) = sizes.OrderByDescending(s => (long)s.Width * s.Height).First();
        plan = connected.Select(o => new OutputConfig(o.Name, true, w, h, 0, 0, 0, 0)).ToList();
        return null;
    }

    /// <summary>
    /// One output in a new mode or turn. The others keep their places, except
    /// that those to its right or below move with its new edge, so the desk
    /// stays touching; if that still leaves a gap or an overlap, all go side
    /// by side.
    /// </summary>
    public static string? ChangeMode(string name, int width, int height, double hz, int rotation,
        IReadOnlyList<XRandROutput> outputs, out List<OutputConfig> plan)
    {
        plan = [];
        var target = outputs.FirstOrDefault(o => o.Name == name);
        if (target is not { Active: true }) return $"{name} is not lit";

        var changed = new OutputConfig(name, true, width, height, hz, target.PosX, target.PosY, rotation);
        var (newW, newH) = changed.Extent;
        int dW = newW - target.WidthPx, dH = newH - target.HeightPx;
        int right = target.PosX + target.WidthPx, bottom = target.PosY + target.HeightPx;

        var lit = outputs.Where(o => o.Active).ToList();
        var panels = new List<(XRandROutput Output, int X, int Y, int W, int H)>();
        foreach (var o in lit)
        {
            if (o.Name == name) { panels.Add((o, o.PosX, o.PosY, newW, newH)); continue; }
            int x = o.PosX >= right ? o.PosX + dW : o.PosX;
            int y = o.PosY >= bottom ? o.PosY + dH : o.PosY;
            panels.Add((o, x, y, o.WidthPx, o.HeightPx));
        }
        var solver = panels.Select(p => new ArrangementSolver.Panel(p.Output.Name, p.X, p.Y, p.W, p.H, p.Output.Primary)).ToList();
        bool overlap = false;
        for (int i = 0; i < solver.Count; i++)
        for (int j = i + 1; j < solver.Count; j++)
        {
            var (a, b) = (solver[i], solver[j]);
            if (a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom) overlap = true;
        }
        if (overlap || !ArrangementSolver.IsValid(solver))
        {
            int x = 0;
            panels = panels.OrderBy(p => p.Output.PosX).Select(p => { var at = (p.Output, x, 0, p.W, p.H); x += p.W; return at; }).ToList();
        }
        int left = panels.Min(p => p.X), top = panels.Min(p => p.Y);
        foreach (var p in panels)
        {
            plan.Add(p.Output.Name == name
                ? changed with { X = p.X - left, Y = p.Y - top }
                : new OutputConfig(p.Output.Name, true, p.Output.Rotation is 90 or 270 ? p.Output.HeightPx : p.Output.WidthPx,
                    p.Output.Rotation is 90 or 270 ? p.Output.WidthPx : p.Output.HeightPx, p.Output.RefreshHz, p.X - left, p.Y - top, p.Output.Rotation));
        }
        return null;
    }

    /// <summary>The plan that puts the desk back as <paramref name="before"/>
    /// found it: the keep-changes countdown's way back.</summary>
    public static List<OutputConfig> Restore(IReadOnlyList<XRandROutput> before) =>
        before.Where(o => o.Connected).Select(o => o.Active
            ? new OutputConfig(o.Name, true, o.Rotation is 90 or 270 ? o.HeightPx : o.WidthPx, o.Rotation is 90 or 270 ? o.WidthPx : o.HeightPx,
                o.RefreshHz, o.PosX, o.PosY, o.Rotation)
            : new OutputConfig(o.Name, false)).ToList();

    /// <summary>Leaves out what would not change, so an output already where it
    /// is asked to be is not switched off and on again.</summary>
    public static List<OutputConfig> Changes(IReadOnlyList<OutputConfig> plan, IReadOnlyList<XRandROutput> outputs) =>
        plan.Where(c =>
        {
            var o = outputs.FirstOrDefault(x => x.Name == c.Name);
            if (o is null) return true;
            if (!c.Enabled) return o.Active;
            if (!o.Active) return true;
            var (w, h) = c.Extent;
            return (o.WidthPx, o.HeightPx, o.PosX, o.PosY, o.Rotation) != (w, h, c.X, c.Y, c.Rotation)
                || (c.RefreshHz > 0 && Math.Abs(o.RefreshHz - c.RefreshHz) > 0.5);
        }).ToList();

    /// <summary>Lit outputs keep their places (moved together so the desk still
    /// starts at 0,0); outputs being lit join on the right, top-aligned, in
    /// their preferred mode.</summary>
    private static List<OutputConfig> SideBySide(IEnumerable<XRandROutput> lit, IReadOnlyList<XRandROutput> all,
        IReadOnlyDictionary<string, List<XRandRMode>> modes)
    {
        var plan = new List<OutputConfig>();
        var keep = lit.Where(o => o.Active).ToList();
        int left = keep.Count == 0 ? 0 : keep.Min(o => o.PosX), top = keep.Count == 0 ? 0 : keep.Min(o => o.PosY);
        foreach (var o in keep)
        {
            var (w, h) = o.Rotation is 90 or 270 ? (o.HeightPx, o.WidthPx) : (o.WidthPx, o.HeightPx);
            plan.Add(new OutputConfig(o.Name, true, w, h, o.RefreshHz, o.PosX - left, o.PosY - top, o.Rotation));
        }
        int x = plan.Count == 0 ? 0 : plan.Max(p => p.X + p.Extent.W);
        foreach (var o in lit.Where(o => !o.Active))
        {
            var list = modes.TryGetValue(o.Name, out var m) ? m : [];
            var pick = list.FirstOrDefault(p => p.Preferred) ?? list.FirstOrDefault();
            if (pick is null) continue;
            plan.Add(new OutputConfig(o.Name, true, pick.Width, pick.Height, pick.RefreshHz, x, 0, 0));
            x += pick.Width;
        }
        return plan;
    }
}
