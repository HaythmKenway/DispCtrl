using System.Runtime.InteropServices;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Session;

/// <summary>Stay active, the "mouse jiggler": after <see cref="Idle"/> without
/// the pointer moving, it moves one pixel and straight back.</summary>
/// <remarks>
/// The Windows engine's rule (PowerService): only after 55 seconds of nothing,
/// so a hand on the mouse is never fought, and under the minute most chat apps
/// and remote sessions wait before calling someone away. Through XTest, as a
/// real device would move it, so every idle clock X keeps (the screensaver's,
/// the XSync IDLETIME counter GNOME and KDE read) starts again. Stillness is
/// judged by the pointer alone: typing without touching the mouse earns a nudge
/// nobody can see, which is cheaper than another library for the idle time.
/// </remarks>
public sealed class PointerNudge : IDisposable
{
    private const string LibXtst = "libXtst.so.6";

    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(55);

    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public PointerNudge(Action<string> log) => _log = log;

    /// <summary>When it last moved the pointer, for anything that must not
    /// count its own nudge as a person (none yet; OLED care will).</summary>
    public DateTime LastNudge { get; private set; }

    public bool Running => _thread is { IsAlive: true };

    public void Start()
    {
        if (Running) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "stay active" };
        _thread.Start();
    }

    private void Run()
    {
        using var x = XConnection.Open();
        if (x is null) { _log("stay active: no X display"); return; }
        try
        {
            if (XTestQueryExtension(x.Display, out _, out _, out _, out _) == 0)
            {
                _log("stay active: the X server has no XTEST extension");
                return;
            }
        }
        catch (DllNotFoundException)
        {
            _log("stay active: libXtst is missing");
            return;
        }

        _log("stay active: on");
        (int X, int Y) last = Where(x);
        var stillSince = DateTime.UtcNow;
        while (!_stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
        {
            var now = Where(x);
            if (now != last) { last = now; stillSince = DateTime.UtcNow; continue; }
            if (DateTime.UtcNow - stillSince < Idle) continue;

            XTestFakeRelativeMotionEvent(x.Display, 1, 0, 0);
            XTestFakeRelativeMotionEvent(x.Display, -1, 0, 0);
            Xlib.XFlush(x.Display);
            LastNudge = DateTime.UtcNow;
            stillSince = DateTime.UtcNow;
        }
        _log("stay active: off");
    }

    private static (int X, int Y) Where(XConnection x)
    {
        Xlib.XQueryPointer(x.Display, x.Root, out _, out _, out int rx, out int ry, out _, out _, out _);
        return (rx, ry);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(2000);
        _stop.Dispose();
    }

    [DllImport(LibXtst)]
    private static extern int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);

    [DllImport(LibXtst)]
    private static extern int XTestFakeRelativeMotionEvent(nint display, int dx, int dy, ulong delay);
}
