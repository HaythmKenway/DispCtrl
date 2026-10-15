using System.Diagnostics;
using System.Runtime.InteropServices;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.X11;
using Tmds.DBus.Protocol;

namespace DispCtrl.Linux.Automation;

/// <summary>Something that happened in the session: a <see cref="Settings.Trigger"/>
/// event, what it happened to (an output, an app's class, a time), and for
/// idle how long nothing has been touched.</summary>
public sealed record SessionEvent(string Name, string? Subject, TimeSpan Idle = default);

/// <summary>
/// The engine's eyes for triggers, desk profiles and app rules: one thread that
/// looks at the session once a second and reports what changed.
/// </summary>
/// <remarks>
/// Polling, not subscriptions: each source (RandR, EWMH, logind, sysfs) has its
/// own way of announcing a change, and a second's latency is nothing to a
/// person waiting for a preset. Each look costs a few X round trips and one
/// D-Bus call. Idle is the X server's own count (XScreenSaver), keyboard
/// included; resume is a jump between the wall clock and the monotonic one,
/// which stops while the machine sleeps.
/// </remarks>
public sealed unsafe class SessionEvents : IDisposable
{
    private const string LibXss = "libXss.so.1";

    private readonly Action<SessionEvent> _raise;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public SessionEvents(Action<SessionEvent> raise, Action<string> log)
    {
        _raise = raise;
        _log = log;
    }

    /// <summary>The app in front now, by window class; null when none is.</summary>
    public string? FrontApp { get; private set; }

    /// <summary>Whether the window in front is full screen (a film, a game).</summary>
    public bool FrontFullscreen { get; private set; }

    /// <summary>Connected outputs now, by name, with their monitor names.</summary>
    public IReadOnlyDictionary<string, string?> Outputs { get; private set; } = new Dictionary<string, string?>();

