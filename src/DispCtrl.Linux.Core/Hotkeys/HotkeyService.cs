using System.Text.Json;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Hotkeys;

/// <summary>
/// The engine's global shortcuts: each enabled <see cref="HotkeyBinding"/> runs
/// its dispctrl-linux command when pressed, with every window closed.
/// </summary>
/// <remarks>
/// On GNOME they become GNOME's own custom shortcuts, as the snap layouts
/// shortcut does: GNOME Shell keeps Super for itself, so an X grab of it is
/// never delivered, and GNOME's shortcuts also work on Wayland. They run
/// <c>dispctrl-linux COMMAND</c>, which reaches this engine. Elsewhere the keys
/// are grabbed on the root window, on a thread of their own with its own X
/// connection, and the command runs in the engine.
/// </remarks>
public sealed unsafe class HotkeyService : IDisposable
{
    private const string GnomePrefix = "dispctrl-hotkey-";

    private readonly Action<string[]> _run;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private string? _applied;
    private List<HotkeyBinding> _bindings = [];
    private volatile bool _stop;
    private volatile bool _reload;
    private Thread? _thread;
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private bool _gnome;

    public HotkeyService(Action<string[]> run, Action<string> log)
    {
        _run = run;
        _log = log;
    }

    /// <summary>Holds exactly <paramref name="bindings"/>' enabled keys; cheap
    /// when they have not changed since the last call.</summary>
    public void Apply(IReadOnlyList<HotkeyBinding> bindings)
    {
        var key = JsonSerializer.Serialize(bindings.Where(b => b.Enabled).Select(b => (b.Keys, b.Command)).ToList());
        lock (_gate)
        {
            if (key == _applied && (_gnome || _thread is { IsAlive: true })) return;
            _applied = key;
            _bindings = bindings.Where(b => b.Enabled).ToList();
            _problems.Clear();
        }

        _gnome = GnomeShortcuts.Available;
        if (_gnome)
        {
            StopThread();
            var kept = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (Shortcut.Parse(b.Keys) is not { } shortcut) { Problem(b.Keys, "not a shortcut"); continue; }
                string id = $"{GnomePrefix}{i + 1}";
                var accelerator = GnomeShortcuts.Accelerator(shortcut);
                if (GnomeShortcuts.TakenBy(accelerator, id) is { } other && !other.StartsWith("DispCtrl", StringComparison.Ordinal))
                {
                    Problem(b.Keys, $"already GNOME's shortcut for \"{other}\"");
                    continue;
                }
                if (GnomeShortcuts.Set(id, $"DispCtrl: {b.Command}", GnomeShortcuts.CommandLine(b.Command), accelerator) is { } error)
                {
                    Problem(b.Keys, error);
                    continue;
                }
                kept.Add(id);
            }
            GnomeShortcuts.RemoveStale(GnomePrefix, kept);
            _log($"hotkeys: {kept.Count} as GNOME shortcuts{(_problems.Count == 0 ? "" : $"; {_problems.Count} refused")}");
            return;
        }

        if (_thread is { IsAlive: true }) { _reload = true; return; }
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
        _thread.Start();
    }

    public string Status()
    {
        lock (_gate)
        {
            var held = _bindings.Count - _problems.Count;
            var how = _gnome ? "as GNOME custom shortcuts" : "grabbed from X";
            var lines = new List<string> { $"{held} of {_bindings.Count} held, {how}" };
            lines.AddRange(_problems.Select(p => $"  {p.Key}: {p.Value}"));
            return string.Join('\n', lines);
        }
    }

    private void Problem(string keys, string why)
    {
        lock (_gate) _problems[keys] = why;
        _log($"hotkeys: {keys}: {why}");
    }

    private void Loop()
    {
        using var x = XConnection.Open();
        if (x is null) { _log("hotkeys: no X display"); return; }
        var grabbed = new List<(int Keycode, Shortcut Shortcut, string Command)>();
        var fds = stackalloc Xlib.PollFd[1];
        byte* ev = stackalloc byte[Xlib.EventSize];
        _reload = true;
        while (!_stop)
        {
            if (_reload)
            {
                _reload = false;
                foreach (var (code, shortcut, _) in grabbed)
                    foreach (var m in shortcut.WithLocks()) Xlib.XUngrabKey(x.Display, code, m, x.Root);
                grabbed.Clear();
                List<HotkeyBinding> bindings;
                lock (_gate) bindings = [.. _bindings];
                foreach (var b in bindings)
                {
                    if (Shortcut.Parse(b.Keys) is not { } shortcut) { Problem(b.Keys, "not a shortcut"); continue; }
                    var keysym = Xlib.XStringToKeysym(shortcut.Keysym);
                    int keycode = keysym == 0 ? 0 : Xlib.XKeysymToKeycode(x.Display, keysym);
                    if (keycode == 0) { Problem(b.Keys, $"this keyboard has no {shortcut.Keysym} key"); continue; }
                    Xlib.LastErrorCode = 0;
                    foreach (var m in shortcut.WithLocks())
                        Xlib.XGrabKey(x.Display, keycode, m, x.Root, Xlib.False, Xlib.GrabModeAsync, Xlib.GrabModeAsync);
                    Xlib.XSync(x.Display, Xlib.False);
                    if (Xlib.LastErrorCode == Xlib.BadAccess)
                    {
                        foreach (var m in shortcut.WithLocks()) Xlib.XUngrabKey(x.Display, keycode, m, x.Root);
                        Problem(b.Keys, "taken by another program");
                        continue;
                    }
                    grabbed.Add((keycode, shortcut, b.Command));
                }
                _log($"hotkeys: {grabbed.Count} grabbed");
            }

            while (Xlib.XPending(x.Display) > 0)
            {
                Xlib.XNextEvent(x.Display, ev);
                if (*(int*)ev != Xlib.KeyPress) continue;
                var (_, _, _, _, state, keycode) = Xlib.Input(ev);
                foreach (var (code, shortcut, command) in grabbed)
                {
                    if (code != keycode || !shortcut.Matches(state)) continue;
                    var args = Automation.FeatureRunner.Split(command).ToArray();
                    // Off this thread: a DDC/CI write takes a moment, and the
                    // key repeat must not queue behind it here.
                    ThreadPool.QueueUserWorkItem(_ => _run(args));
                }
            }
            fds[0] = new Xlib.PollFd { fd = Xlib.XConnectionNumber(x.Display), events = Xlib.POLLIN };
            Xlib.poll(fds, 1, 250);
        }
        foreach (var (code, shortcut, _) in grabbed)
            foreach (var m in shortcut.WithLocks()) Xlib.XUngrabKey(x.Display, code, m, x.Root);
        x.Flush();
    }

    private void StopThread()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }

    /// <summary>Lets go of every key; GNOME's entries stay, as GNOME runs them
    /// whether or not the engine does, and reach it when it is back.</summary>
    public void Dispose() => StopThread();
}
