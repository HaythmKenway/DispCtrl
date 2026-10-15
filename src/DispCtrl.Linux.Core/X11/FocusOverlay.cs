using DispCtrl.Linux.Graphics;

namespace DispCtrl.Linux.X11;

/// <summary>
/// Focus mode: a dark veil over every display except the window in front,
/// drawn on overlays that take no input, so every click still lands.
/// </summary>
/// <remarks>
/// One overlay per monitor, repainted only when the window in front moves,
/// changes or goes full screen (then the veil lifts: a film or a game is the
/// focus). It needs a compositor to blend the veil; without a 32-bit visual
/// it says so and stays off. The veil is a set of rectangles round the clear
/// areas, not a shaped hole, so nothing depends on the Shape extension's output.
/// The clear areas are the window in front with its dialogs and popups, docks
/// and panels, and DispCtrl's own windows; see <see cref="Desktop.ClearOf"/>.
/// </remarks>
public sealed class FocusOverlay : IDisposable
{
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private volatile int _dimPercent;

    public FocusOverlay(int dimPercent, Action<string> log)
    {
        _dimPercent = dimPercent;
        _log = log;
    }

    public string? Problem { get; private set; }

    public bool Running => _thread is { IsAlive: true };

    public int DimPercent { set => _dimPercent = value; }

    public void Start()
    {
        if (Running) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "focus mode" };
        _thread.Start();
    }

    private void Loop()
    {
        using var x = XConnection.Open();
        if (x is null) { Problem = "no X display"; return; }
        var overlays = new Dictionary<string, Overlay>(StringComparer.Ordinal);
        (string Holes, string Monitors, int Dim) last = ("", "", -1);
        _log("focus mode: on");
        try
        {
            while (!_stop.Token.WaitHandle.WaitOne(120))
            {
                var monitors = Desktop.Monitors(x);
                string layout = string.Join(";", monitors.Select(m => $"{m.Name}{m.Bounds}"));
                ulong? active = Desktop.Active(x);
                AppWindow? front = active is { } id ? Desktop.Describe(x, id) : null;
                bool lifted = front is null || front.Fullscreen;
                var holes = new List<Rect>();
                if (!lifted && front is { Minimized: false })
                {
                    var ignore = overlays.Values.Select(o => o.Window).ToHashSet();
                    holes.AddRange(Desktop.ClearOf(x, front.Id, ignore));
                    // The shell's own bar and dock are not X windows: they are what the work area leaves out.
                    foreach (var m in monitors)
                        holes.AddRange(VeilRects(m.Bounds, [m.WorkArea]));
                }
                string key = lifted ? "lifted" : string.Join(";", holes.Distinct().OrderBy(h => h.X).ThenBy(h => h.Y)
                    .ThenBy(h => h.Width).ThenBy(h => h.Height));
                int dim = _dimPercent;
                if ((key, layout, dim) == last) continue;
                last = (key, layout, dim);

                foreach (var gone in overlays.Keys.Except(monitors.Select(m => m.Name)).ToList())
                {
                    overlays[gone].Dispose();
                    overlays.Remove(gone);
                }
                foreach (var m in monitors)
                {
                    if (!overlays.TryGetValue(m.Name, out var overlay))
                    {
                        overlay = Overlay.Create(x, m.Bounds)!;
                        if (overlay is null)
                        {
                            Problem = "focus mode needs a compositor (a desktop that blends windows)";
                            _log($"focus mode: {Problem}");
                            return;
                        }
                        overlays[m.Name] = overlay;
                    }
                    overlay.MoveTo(m.Bounds);
                    // No window in front, or one full screen: nothing to veil round.
                    if (lifted)
                    {
                        overlay.Hide();
                        continue;
                    }
                    var veil = new Rgba(0, 0, 0, dim / 100.0);
                    var rects = VeilRects(m.Bounds, holes);
                    overlay.Paint(canvas =>
                    {
                        foreach (var r in rects)
                            Fill(canvas, r.X - m.Bounds.X, r.Y - m.Bounds.Y, r.Width, r.Height, veil);
                    });
                }
            }
        }
        finally
        {
            foreach (var o in overlays.Values) o.Dispose();
            _log("focus mode: off");
        }
    }

    /// <summary>The part of <paramref name="bounds"/> that no hole covers, as
    /// disjoint rectangles in the same coordinates: the veil, however many
    /// windows must stay clear and however they overlap.</summary>
    /// <remarks>Cut into horizontal bands at every hole edge; in each band the
    /// holes' spans are merged and the gaps between them kept. Bands with the
    /// same gaps are joined again so a lone window costs four rectangles, not
    /// a rectangle per band.</remarks>
    internal static List<Rect> VeilRects(Rect bounds, IReadOnlyList<Rect> holes)
    {
        var result = new List<Rect>();
        if (bounds.IsEmpty) return result;
        var clipped = holes.Select(h => h.Intersect(bounds)).Where(h => !h.IsEmpty).ToList();
        var edges = new SortedSet<int> { bounds.Y, bounds.Bottom };
        foreach (var h in clipped) { edges.Add(h.Y); edges.Add(h.Bottom); }
        var ys = edges.ToList();

        var open = new List<Rect>();
        for (int i = 0; i + 1 < ys.Count; i++)
        {
            int top = ys[i], bottom = ys[i + 1];
            var spans = clipped.Where(h => h.Y < bottom && h.Bottom > top)
                .Select(h => (h.X, h.Right)).OrderBy(s => s.X).ToList();
            var gaps = new List<(int From, int To)>();
            int cursor = bounds.X;
            foreach (var (from, to) in spans)
            {
                if (from > cursor) gaps.Add((cursor, from));
                cursor = Math.Max(cursor, to);
            }
            if (cursor < bounds.Right) gaps.Add((cursor, bounds.Right));

            var next = new List<Rect>();
            foreach (var (from, to) in gaps)
            {
                int k = open.FindIndex(r => r.X == from && r.Right == to && r.Bottom == top);
                if (k >= 0)
                {
                    next.Add(open[k] with { Height = bottom - open[k].Y });
                    open.RemoveAt(k);
                }
                else next.Add(new Rect(from, top, to - from, bottom - top));
            }
            result.AddRange(open);
            open = next;
        }
        result.AddRange(open);
        return result;
    }

    private static void Fill(Canvas canvas, double x, double y, double w, double h, Rgba colour)
    {
        if (w <= 0 || h <= 0) return;
        canvas.RoundedRect(x, y, w, h, 0);
        canvas.Fill(colour);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(2000);
        _stop.Dispose();
    }
}
