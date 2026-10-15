using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Placement;

/// <summary>Where a window goes when it changes display: pure geometry, so the
/// checks can try it without a window manager.</summary>
public static class WindowMoves
{
    /// <summary>The frame on <paramref name="to"/>'s work area that sits where
    /// <paramref name="frame"/> sat on <paramref name="from"/>'s: the same share
    /// of the way across and down, no larger than the area it lands on.</summary>
    public static Rect Carry(Rect frame, Rect from, Rect to)
    {
        int w = Math.Min(frame.Width, to.Width), h = Math.Min(frame.Height, to.Height);
        double fx = from.Width > frame.Width ? (double)(frame.X - from.X) / (from.Width - frame.Width) : 0.5;
        double fy = from.Height > frame.Height ? (double)(frame.Y - from.Y) / (from.Height - frame.Height) : 0.5;
        fx = Math.Clamp(fx, 0, 1);
        fy = Math.Clamp(fy, 0, 1);
        return new Rect(to.X + (int)Math.Round(fx * (to.Width - w)), to.Y + (int)Math.Round(fy * (to.Height - h)), w, h);
    }

    /// <summary>The monitor after (or before) <paramref name="current"/>, left
    /// to right, wrapping round.</summary>
    public static int Neighbour(IReadOnlyList<MonitorInfo> monitors, MonitorInfo current, int direction)
    {
        var order = Ordered(monitors);
        int at = order.FindIndex(m => m.Name == current.Name);
        return monitors.ToList().IndexOf(order[((at < 0 ? 0 : at) + direction + order.Count) % order.Count]);
    }

    /// <summary>Monitors as people number them: left to right, then top to bottom.</summary>
    public static List<MonitorInfo> Ordered(IReadOnlyList<MonitorInfo> monitors) =>
        monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();

    /// <summary>One rectangle over every given work area: a window spanning
    /// displays.</summary>
    public static Rect Span(IEnumerable<Rect> areas)
    {
        var list = areas.ToList();
        int left = list.Min(a => a.X), top = list.Max(a => a.Y);
        int right = list.Max(a => a.Right), bottom = list.Min(a => a.Bottom);
        return new Rect(left, top, right - left, Math.Max(1, bottom - top));
    }
}
