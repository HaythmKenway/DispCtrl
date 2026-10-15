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
/// it says so and stays off. The veil is four rectangles round the window,
/// not a shaped hole, so nothing depends on the Shape extension's output.
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
        (Rect? Hole, string Monitors, int Dim) last = (null, "", -1);
        _log("focus mode: on");
        try
        {
            while (!_stop.Token.WaitHandle.WaitOne(120))
            {
                var monitors = Desktop.Monitors(x);
                string layout = string.Join(";", monitors.Select(m => $"{m.Name}{m.Bounds}"));
                AppWindow? front = Desktop.Active(x) is { } id ? Desktop.Describe(x, id) : null;
                Rect? hole = front is { Minimized: false, Fullscreen: false } ? front.Frame : null;
                int dim = _dimPercent;
                if ((hole, layout, dim) == last) continue;
                last = (hole, layout, dim);

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
                    if (hole is null && front is { Fullscreen: true } || front is null)
                    {
                        overlay.Hide();
                        continue;
                    }
                    var veil = new Rgba(0, 0, 0, dim / 100.0);
                    overlay.Paint(canvas =>
                    {
                        var b = m.Bounds;
                        if (hole is not { } h || h.Intersect(b).IsEmpty)
                        {
                            canvas.RoundedRect(0, 0, b.Width, b.Height, 0);
                            canvas.Fill(veil);
                            return;
                        }
                        var inside = h.Intersect(b);
                        int left = inside.X - b.X, top = inside.Y - b.Y, right = inside.Right - b.X, bottom = inside.Bottom - b.Y;
                        Fill(canvas, 0, 0, b.Width, top, veil);
                        Fill(canvas, 0, bottom, b.Width, b.Height - bottom, veil);
                        Fill(canvas, 0, top, left, bottom - top, veil);
                        Fill(canvas, right, top, b.Width - right, bottom - top, veil);
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
