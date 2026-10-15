using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Session;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.Snap;

namespace DispCtrl.Linux;

/// <summary>
/// <c>dispctrl-linux engine</c>: the resident process. It serves every command
/// over a Unix socket (one newline-delimited JSON request, one reply) and owns
/// the gamma ramps: night light follows its schedule, a ramp that a mode change
/// or hot-plug reset is put back, and stopping it puts every ramp it warmed or
/// dimmed back to normal.
/// </summary>
/// <remarks>
/// Commands run one at a time behind <see cref="_gate"/>: ddcutil serialises a
/// bus itself, but a ramp pass and a command changing the same setting must
/// not interleave. Output goes to per-request writers, never through
/// <see cref="Console"/>, which is process-wide.
/// </remarks>
internal sealed class EngineHost : IEngineHooks
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _owned = Runtime.LoadOwnedRamps();
    private DateTime _lastPass;
    private readonly Lock _snapGate = new();
    private SnapService? _snap;
    private string? _snapSettings;
    private Inhibitor? _inhibitor;
    private bool _inhibitDisplays;
    private PointerNudge? _nudge;
    private Hotkeys.HotkeyService? _hotkeys;
    private Automation.SessionEvents? _events;
    private double _care = 1;
    private bool _sleptOnce;
    private X11.FocusOverlay? _focus;
    private Session.RoomLight? _roomLight;
    private string? _roomLightProblem;
    private double? _lux;
    private int _lastRoomLevel = -1;
    private readonly HashSet<int> _idleFired = [];
    private string? _lastDesk;
    private (AppRule Rule, Preset? Before)? _ruleHeld;
    private CancellationTokenSource? _rulePending;
    private bool? _darkWanted;

    public static int Run()
    {
        string path = Runtime.SocketPath;
        if (path.Length > Runtime.MaxSocketPath)
        {
            Console.Error.WriteLine($"dispctrl-linux engine: the socket path is too long for a Unix socket ({path.Length} > {Runtime.MaxSocketPath}): {path}");
            return CommandRunner.Refused;
        }
        if (EngineClient.IsRunning(path))
        {
            Console.Error.WriteLine($"dispctrl-linux engine: already running ({path})");
            return CommandRunner.Refused;
        }
        // Started from the window or the snap's launcher, it leaves their
        // session so it outlives them. The setsid program is not allowed under
        // the snap's confinement ("Permission denied"), so the Start button and
        // the snap's first-run start both failed; the call itself is.
        if (Environment.GetEnvironmentVariable(DetachVariable) == "1") setsid();
        return new EngineHost().Serve(path);
    }

    /// <summary>Set by whoever starts the engine in the background
    /// (<c>DeskActions.StartEngine</c>, the snap's launcher).</summary>
    public const string DetachVariable = "DISPCTRL_ENGINE_DETACH";

    private int Serve(string path)
    {
        // A socket file nobody answers on was left by an engine that was killed.
        if (File.Exists(path)) File.Delete(path);
        _socketPath = path;

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        // bind() honours the umask, which left it group-writable (0775).
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop);
        using var watcher = WatchSettings();

        Log($"dispctrl-linux engine {CommandRunner.Version}: listening on {path}");
        if (!GammaRamp.IsAvailable(out var reason))
            Log($"no ramps yet: {reason}. They follow once a client brings an X display, or $DISPLAY is set.");

        var reconcile = Task.Run(ReconcileLoop);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Socket client;
                try { client = listener.AcceptAsync(_stop.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) { Log($"accept failed: {ex.Message}"); continue; }
                _ = Task.Run(() => Serve(client));
            }
        }
        finally
        {
            _stop.Cancel();
            try { reconcile.Wait(5000); } catch (AggregateException) { }
            _gate.Wait(5000);
            lock (_snapGate) { _snap?.Dispose(); _snap = null; }
            _inhibitor?.Dispose();
            _nudge?.Dispose();
            _hotkeys?.Dispose();
            _events?.Dispose();
            _focus?.Dispose();
            _roomLight?.Dispose();
            RampApplier.Release(_owned);
            _owned.Clear();
            Runtime.SaveOwnedRamps(_owned);
            if (File.Exists(path)) File.Delete(path);
            Log("stopped; ramps put back");
        }
        return CommandRunner.Done;
    }

    private void Stop(PosixSignalContext context)
    {
        context.Cancel = true;
        _stop.Cancel();
    }

    private void Serve(Socket client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, utf8);
                using var writer = new StreamWriter(stream, utf8) { AutoFlush = true };
                var line = reader.ReadLine();
                if (line is null) return;
                writer.WriteLine(JsonSerializer.Serialize(Handle(line), EngineClient.Json));
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private EngineReply Handle(string line)
    {
        EngineRequest? request;
        try { request = JsonSerializer.Deserialize<EngineRequest>(line, EngineClient.Json); }
        catch (JsonException) { request = null; }
        if (request?.Args is not { } args)
            return new EngineReply(CommandRunner.AskedWrongly, "", "engine: malformed request (expected {\"args\":[...]})\n");

        AdoptDisplay(request.Env);

        if (args is ["engine", "stop"])
        {
            _stop.Cancel();
            return new EngineReply(CommandRunner.Done, "Stopping the engine; ramps go back to normal.\n", "");
        }
        if (args is ["engine", "status"])
            return new EngineReply(CommandRunner.Done, Describe(), "");

        var output = new StringWriter();
        var error = new StringWriter();
        _gate.Wait();
        try
        {
            var context = new CommandContext(output, error, InEngine: true, ApplyRampsLocked) { Engine = this };
            int code = CommandRunner.Run(args, context);
            return new EngineReply(code, output.ToString(), error.ToString());
        }
        catch (Exception ex)
        {
            Log($"command '{string.Join(' ', args)}' failed: {ex}");
            return new EngineReply(CommandRunner.Refused, output.ToString(), error + $"engine: {ex.Message}\n");
        }
        finally { _gate.Release(); }
    }

    private string Describe()
    {
        var since = _lastPass == default ? "not yet" : $"{_lastPass:HH:mm:ss}";
        return $"engine       running, {CommandRunner.Version}, pid {Environment.ProcessId}\n"
            + $"socket       {Runtime.SocketPath}\n"
            + $"last pass    {since}\n"
            + $"ramps held   {(_owned.Count == 0 ? "none" : string.Join(", ", _owned))}\n";
    }

    /// <summary>An engine started by systemd before the session exported
    /// <c>DISPLAY</c> has no X server to talk to; the first client that has one
    /// lends it. libX11 reads <c>XAUTHORITY</c> from the C environment, which
    /// <see cref="Environment.SetEnvironmentVariable(string, string)"/> does not
    /// reach, hence setenv.</summary>
    private void AdoptDisplay(Dictionary<string, string>? env)
    {
        if (env is null || GammaRamp.DisplayName is not null) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        if (!env.TryGetValue("DISPLAY", out var display) || string.IsNullOrEmpty(display)) return;

        if (env.TryGetValue("XAUTHORITY", out var auth) && !string.IsNullOrEmpty(auth)) setenv("XAUTHORITY", auth, 1);
        setenv("DISPLAY", display, 1);
        Environment.SetEnvironmentVariable("DISPLAY", display);
        GammaRamp.DisplayName = display;
        Log($"using X display {display}, from a client");
        _wake.Release();
    }

    private IReadOnlyList<RampChange> ApplyRampsLocked()
    {
        var changes = RampApplier.Apply(SettingsStore.Load(), TimeOnly.FromDateTime(DateTime.Now), _owned, _care);
        Runtime.SaveOwnedRamps(_owned);
        _lastPass = DateTime.Now;
        foreach (var c in changes) LogChange(c);
        return changes;
    }

    /// <summary>A pass every ten seconds, at each schedule boundary, and whenever
    /// settings change. Ten seconds bounds how long a hot-plugged monitor or a
    /// mode change shows an unwarmed ramp; a pass that finds every ramp in place
    /// writes nothing.</summary>
    private void ReconcileLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            LinuxSettings settings;
            _gate.Wait();
            try
            {
                settings = SettingsStore.Load();
                ApplyRampsLocked();
                UpdateSnap(settings.Snap);
                settings = UpdateAwake(settings);
                UpdateDarkMode(settings);
                UpdateHotkeys(settings);
                UpdateEvents(settings);
                UpdateRoomLight(settings);
            }
            catch (Exception ex)
            {
                Log($"ramp pass failed: {ex.Message}");
                settings = new LinuxSettings();
            }
            finally { _gate.Release(); }

            if (SocketGone())
            {
                Log("the socket is gone (the runtime folder was cleared, as a snap refresh does, or another engine took it over); stopping");
                _stop.Cancel();
                break;
            }

            var wait = TimeSpan.FromSeconds(10);
            var n = settings.NightLight;
            if (n.Enabled && n.Scheduled && Schedule.TryParse(n.From, out var from) && Schedule.TryParse(n.To, out var to))
            {
                var untilBoundary = Schedule.NextBoundary(DateTime.Now, from, to) - DateTime.Now + TimeSpan.FromMilliseconds(200);
                if (untilBoundary < wait) wait = untilBoundary;
            }
            if ((n.Enabled && n.Scheduled) is false && settings.DarkMode.FollowSchedule
                && Schedule.TryParse(n.From, out var darkFrom) && Schedule.TryParse(n.To, out var darkTo))
            {
                var untilBoundary = Schedule.NextBoundary(DateTime.Now, darkFrom, darkTo) - DateTime.Now + TimeSpan.FromMilliseconds(200);
                if (untilBoundary < wait) wait = untilBoundary;
            }
            if (settings.Awake is { Mode: AwakeMode.Until, Until: { } until } && until - DateTimeOffset.Now is var left && left < wait)
                wait = left > TimeSpan.Zero ? left + TimeSpan.FromMilliseconds(200) : TimeSpan.FromMilliseconds(200);
            try { _wake.Wait(wait, _stop.Token); }
            catch (OperationCanceledException) { break; }
            while (_wake.CurrentCount > 0) _wake.Wait(0);
        }
    }

    /// <summary>Starts, reloads or stops the snap thread to match settings. It
    /// starts only once an X display answers - an engine started before the
    /// session exported one gets it from the first client - and restarts if
    /// its thread ended (the X server went away and came back).</summary>
    private void UpdateSnap(SnapSettings settings)
    {
        lock (_snapGate)
        {
            string key = JsonSerializer.Serialize(settings);
            bool wanted = settings.Enabled && !GammaRamp.OnWayland;
            if (!wanted)
            {
                if (_snap is not null) { _snap.Dispose(); _snap = null; Log("snap: off"); }
                _snapSettings = key;
                return;
            }
            if (_snap is { Running: true })
            {
                if (key != _snapSettings) { _snap.Reload(settings); _snapSettings = key; }
                return;
            }
            if (_snap is not null && _snap.Running is false && key == _snapSettings && _snapStartedAt.AddSeconds(30) > DateTime.Now)
                return; // just started, or failed a moment ago: do not spin
            using (var probe = X11.XConnection.Open())
            {
                if (probe is null) return;
            }
            _snap?.Dispose();
            _snap = new SnapService(settings, Log);
            _snap.Start();
            _snapSettings = key;
            _snapStartedAt = DateTime.Now;
        }
    }

    private DateTime _snapStartedAt;
    private string? _socketPath;
    private (DateTime Made, DateTime Written)? _socketId;

    /// <summary>
    /// Whether the socket this engine listens on is no longer the one at its
    /// path. An engine nobody can reach still writes ramps and holds the snap
    /// shortcut, so a second one would fight it: after a snap refresh the old
    /// revision's engine ran on, its socket deleted with the runtime folder,
    /// while the window said "Not running".
    /// </summary>
    private bool SocketGone()
    {
        if (_socketPath is null) return false;
        try
        {
            var info = new FileInfo(_socketPath);
            if (!info.Exists) return true;
            // Its times, not its inode: libc's stat is only exported from glibc
            // 2.33, and the packages run on 2.27. A new socket is a new file.
            var id = (info.CreationTimeUtc, info.LastWriteTimeUtc);
            _socketId ??= id;
            return _socketId != id;
        }
        catch (IOException) { return false; }
    }


    /// <summary>Holds or lets go of keep awake's inhibitors and starts or stops
    /// Stay active to match settings; a timed keep awake whose time has come
    /// is switched off in settings, so every client reads it as off.</summary>
    private LinuxSettings UpdateAwake(LinuxSettings settings)
    {
        var a = settings.Awake;
        if (a.Mode == AwakeMode.Until && !a.ActiveAt(DateTimeOffset.Now))
        {
            settings = SettingsStore.Update(s => { s.Awake.Mode = AwakeMode.Off; s.Awake.Until = null; });
            a = settings.Awake;
            Log("keep awake: its time is up");
        }

        bool wanted = a.ActiveAt(DateTimeOffset.Now);
        if (!wanted || (_inhibitor is not null && _inhibitDisplays != a.KeepDisplaysOn))
        {
            if (_inhibitor is not null) { _inhibitor.Dispose(); _inhibitor = null; Log("keep awake: let go"); }
        }
        if (wanted && _inhibitor is null)
        {
            _inhibitor = Inhibitor.Acquire(a.KeepDisplaysOn, a.KeepDisplaysOn ? "Keep awake, with the displays on" : "Keep awake");
            _inhibitDisplays = a.KeepDisplaysOn;
            Log($"keep awake: holding {(_inhibitor.Holding.Count == 0 ? "nothing" : string.Join(", ", _inhibitor.Holding))}"
                + (_inhibitor.Failed.Count == 0 ? "" : $"; not offered: {string.Join("; ", _inhibitor.Failed)}"));
        }

        bool nudge = a.StayActive && !GammaRamp.OnWayland;
        if (!nudge && _nudge is not null) { _nudge.Dispose(); _nudge = null; }
        if (nudge && _nudge is not { Running: true })
        {
            _nudge?.Dispose();
            using var probe = X11.XConnection.Open();
            if (probe is not null) { _nudge = new PointerNudge(Log); _nudge.Start(); }
        }
        return settings;
    }

    /// <summary>Dark between night light's hours when asked: switched when the
    /// engine starts and at each end of the schedule, never in between, so a
    /// change made by hand lasts until the next boundary.</summary>
    private void UpdateDarkMode(LinuxSettings settings)
    {
        if (!settings.DarkMode.FollowSchedule) { _darkWanted = null; return; }
        if (!Schedule.TryParse(settings.NightLight.From, out var from) || !Schedule.TryParse(settings.NightLight.To, out var to)) return;
        bool wanted = Schedule.IsActive(TimeOnly.FromDateTime(DateTime.Now), from, to);
        if (_darkWanted == wanted) return;
        _darkWanted = wanted;
        if (DarkMode.Read() is bool now && now != wanted)
        {
            if (DarkMode.Set(wanted, out var error)) Log($"dark mode: {(wanted ? "on" : "off")}, on schedule");
            else Log($"dark mode: {error}");
        }
    }

    /// <summary>The hotkeys follow settings once there is a desktop to hold
    /// them on: GNOME's shortcuts or an X display.</summary>
    private void UpdateHotkeys(LinuxSettings settings)
    {
        if (_hotkeys is null)
        {
            // A desktop session first, GNOME or not: GNOME's shortcuts are its
            // real settings, and an engine with no display (the checks') must
            // never write them.
            if (!(GammaRamp.OnWayland && X11.GnomeShortcuts.Available))
            {
                using var probe = X11.XConnection.Open();
                if (probe is null) return;
            }
            _hotkeys = new Hotkeys.HotkeyService(RunHotkey, Log);
        }
        _hotkeys.Apply(settings.Hotkeys ?? []);
    }

    /// <summary>A grabbed key's command, run here as a client's would be.</summary>
    private void RunHotkey(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        _gate.Wait();
        try
        {
            int code = CommandRunner.Run(args, new CommandContext(output, error, InEngine: true, ApplyRampsLocked) { Engine = this });
            if (code != CommandRunner.Done) Log($"hotkey '{string.Join(' ', args)}': {error.ToString().Trim()}");
        }
        catch (Exception ex) { Log($"hotkey '{string.Join(' ', args)}' failed: {ex.Message}"); }
        finally { _gate.Release(); }
    }

    /// <summary>Watches the session once anything needs it: a trigger, a desk
    /// profile or an app rule.</summary>
    private void UpdateEvents(LinuxSettings settings)
    {
        UpdateFocus(settings);
        bool wanted = settings.Triggers.Any(t => t.Enabled) || settings.AppRules.Any(r => r.Enabled) || settings.Presets.Any(p => p.Desk)
            || settings.OledCare.Enabled || settings.MonitorSleep.Enabled;
        if (!wanted) { _events?.Dispose(); _events = null; return; }
        if (_events is not null) return;
        using (var probe = X11.XConnection.Open()) if (probe is null) return;
        _events = new Automation.SessionEvents(e => ThreadPool.QueueUserWorkItem(_ => OnSessionEvent(e)), Log);
        _events.Start();
    }

    private void OnSessionEvent(Automation.SessionEvent e)
    {
        var settings = SettingsStore.Load();
        if (e.Name != "idle") Log($"event: {e.Name}{(e.Subject is null ? "" : $" {e.Subject}")}");

        if (e.Name == "back")
        {
            _idleFired.Clear();
            _sleptOnce = false;
            SetCare(1);
        }
        if (e.Name == "idle") IdleCare(settings, e.Idle);
        for (int i = 0; i < settings.Triggers.Count; i++)
        {
            var t = settings.Triggers[i];
            if (!t.Enabled || t.Event != e.Name) continue;
            if (e.Name == "idle")
            {
                if (e.Idle < TimeSpan.FromMinutes(t.Minutes) || !_idleFired.Add(i)) continue;
            }
            else if (!string.IsNullOrEmpty(t.Match) && !(e.Subject ?? "").Contains(t.Match, StringComparison.OrdinalIgnoreCase)) continue;
            RunFeature(settings, t.Feature);
        }

        if (e.Name is "display-connected" or "display-disconnected") DeskProfile(settings);
        if (e.Name == "app-in-front") AppArrived(settings, e.Subject!);
        if (e.Name == "app-left") AppLeft(e.Subject!);
    }

    /// <summary>OLED care's stages and monitor sleep, from how long nothing has
    /// been touched; paused while a window is full screen or media plays.</summary>
    private void IdleCare(LinuxSettings settings, TimeSpan idle)
    {
        var o = settings.OledCare;
        if (o.Enabled)
        {
            bool paused = (o.PauseFullscreen && _events?.FrontFullscreen == true) || (o.PauseMedia && _events?.MediaPlaying() == true);
            double care = paused ? 1
                : o.SecondStage && idle >= TimeSpan.FromMinutes(o.SecondMinutes) ? 1 - o.SecondDimPercent / 100.0
                : idle >= TimeSpan.FromMinutes(o.IdleMinutes) ? 1 - o.DimPercent / 100.0
                : 1;
            if (_care < 1 && care == 1 && !paused) care = _care; // a rest holds until the next touch
            SetCare(care);
        }
        var sleep = settings.MonitorSleep;
        bool displaysHeld = settings.Awake.ActiveAt(DateTimeOffset.Now) && settings.Awake.KeepDisplaysOn;
        if (sleep.Enabled && !_sleptOnce && !displaysHeld && idle >= TimeSpan.FromMinutes(sleep.Minutes))
        {
            _sleptOnce = true;
            Log("monitor sleep: displays off");
            if (!Session.DisplayPower.TurnOff(out var error)) Log($"monitor sleep: {error}");
        }
    }

    private void SetCare(double care)
    {
        if (Math.Abs(care - _care) < 0.001) return;
        _care = care;
        Log(care >= 1 ? "oled care: back to normal" : $"oled care: {Math.Round((1 - care) * 100)}% dimmer");
        _gate.Wait();
        try { ApplyRampsLocked(); }
        finally { _gate.Release(); }
    }

    /// <summary>Rest now: OLED care's second stage until the next touch, the
    /// watcher's "back" undoing it. Runs inside a command, holding the gate.</summary>
    public void OledRest()
    {
        var o = SettingsStore.Load().OledCare;
        _care = 1 - o.SecondDimPercent / 100.0;
        ApplyRampsLocked();
        if (_events is null)
        {
            using var probe = X11.XConnection.Open();
            if (probe is not null) { _events = new Automation.SessionEvents(e => ThreadPool.QueueUserWorkItem(_ => OnSessionEvent(e)), Log); _events.Start(); }
        }
    }

    private void UpdateFocus(LinuxSettings settings)
    {
        if (!settings.Focus.Enabled || GammaRamp.OnWayland)
        {
            if (_focus is not null) { _focus.Dispose(); _focus = null; }
            return;
        }
        if (_focus is { Running: true }) { _focus.DimPercent = settings.Focus.DimPercent; return; }
        if (_focus?.Problem is not null) return;
        using (var probe = X11.XConnection.Open()) if (probe is null) return;
        _focus = new X11.FocusOverlay(settings.Focus.DimPercent, Log);
        _focus.Start();
    }

    /// <summary>Room light on each pass: unison moves when the light, smoothed,
    /// asks for at least three points more or less, so a passing shadow does
    /// not make the displays pump.</summary>
    private void UpdateRoomLight(LinuxSettings settings)
    {
        if (!settings.RoomLight.Enabled)
        {
            _roomLight?.Dispose();
            _roomLight = null;
            _roomLightProblem = null;
            _lux = null;
            return;
        }
        if (_roomLight is null)
        {
            if (_roomLightProblem is not null) return;
            _roomLight = Session.RoomLight.Open(out _roomLightProblem);
            if (_roomLight is null) { Log($"room light: {_roomLightProblem}"); return; }
        }
        if (_roomLight.Lux() is not double lux) return;
        _lux = _lux is double was ? was * 0.6 + lux * 0.4 : lux;
        int level = Session.RoomLight.LevelFor(_lux.Value, settings.RoomLight.Min, settings.RoomLight.Max);
        if (_lastRoomLevel >= 0 && Math.Abs(level - _lastRoomLevel) < 3) return;
        _lastRoomLevel = level;
        // Called inside the pass, which holds the gate.
        var context = new CommandContext(TextWriter.Null, TextWriter.Null, InEngine: true, ApplyRampsLocked) { Engine = this };
        CommandRunner.SetUnisonFromRoom(level, context);
    }

    public string? FocusProblem() => _focus?.Problem ?? (GammaRamp.OnWayland ? "focus mode needs an X11 session" : null);

    private void RunFeature(LinuxSettings settings, string name)
    {
        if (settings.Features.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } feature)
        {
            Log($"trigger: no feature '{name}'");
            return;
        }
        foreach (var p in Automation.FeatureRunner.Run(feature, RunCommand)) Log($"feature {name}: {p}");
    }

    /// <summary>A command run here, as a client's or a hotkey's would be; the
    /// reason it was refused, or null.</summary>
    private string? RunCommand(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        _gate.Wait();
        try
        {
            int code = CommandRunner.Run(args, new CommandContext(output, error, InEngine: true, ApplyRampsLocked) { Engine = this });
            return code == CommandRunner.Done ? null : error.ToString().Trim();
        }
        catch (Exception ex) { return ex.Message; }
        finally { _gate.Release(); }
    }

    /// <summary>A desk profile applies when exactly its displays are connected,
    /// once per arrival: after the outputs settle, and not again until the
    /// desk changes.</summary>
    private void DeskProfile(LinuxSettings settings)
    {
        Thread.Sleep(3000);
        var connected = _events?.Outputs ?? new Dictionary<string, string?>();
        var profile = settings.Presets.FirstOrDefault(p => p.Desk && Presets.PresetDesk.FitsExactly(p, connected));
        if (profile is null) { _lastDesk = null; return; }
        if (_lastDesk == profile.Name) return;
        _lastDesk = profile.Name;
        Log($"desk profile: {profile.Name}");
        if (RunCommand(["preset", "apply", profile.Name]) is { } error) Log($"desk profile {profile.Name}: {error}");
    }

    private void AppArrived(LinuxSettings settings, string app)
    {
        var rule = settings.AppRules.FirstOrDefault(r => r.Enabled && string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase));
        if (rule is null || _ruleHeld?.Rule.App.Equals(app, StringComparison.OrdinalIgnoreCase) == true) return;
        _rulePending?.Cancel();
        var pending = _rulePending = new CancellationTokenSource();
        if (pending.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(rule.DelaySeconds))) return;
        if (!string.Equals(_events?.FrontApp, app, StringComparison.OrdinalIgnoreCase)) return;
        if (settings.Presets.FirstOrDefault(p => string.Equals(p.Name, rule.Preset, StringComparison.OrdinalIgnoreCase)) is not { } preset) return;
        // The desk as it was, only the parts the rule's preset will change.
        Preset? before = rule.Then == "previous"
            ? Presets.PresetDesk.Capture("(before " + app + ")", PresetParts.All.Where(p => !preset.Holds(p)).ToList())
            : null;
        _ruleHeld = (rule, before);
        Log($"app rule: {app} in front, {rule.Preset}");
        if (RunCommand(["preset", "apply", rule.Preset]) is { } error) Log($"app rule {app}: {error}");
    }

    private void AppLeft(string app)
    {
        _rulePending?.Cancel();
        if (_ruleHeld is not { } held || !held.Rule.App.Equals(app, StringComparison.OrdinalIgnoreCase)) return;
        _ruleHeld = null;
        switch (held.Rule.Then)
        {
            case "keep":
                break;
            case "previous" when held.Before is { } before:
                Log($"app rule: {app} left, the desk goes back");
                _gate.Wait();
                try
                {
                    foreach (var p in Presets.PresetDesk.Apply(before)) Log($"app rule {app}: {p}");
                    ApplyRampsLocked();
                }
                finally { _gate.Release(); }
                break;
            default:
                if (RunCommand(["preset", "apply", held.Rule.Then]) is { } error) Log($"app rule {app}: {error}");
                break;
        }
    }

    public string HotkeyStatus() => _hotkeys?.Status() ?? "not held yet: no desktop to hold them on";

    public string AwakeStatus()
    {
        var parts = new List<string>();
        if (_inhibitor is { } i)
            parts.Add(i.Holding.Count == 0 ? "no inhibitor was granted" : string.Join(", ", i.Holding));
        else parts.Add("nothing (keep awake off)");
        if (_nudge is { Running: true } n)
            parts.Add(n.LastNudge == default ? "stay active running" : $"stay active, last nudge {n.LastNudge.ToLocalTime():HH:mm:ss}");
        return string.Join("; ", parts);
    }

    public bool SnapPick(out string? why)
    {
        lock (_snapGate)
        {
            if (_snap is not { Running: true })
            {
                why = SettingsStore.Load().Snap.Enabled ? "snap layouts are not running (no X display yet?)" : "snap layouts are off: dispctrl-linux snap on";
                return false;
            }
            _snap.Pick();
            why = null;
            return true;
        }
    }

    public string SnapStatus()
    {
        lock (_snapGate)
        {
            if (_snap is not { Running: true }) return "running, but snap layouts are not active";
            return _snap.ShortcutProblem is { } problem ? $"active; shortcut not held: {problem}" : "active";
        }
    }

    /// <summary>A DispCtrl save is a rename onto settings.json and wakes the pass
    /// at once; an editor writing in place raises several changes mid-write, so
    /// those wait 120 ms for the file to be whole - the Windows engine's rule.</summary>
    private FileSystemWatcher? WatchSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Directory);
            var watcher = new FileSystemWatcher(SettingsStore.Directory, "settings.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            Timer? debounce = null;
            watcher.Renamed += (_, _) => _wake.Release();
            watcher.Created += (_, _) => _wake.Release();
            watcher.Changed += (_, _) =>
            {
                debounce?.Dispose();
                debounce = new Timer(_ => _wake.Release(), null, 120, Timeout.Infinite);
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log($"not watching settings ({ex.Message}); changes apply on the next pass");
            return null;
        }
    }

    private static void LogChange(RampChange change)
    {
        var t = change.Target;
        if (!change.Written) Log($"{change.Output}: ramp refused: {change.Error}");
        else if (t.IsIdentity) Log($"{change.Output}: ramp back to normal");
        else Log($"{change.Output}: ramp {t.Red:0.000} {t.Green:0.000} {t.Blue:0.000} x {t.Dim:0.00}");
    }

    // journald stamps each line itself; a terminal run reads the same.
    private static void Log(string message) => Console.Error.WriteLine(message);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}