    public void Start()
    {
        if (_thread is { IsAlive: true }) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "session events" };
        _thread.Start();
    }

    private void Loop()
    {
        using var x = XConnection.Open();
        DBusConnection? system = null;
        try
        {
            system = new DBusConnection(DBusAddress.System!);
            system.ConnectAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { system?.Dispose(); system = null; }

        bool xss = x is not null && HasXss(x);
        var clock = Stopwatch.StartNew();
        var wallStart = DateTime.UtcNow;
        string? lastMinute = null;
        bool? locked = null, onMains = null, idleReported = false;
        TimeSpan lastIdle = TimeSpan.Zero;
        int tick = 0;

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                // Outputs every two seconds; the rest every second.
                if (tick++ % 2 == 0) Displays();
                if (x is not null) Front(x);

                if (x is not null && xss && Idle(x) is { } idle)
                {
                    if (idle < lastIdle && idleReported == true) _raise(new SessionEvent("back", null, lastIdle));
                    if (idle < lastIdle) idleReported = false;
                    lastIdle = idle;
                    // Idle is reported every second while idle: each trigger
                    // picks its own minute.
                    if (idle >= TimeSpan.FromMinutes(1)) { _raise(new SessionEvent("idle", null, idle)); idleReported = true; }
                }

                if (system is not null && LockedHint(system) is bool l)
                {
                    if (locked is bool was && was != l) _raise(new SessionEvent(l ? "locked" : "unlocked", null));
                    locked = l;
                }

                if (Mains() is bool mains)
                {
                    if (onMains is bool was && was != mains) _raise(new SessionEvent(mains ? "on-power" : "on-battery", null));
                    onMains = mains;
                }

                var gap = DateTime.UtcNow - wallStart - clock.Elapsed;
                if (gap > TimeSpan.FromSeconds(20)) _raise(new SessionEvent("resumed", null));
                if (gap.Duration() > TimeSpan.FromSeconds(20)) { clock.Restart(); wallStart = DateTime.UtcNow; }

                var minute = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (minute != lastMinute)
                {
                    if (lastMinute is not null) _raise(new SessionEvent("at-time", minute));
                    lastMinute = minute;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"session events: {ex.Message}");
            }
            _stop.Token.WaitHandle.WaitOne(1000);
        }
        system?.Dispose();
    }

    private void Displays()
    {
        if (!XRandR.IsAvailable) return;
        var names = XRandR.MonitorNames();
        var now = XRandR.Query().Where(o => o.Connected).ToDictionary(o => o.Name, o => names.GetValueOrDefault(o.Name), StringComparer.Ordinal);
        var before = Outputs;
        Outputs = now;
        if (before.Count == 0 && now.Count > 0 && _first) { _first = false; return; }
        _first = false;
        foreach (var (name, model) in now)
            if (!before.ContainsKey(name)) _raise(new SessionEvent("display-connected", model ?? name));
        foreach (var (name, model) in before)
            if (!now.ContainsKey(name)) _raise(new SessionEvent("display-disconnected", model ?? name));
    }

    private bool _first = true;

    private void Front(XConnection x)
    {
        var window = Desktop.Active(x) is { } id ? Desktop.Describe(x, id) : null;
        FrontFullscreen = window?.Fullscreen == true;
        string? app = window?.AppClass;
        if (app == FrontApp) return;
        var left = FrontApp;
        FrontApp = app;
        if (left is not null) _raise(new SessionEvent("app-left", left));
        if (app is not null) _raise(new SessionEvent("app-in-front", app));
    }

    /// <summary>Mains power present, from the kernel; null on a desktop with no
    /// battery to tell it from.</summary>
    internal static bool? Mains(string root = "/sys/class/power_supply")
    {
        if (!Directory.Exists(root)) return null;
        bool battery = false;
        bool? online = null;
        foreach (var dir in Directory.GetDirectories(root))
        {
            string type = ReadText(Path.Combine(dir, "type"));
            if (type == "Battery") battery = true;
            if (type == "Mains" && ReadText(Path.Combine(dir, "online")) is { Length: > 0 } o) online = (online ?? false) || o == "1";
        }
        return battery ? online ?? false : null;
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static bool? LockedHint(DBusConnection system)
    {
        try
        {
            MessageBuffer Build()
            {
                var w = system.GetMessageWriter();
                try
                {
                    w.WriteMethodCallHeader("org.freedesktop.login1", "/org/freedesktop/login1/session/auto",
                        "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
                    w.WriteString("org.freedesktop.login1.Session");
                    w.WriteString("LockedHint");
                    return w.CreateMessage();
                }
                finally { w.Dispose(); }
            }
            var task = system.CallMethodAsync(Build(), (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetBool(), null);
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : null;
        }
        catch (Exception) { return null; }
    }

    private DBusConnection? _session;

    /// <summary>Whether any media player on the session bus says it is
    /// playing (MPRIS): a film in a window is watched without a touch.</summary>
    public bool MediaPlaying()
    {
        try
        {
            if (_session is null)
            {
                _session = new DBusConnection(DBusAddress.Session!);
                _session.ConnectAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            MessageBuffer ListNames()
            {
                var w = _session.GetMessageWriter();
                try
                {
                    w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames", null, MessageFlags.None);
                    return w.CreateMessage();
                }
                finally { w.Dispose(); }
            }
            var names = _session.CallMethodAsync(ListNames(), (Message m, object? _) => m.GetBodyReader().ReadArrayOfString(), null);
            if (!names.Wait(TimeSpan.FromSeconds(2))) return false;
            foreach (var name in names.Result.Where(n => n.StartsWith("org.mpris.MediaPlayer2.", StringComparison.Ordinal)))
            {
                MessageBuffer Get()
                {
                    var w = _session.GetMessageWriter();
                    try
                    {
                        w.WriteMethodCallHeader(name, "/org/mpris/MediaPlayer2", "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
                        w.WriteString("org.mpris.MediaPlayer2.Player");
                        w.WriteString("PlaybackStatus");
                        return w.CreateMessage();
                    }
                    finally { w.Dispose(); }
                }
                var status = _session.CallMethodAsync(Get(), (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetString(), null);
                if (status.Wait(TimeSpan.FromSeconds(1)) && status.Result == "Playing") return true;
            }
        }
        catch (Exception)
        {
            _session?.Dispose();
            _session = null;
        }
        return false;
    }

    private static bool HasXss(XConnection x)
    {
        try { return XScreenSaverQueryExtension(x.Display, out _, out _) != 0; }
        catch (DllNotFoundException) { return false; }
    }

    private static TimeSpan? Idle(XConnection x)
    {
        var info = XScreenSaverAllocInfo();
        if (info == 0) return null;
        try
        {
            if (XScreenSaverQueryInfo(x.Display, x.Root, info) == 0) return null;
            // XScreenSaverInfo on LP64: window (8), state (4), kind (4), til_or_since (8), idle (8).
            return TimeSpan.FromMilliseconds(*(ulong*)(info + 24));
        }
        finally { Xlib.XFree(info); }
    }

    [DllImport(LibXss)] private static extern int XScreenSaverQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport(LibXss)] private static extern nint XScreenSaverAllocInfo();
    [DllImport(LibXss)] private static extern int XScreenSaverQueryInfo(nint display, ulong drawable, nint info);

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(3000);
        _stop.Dispose();
        _session?.Dispose();
    }
}
