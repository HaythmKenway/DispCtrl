using System.Globalization;
using System.Reflection;
using DispCtrl.Linux.Diagnostics;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

/// <summary>Where a command runs: its output, and what applies the ramps after
/// a setting changes.</summary>
/// <param name="ApplyRamps">Puts every ramp where the settings now say and
/// returns what it wrote. The engine passes its own pass, so it stays the one
/// writer; a local run applies once and leaves it there.</param>
public sealed record CommandContext(TextWriter Out, TextWriter Err, bool InEngine, Func<IReadOnlyList<RampChange>> ApplyRamps)
{
    /// <summary>What only the running engine can do (it holds the snap thread);
    /// null outside it.</summary>
    public IEngineHooks? Engine { get; init; }

    public static CommandContext Local(TextWriter output, TextWriter error) =>
        new(output, error, InEngine: false, ApplyLocally);

    /// <summary>One pass outside the engine, remembering which outputs it left
    /// warm or dim so a later pass (or the engine) may put them back.</summary>
    public static IReadOnlyList<RampChange> ApplyLocally()
    {
        var owned = Runtime.LoadOwnedRamps();
        var changes = RampApplier.Apply(SettingsStore.Load(), TimeOnly.FromDateTime(DateTime.Now), owned);
        Runtime.SaveOwnedRamps(owned);
        return changes;
    }
}

/// <summary>The engine's own state, for commands that need it.</summary>
public interface IEngineHooks
{
    /// <summary>Opens the snap layouts for the window in front.</summary>
    bool SnapPick(out string? why);

    /// <summary>Whether snapping is live, and what is in its way.</summary>
    string SnapStatus();

    /// <summary>The inhibitors keep awake holds, and Stay active's state.</summary>
    string AwakeStatus();

    /// <summary>Which hotkeys are held, and any refused.</summary>
    string HotkeyStatus();

    /// <summary>OLED care's second stage now, until the next touch.</summary>
    void OledRest();

    /// <summary>Why Focus mode cannot draw here; null when it can.</summary>
    string? FocusProblem();
}

/// <summary>Every <c>dispctrl-linux</c> command. Exit codes follow
/// <c>dispctrl.exe</c>: 0 done, 1 refused, 2 asked wrongly.</summary>
public static partial class CommandRunner
{
    public const int Done = 0, Refused = 1, AskedWrongly = 2;

    /// <summary>Every command's first word, for checking a hotkey's or a
    /// feature's steps before they are saved.</summary>
    internal static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "displays", "display", "status", "brightness", "dim", "nightlight", "night-light", "restore", "windows", "snap",
        "awake", "stayactive", "stay-active", "displays-off", "darkmode", "dark-mode", "monitor", "input", "preset", "presets",
        "tray", "unison", "pin", "placement", "features", "rgb", "lighting", "oled", "focus", "monitor-sleep", "roomlight", "wallpaper", "version", "settings", "apply", "topology",
    };

    private static readonly HashSet<string> BareFlags = new(StringComparer.Ordinal) { "--all", "--local", "--json", "--open", "--url", "--mail", "--mail-url", "--path" };

    public static string Version =>
        typeof(CommandRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static int Run(string[] args, CommandContext context)
    {
        if (args.Length == 0) return Usage(context);
        try
        {
            return args[0] switch
            {
                "doctor" => Doctor(context),
                "settings" => SettingsCommand(args[1..], context),
                "apply" => ApplyCommand(args[1..], context),
                "commands" => CommandsCommand(context),
                "topology" => DisplayCommand(["project", .. args[1..].Where(a => a != "--mode")], context),
                "displays" => Displays(context),
                "display" => DisplayCommand(args[1..], context),
                "status" => Status(context),
                "brightness" => Brightness(args[1..], context),
                "dim" => Dim(args[1..], context),
                "nightlight" or "night-light" => NightLight(args[1..], context),
                "restore" => Restore(context),
                "windows" => WindowsCommand(args[1..], context),
                "snap" => SnapCommand(args[1..], context),
                "awake" => AwakeCommand(args[1..], context),
                "unison" => UnisonCommand(args[1..], context),
                "pin" => PinCommand(args[1..], context),
                "hotkeys" => HotkeysCommand(args[1..], context),
                "features" => FeaturesCommand(args[1..], context),
                "rgb" or "lighting" => RgbCommand(args[1..], context),
                "oled" => OledCommand(args[1..], context),
                "focus" => FocusCommand(args[1..], context),
                "monitor-sleep" => MonitorSleepCommand(args[1..], context),
                "roomlight" or "room-light" => RoomLightCommand(args[1..], context),
                "update" => UpdateCommand(args[1..], context),
                "report" => ReportCommand(args[1..], context),
                "logs" => LogsCommand(args[1..], context),
                "wallpaper" => WallpaperCommand(args[1..], context),
                "triggers" => TriggersCommand(args[1..], context),
                "placement" => PlacementCommand(args[1..], context),
                "stayactive" or "stay-active" => StayActive(args[1..], context),
                "displays-off" => DisplaysOff(args[1..], context),
                "darkmode" or "dark-mode" => DarkModeCommand(args[1..], context),
                "monitor" => MonitorCommand(args[1..], context),
                "input" => InputCommand(args[1..], context),
                "preset" or "presets" => PresetCommand(args[1..], context),
                "tray" => TrayCommand(args[1..], context),
                "version" or "--version" => Write(context.Out, $"dispctrl-linux {Version}"),
                "help" or "--help" or "-h" => Usage(context),
                _ => Wrong(context, $"unknown command '{args[0]}' (dispctrl-linux help lists them)"),
            };
        }
        catch (IOException ex)
        {
            AppLog.Warn("command", $"'{args[0]}' failed: {ex.Message}");
            context.Err.WriteLine($"dispctrl-linux: {ex.Message}");
            return Refused;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Warn("command", $"'{args[0]}' failed: {ex.Message}");
            context.Err.WriteLine($"dispctrl-linux: {ex.Message}");
            return Refused;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever was not foreseen is answered, and logged for the problem
            // report, rather than ending the engine's connection or the window.
            AppLog.Error("command", $"'{args[0]}' failed unexpectedly", ex);
            context.Err.WriteLine($"dispctrl-linux: '{args[0]}' failed unexpectedly ({ex.GetType().Name}: {ex.Message}); the details are in the log (dispctrl-linux logs)");
            return Refused;
        }
    }

    public const string UsageText = """
        dispctrl-linux - per-monitor brightness, dimming, night light, arrangement and presets on Linux (X11)

          displays                                 outputs, DDC/CI monitors and backlight devices
          status                                   the engine, night light, dimming and every ramp
          doctor                                   what is installed, permitted and in the way

          brightness <0-100> --ddc <n>             a DDC/CI monitor's own brightness (ddcutil display number)
          brightness <0-100> --backlight <name>    a built-in panel's backlight (/sys/class/backlight)
          brightness <0-100> --all                 every display's own brightness
          brightness up | down [step]              every display, 5% a step (through unison when it is on)
          unison [on | off | <0-100>]              one level for every display, each within its own range
          unison up | down [step]
          unison range <display> <min> <max>       a display's own lowest and highest (unison lists the names)
          unison include | exclude <display>
          unison calibrate start | capture | cancel | status   walk each display to its dimmest, then brightest, by eye
          roomlight [on | off] [--min N --max N]   unison follows the ambient light sensor (iio-sensor-proxy)
          dim <0.1-1> --output <name> | --all      software dimming through the output's gamma ramp
          dim off [--output <name> | --all]        no software dimming

          nightlight                               night light now and its settings
          nightlight on | off | toggle             switch it
          nightlight <0-100>                       strength (6500 K at 0 to 1900 K at 100), and on
          nightlight --from HH:MM --to HH:MM       follow a schedule (the engine applies it)
          nightlight --schedule off                warm whenever it is on

          restore                                  night light off, no dimming, every ramp back to normal
          darkmode [on | off | toggle]             the desktop's dark theme (GNOME, KDE, XFCE)
          darkmode schedule on|off                 dark during night light's hours, light outside them

          monitor [list]                           DDC/CI monitors and their display numbers
          monitor <n>                              the controls a monitor lists, with their values
          monitor <n> set <control> <value>        contrast, volume, colour-preset, picture-mode, red ... or 0x12;
                                                   a listed value by name (picture-mode movie). Never resets or power
          input <n> [<source>]                     the input sources it lists, or switch to one (input 1 HDMI-1)

          awake [on | off | toggle]                keep the computer awake (the engine holds it)
          awake for 30m | 2h | 1h30m               for a while
          awake until HH:MM                        until a time
          awake displays on|off                    also keep the displays on and the screen unlocked
          stayactive on|off|toggle                 nudge the pointer a pixel after 55 s still (a mouse jiggler)
          displays-off                             every display off now; any key or the mouse wakes them
          oled [on | off | toggle | rest]          OLED care: dim when idle, in two stages; rest dims now
               [--idle-minutes N --dim N --second-stage on|off --second-minutes N --second-dim N]
               [--pause-fullscreen on|off --pause-media on|off]
          focus [on | off | toggle] [--dim 10-90]  dim everything but the window in front
          monitor-sleep [on | off] [--minutes N]   the displays off after a while idle

          preset [list]                            saved desks, and whether each fits the displays attached
          preset save NAME [--skip PARTS]          the desk now: layout, brightness, nightlight, theme, controls, lighting
          preset apply NAME                        put it back
          preset show NAME | delete NAME | rename OLD NEW
          preset set NAME --skip PARTS|none        the parts it leaves alone
          preset suggested | add NAME | add all    ready-made ones: Day, Evening, Night, Reading, Movie, Gaming ...
          preset desk NAME on|off                  apply by itself when exactly its displays connect
          preset rule add APP PRESET [--then previous|keep|PRESET] [--delay S]   while an app is in front
          preset rule [list] | remove N
          preset launch NAME PROGRAM [ARGS] [--keep]   apply, run, put the desk back when it ends

          rgb [list]                               keyboard, mouse, fan and strip lighting, through OpenRGB's SDK server
          rgb colour <#RRGGBB|name> [--device N]   one colour (warm, white, red, blue, purple ...)
          rgb key <NAME[,NAME...]> <colour> [--device N]   light single keys: a, "left shift", F5, "led 12" (keyboards with a light per key)
          rgb keys <NAME=COLOUR,...|FILE> [--device N]     several keys at once; a file holds one NAME=COLOUR per line
          rgb look list | NAME [--device N]                       ready-made key colourings: Productivity, Coding, Gaming, Focus, Meow
          rgb preset list|save|apply|delete <NAME> [--device N]   per-key colour sets for a keyboard, saved by name
          rgb order <RGB|GRB|BGR|...> [--device N]  the colour order a device's LEDs take, when a colour shows as another (red as green)
          rgb off | rgb mode <effect> [--device N] off, or one of the device's own effects (Rainbow Wave ...)
          rgb server HOST[:PORT]                   where OpenRGB listens (127.0.0.1:6742)
          rgb start                                start OpenRGB's server headless (found on PATH, ~/.local/bin, Flatpak, snap or our own copy)
          rgb setup                                download the official OpenRGB AppImage (SHA-256 checked) if none is found, then start it
          rgb udev                                 whether OpenRGB's udev rules are installed, and the one-line root command if not
          rgb autostart on|off                     start OpenRGB by itself when a lighting command finds it stopped (on)
          (without OpenRGB, a laptop's keyboard backlight in /sys/class/leds is used: colour where it has one, else brightness)

          features [list]                          named steps: commands, wait MS, run PROGRAM
          features add NAME "STEP; STEP; ..."      e.g. features add Late "nightlight 80; dim 0.6 --all"
          features run NAME [--dry-run] | remove NAME
          triggers [list]                          when something happens, run a feature (the engine watches)
          triggers add EVENT FEATURE [--match X] [--minutes N]   display-connected, display-disconnected,
                                                   app-in-front, app-left, idle, back, locked, unlocked,
                                                   on-battery, on-power, resumed, at-time (--match 22:30)
          triggers remove | on | off N

          display [list]                           each lit output: main, mode, position, size
          display set --output <name> --x <n> --y <n>   move one output (others stay; the desktop is
                                                   shifted so it starts at 0,0)
          display set --output <name> --primary true    make it the main display
          display arrange <output>=<x>,<y> ...     the whole layout in one change
          display arrange reset                    side by side, top edges aligned
          display modes [--output <name>]          the resolutions and refresh rates each offers
          display set --output <name> --mode 1920x1080 [--rate 60]   resolution and refresh rate
          display set --output <name> --rotate normal|left|right|inverted
          display set --output <name> --enabled true|false   light it, or switch it off
          display project extend | duplicate | internal | external   Win+P's four arrangements
          display only <output>                    that display alone

          windows                                  the open windows, front first, with their ids
          pin [on | off | toggle] [--window <id>]  keep a window above the others (the one in front by default)
          pin list | pin off --all
          placement gather [--to N|pointer|active] every window onto one display
          placement move [--window <id>] [--to next|previous|N]   a window onto another display
          placement span [--window <id>] [--displays 1,2]          a window across displays
          snap                                     Snap layouts and Snap Assist: state and settings
          snap on | off                            switch them (drag to the top, the shortcut, Assist)
          snap shortcut <keys> | none              the shortcut, e.g. Super+Z or Ctrl+Alt+S
          snap drag on|off   snap assist on|off    each part on its own
          snap gap <0-32>                          pixels between snapped windows
          snap pick                                open the layouts for the window in front (needs the engine)
          snap <layout> <zone> [--window <id>]     place a window (the active one by default) in a zone
          snap layouts                             the layouts each display offers
          tray [on | off]                          the tray icon (kept by dispctrl-linux-gui --tray)
          tray click panel | window                what a left click on it opens
          tray show                                open the quick panel

          hotkeys [list]                           global shortcuts and the command each runs (the engine holds them)
          hotkeys add <keys> <command...>          e.g. hotkeys add Ctrl+Alt+M preset apply Movie
          hotkeys remove | on | off <keys|N>       hotkeys reset: the defaults
          wallpaper <picture>                      the desktop's picture (GNOME, KDE, XFCE)
          update [check]                           whether a newer release is out (one request, nothing downloaded)
          report [--what "..."] [--open]           diagnostics and the log's end for a problem report, serials and your name removed
          report --url | --mail | --mail-url       the GitHub issue's address; an email to the author (opened for you to read and send)
          report --save [FILE]                     the whole redacted report and log in a file to attach (default: your home folder)
          logs [--tail N] [--path] [--open]        the last lines of the log, where it is kept, or its folder opened
          settings [get] [--path /unison/level]    the saved settings, or one of them
          settings set --path P --value JSON | reset [--path P]   change one, or put it back to its default
          settings export [--output FILE] | validate FILE | import FILE
          apply FILE [--dry-run]                   a file of commands, one a line, run in order (# for comments)
          commands                                 every command's first word
          topology extend | duplicate | internal | external   the same as display project
          engine [run] | status | stop             the resident engine (docs/LINUX.md)
          version

        A command goes to the running engine when there is one; --local runs it here.
        Exit codes: 0 done, 1 refused, 2 asked wrongly.
        """;

    private static int Usage(CommandContext context) => Write(context.Out, UsageText);

    private static int Doctor(CommandContext c)
    {
        int problems = 0;
        void Line(bool ok, string name, string detail, string? fix = null)
        {
            c.Out.WriteLine($"[{(ok ? "ok" : "--")}]  {name,-16} {detail}");
            if (!ok && fix is not null) c.Out.WriteLine($"      {fix}");
            if (!ok) problems++;
        }

        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
        Line(session == "x11", "session", session,
            session == "wayland" ? "Ramps need an X11 session; Wayland compositors keep gamma to themselves (log in with \"Ubuntu on Xorg\" or similar)." : null);

        bool x = GammaRamp.IsAvailable(out var xReason);
        Line(x, "X display", x ? $"{GammaRamp.Outputs().Count} output(s) with a gamma ramp" : xReason!, "Night light and dimming need it; brightness does not.");

        Line(Ddcutil.IsAvailable, "ddcutil", Ddcutil.IsAvailable ? Shell.Which("ddcutil")! : Distro.InSnap ? "not in the snap" : "not found",
            Distro.InSnap ? $"External monitors' own brightness: {Distro.DdcutilAdvice}."
                : $"{Distro.InstallCommand([Distro.Ddcutil])}   (only for external monitors' own brightness)");

        var missing = Distro.Missing();
        Line(missing.Count == 0, "libraries",
            missing.Count == 0 ? $"every one DispCtrl loads ({Distro.Libraries.Count})"
                : string.Join("; ", missing.Select(l => $"{l.Soname} missing ({l.NeededFor})")),
            Distro.InstallCommand(missing));

        var buses = System.IO.Directory.Exists("/dev") ? System.IO.Directory.GetFiles("/dev", "i2c-*") : [];
        if (buses.Length == 0)
        {
            Line(false, "/dev/i2c-*", "none", "sudo modprobe i2c-dev, and add it to /etc/modules-load.d to keep it");
        }
        else
        {
            int writable = buses.Count(b => CanReadWrite(b));
            Line(writable > 0, "/dev/i2c-*", $"{buses.Length} bus(es), {writable} this account may use",
                "Log in again after installing ddcutil: its udev rule gives the logged-in user the buses. Where it has none, add one, or: sudo groupadd -f i2c; sudo usermod -aG i2c $USER");
        }

        var backlights = Backlight.Enumerate();
        if (backlights.Count == 0)
        {
            c.Out.WriteLine($"[ok]  {"backlight",-16} none (normal on a desktop)");
        }
        else
        {
            foreach (var b in backlights)
            {
                bool w = CanReadWrite($"/sys/class/backlight/{b.Name}/brightness");
                bool logind = !w && new[] { "busctl", "dbus-send", "gdbus" }.Any(Shell.TryWhich);
                Line(w || logind, "backlight", $"{b.Name} {(w ? "writable" : logind ? "through systemd-logind (active local session)" : "read-only for this account")}",
                    "Install the backlight udev rule and join the video group (docs/LINUX.md, \"Backlight\").");
            }
        }

        if (TilingAssistantOn())
        {
            c.Out.WriteLine($"[ok]  {"tiling",-16} Ubuntu's Tiling Assistant is on too: its edge snapping keeps working beside DispCtrl's layouts, which open at the top centre, below the edge it uses");
        }

        if (GnomeNightLightOn())
        {
            Line(false, "GNOME night light", "on", "Switch it off in Settings > Displays: two night lights write the same ramp and the last one wins.");
        }

        bool engine = c.InEngine || EngineClient.IsRunning();
        Line(engine, "engine", engine ? $"running ({Runtime.SocketPath})" : "not running",
            $"{Distro.EngineStartCommand}   (the schedule and hot-plug need it)");
        var others = Installs.Conflicts(Installs.Probe.Current(), engine);
        Line(others.Count == 0, "one install", others.Count == 0 ? "no other DispCtrl install found" : others[0].Evidence,
            others.Count == 0 ? null : $"Two installs fight over the ramps, hotkeys and tray. Remove the other: {others[0].Remedy}");
        c.Out.WriteLine($"[ok]  {"settings",-16} {SettingsStore.FilePath}");

        c.Out.WriteLine();
        c.Out.WriteLine(problems == 0 ? "Nothing in the way." : $"{problems} thing(s) to look at.");
        return Done;
    }

    private static int Displays(CommandContext c)
    {
        if (XRandR.IsAvailable)
        {
            c.Out.WriteLine("Outputs (XRandR)");
            var outputs = XRandR.Query();
            if (outputs.Count == 0) c.Out.WriteLine("  none - RandR reported no outputs");
            foreach (var output in outputs)
            {
                c.Out.WriteLine(output.Connected
                    ? $"  {output.Name,-10} {(output.Primary ? "primary" : "       ")} {output.WidthPx}x{output.HeightPx}+{output.PosX}+{output.PosY}  {output.WidthMm} x {output.HeightMm} mm"
                    : $"  {output.Name,-10} disconnected");
            }
        }
        else
        {
            c.Out.WriteLine("Outputs: no X display");
        }

        c.Out.WriteLine();
        if (Ddcutil.IsAvailable)
        {
            c.Out.WriteLine("DDC/CI monitors (ddcutil)");
            var monitors = Ddcutil.Detect();
            if (monitors.Count == 0)
                c.Out.WriteLine("  none answered - a built-in panel never does; it has no DDC/CI channel");
            foreach (var m in monitors)
            {
                var level = Ddcutil.GetVcp(m.DisplayNum, Ddcutil.VcpBrightness);
                var brightness = level is null ? "brightness unreadable" : $"brightness {level.Current}/{level.Maximum ?? 100}";
                c.Out.WriteLine($"  {m.DisplayNum,-3} {m.Model ?? "(model unknown)",-20} {m.I2CBus,-12} {brightness}");
            }
        }
        else
        {
            c.Out.WriteLine("DDC/CI monitors: ddcutil not found");
        }

        c.Out.WriteLine();
        c.Out.WriteLine("Backlight (/sys/class/backlight)");
        var backlights = Backlight.Enumerate();
        if (backlights.Count == 0) c.Out.WriteLine("  none");
        foreach (var b in backlights)
            c.Out.WriteLine($"  {b.Name,-14} {Percent(b.Fraction)}%  ({b.Current}/{b.Max})");
        return Done;
    }

    private static int Status(CommandContext c)
    {
        var settings = SettingsStore.Load();
        var now = TimeOnly.FromDateTime(DateTime.Now);
        bool engine = c.InEngine || EngineClient.IsRunning();
        c.Out.WriteLine($"engine       {(engine ? "running" : "not running")}");
        WriteNightLight(c.Out, settings, now, engine);
        foreach (var output in GammaRamp.Outputs())
        {
            var target = RampTarget.For(settings, output.Name, now);
            var actual = GammaRamp.Read(output.Name);
            string dim = settings.Dim.TryGetValue(output.Name, out var d) ? $"dim {Percent(d)}%" : "no dimming";
            string ramp = actual is { } p ? $"ramp {p.Red:0.000} {p.Green:0.000} {p.Blue:0.000}" : "ramp unreadable";
            string drift = actual is { } a && !target.Matches(a) ? "  (not what the settings ask for)" : "";
            c.Out.WriteLine($"{output.Name,-12} {dim,-14} {ramp}{drift}");
        }
        return Done;
    }

    private static int Brightness(string[] args, CommandContext c)
    {
        if (args.Length == 0) return Wrong(c, "brightness needs a value and --ddc <n>, --backlight <name> or --all; or up|down");
        if (args[0] is "up" or "down" || args.Skip(1).SequenceEqual(["--all"])) return BrightnessAll(args.Where(a => a != "--all").ToArray(), c);
        string value = args[0];
        if (!TryFlags(args[1..], c, out var flags)) return AskedWrongly;

        if (flags.TryGetValue("--xrandr", out var xrandrOutput))
        {
            // The first Linux build's spelling of software dimming.
            return Dim([value, "--output", xrandrOutput], c);
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) || pct is < 0 or > 100)
            return Wrong(c, "brightness must be a whole number from 0 to 100");

        if (flags.TryGetValue("--ddc", out var ddc))
        {
            if (!int.TryParse(ddc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var displayNum) || displayNum < 1)
                return Wrong(c, "--ddc needs ddcutil's display number (dispctrl-linux displays lists them)");
            if (!Ddcutil.IsAvailable) return Refuse(c, Distro.DdcutilAdvice);
            if (!Ddcutil.SetVcp(displayNum, Ddcutil.VcpBrightness, pct, out var error))
                return Refuse(c, $"display {displayNum} refused brightness: {error}");
            c.Out.WriteLine($"display {displayNum}: brightness {pct}");
            return Done;
        }

        if (flags.TryGetValue("--backlight", out var name))
        {
            var device = Backlight.Enumerate().FirstOrDefault(b => b.Name == name);
            if (device is null) return Wrong(c, $"no backlight device '{name}' (dispctrl-linux displays lists them)");
            int raw = (int)Math.Round(device.Max * (pct / 100.0));
            if (!Backlight.TrySet(name, raw, out var error)) return Refuse(c, error!);
            c.Out.WriteLine($"{name}: brightness {pct}% ({raw}/{device.Max})");
            return Done;
        }

        return Wrong(c, "brightness needs --ddc <n>, --backlight <name> or --all");
    }

    private static int Dim(string[] args, CommandContext c)
    {
        if (args.Length == 0) return Wrong(c, "dim needs a value from 0.1 to 1, or off");
        string value = args[0];
        if (!TryFlags(args[1..], c, out var flags)) return AskedWrongly;

        double level;
        if (value == "off") level = 1;
        else if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out level))
            return Wrong(c, "dim must be a number from 0.1 to 1 (or a percentage such as 70%), or off");
        if (value.EndsWith('%')) level /= 100;
        if (level is < RampTarget.LowestDim or > 1)
            return Wrong(c, $"dim must be from {RampTarget.LowestDim} to 1: lower is a black screen with no way back to this control");

        if (!GammaRamp.IsAvailable(out var reason)) return Refuse(c, $"cannot dim: {reason}");
        var outputs = GammaRamp.Outputs().Select(o => o.Name).ToList();

        List<string> targets;
        if (flags.ContainsKey("--all")) targets = outputs;
        else if (flags.TryGetValue("--output", out var output))
        {
            if (!outputs.Contains(output)) return Wrong(c, $"no connected output '{output}' (connected: {string.Join(", ", outputs)})");
            targets = [output];
        }
        else if (value == "off") targets = outputs;
        else return Wrong(c, "dim needs --output <name> or --all");

        SettingsStore.Update(s =>
        {
            foreach (var t in targets)
            {
                if (level >= 1) s.Dim.Remove(t);
                else s.Dim[t] = level;
            }
        });
        if (ReportRamps(c) is { } failed) return failed;
        foreach (var t in targets) c.Out.WriteLine(level >= 1 ? $"{t}: no dimming" : $"{t}: dimmed to {Percent(level)}%");
        return Done;
    }

    private static int NightLight(string[] args, CommandContext c)
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        if (args.Length == 0 || args[0] == "status")
        {
            WriteNightLight(c.Out, SettingsStore.Load(), now, c.InEngine || EngineClient.IsRunning());
            return Done;
        }

        bool? enable = null;
        int? strength = null;
        string[] rest = args;
        switch (args[0])
        {
            case "on" or "true": enable = true; rest = args[1..]; break;
            case "off" or "false": enable = false; rest = args[1..]; break;
            case "toggle": enable = !SettingsStore.Load().NightLight.Enabled; rest = args[1..]; break;
            default:
                if (!args[0].StartsWith("--", StringComparison.Ordinal))
                {
                    if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) || s is < 0 or > 100)
                        return Wrong(c, "night light strength must be a whole number from 0 to 100, or on/off");
                    strength = s;
                    enable = true;
                    rest = args[1..];
                }
                break;
        }

        if (!TryFlags(rest, c, out var flags)) return AskedWrongly;
        TimeOnly? from = null, to = null;
        bool? scheduled = null;
        if (flags.TryGetValue("--from", out var fromText))
        {
            if (!Schedule.TryParse(fromText, out var f)) return Wrong(c, "--from must be a time such as 20:00");
            from = f;
        }
        if (flags.TryGetValue("--to", out var toText))
        {
            if (!Schedule.TryParse(toText, out var t)) return Wrong(c, "--to must be a time such as 07:00");
            to = t;
        }
        if (from is not null || to is not null) scheduled = true;
        if (flags.TryGetValue("--schedule", out var sch))
        {
            if (sch is "on" or "true") scheduled = true;
            else if (sch is "off" or "false") scheduled = false;
            else return Wrong(c, "--schedule takes on or off");
        }
        if (enable is null && strength is null && scheduled is null)
            return Wrong(c, "nightlight takes on, off, a strength from 0 to 100, --from/--to or --schedule");

        var current = SettingsStore.Load().NightLight;
        Schedule.TryParse(current.From, out var curFrom);
        Schedule.TryParse(current.To, out var curTo);
        if ((from ?? curFrom) == (to ?? curTo))
            return Wrong(c, "the schedule cannot start and end at the same time");

        bool wayland = GammaRamp.OnWayland;
        if (wayland && !Session.DesktopNightLight.Available)
            return Refuse(c, "on Wayland night light goes through the desktop's own (GNOME's or KDE's), and this desktop is neither");
        if (!wayland && !GammaRamp.IsAvailable(out var reason) && enable == true)
            return Refuse(c, $"cannot warm the screen: {reason}");

        var saved = SettingsStore.Update(s =>
        {
            if (enable is bool e) s.NightLight.Enabled = e;
            if (strength is int st) s.NightLight.Strength = st;
            if (scheduled is bool sc) s.NightLight.Scheduled = sc;
            if (from is TimeOnly fr) s.NightLight.From = Schedule.Format(fr);
            if (to is TimeOnly tt) s.NightLight.To = Schedule.Format(tt);
        });
        if (wayland)
        {
            // The compositor owns the ramps: the desktop's own night light it is.
            if (Session.DesktopNightLight.Apply(saved.NightLight) is { } refused) return Refuse(c, refused);
            WriteNightLight(c.Out, saved, now, engine: true);
            c.Out.WriteLine("through      the desktop's own night light (Wayland)");
            return Done;
        }
        if (ReportRamps(c) is { } failed) return failed;

        bool engine = c.InEngine || EngineClient.IsRunning();
        WriteNightLight(c.Out, saved, now, engine);
        return Done;
    }

    private static int Restore(CommandContext c)
    {
        SettingsStore.Update(s =>
        {
            s.NightLight.Enabled = false;
            s.Dim.Clear();
        });
        // Every output, not only the ones DispCtrl remembers warming: this is
        // the command to give someone looking at a dark or orange screen.
        foreach (var output in GammaRamp.Outputs())
        {
            if (!GammaRamp.Reset(output.Name, out var error)) c.Err.WriteLine($"{output.Name}: {error}");
            else c.Out.WriteLine($"{output.Name}: back to normal");
        }
        Runtime.SaveOwnedRamps([]);
        c.ApplyRamps();
        c.Out.WriteLine("Night light off, no dimming.");
        return Done;
    }

    private static void WriteNightLight(TextWriter o, LinuxSettings settings, TimeOnly now, bool engine)
    {
        var n = settings.NightLight;
        string state = !n.Enabled ? "off" : n.ActiveAt(now) ? "on, warming now" : "on, outside its hours";
        o.WriteLine($"night light  {state}");
        o.WriteLine($"strength     {n.Strength} ({Warmth.KelvinFor(n.Strength):0} K)");
        o.WriteLine(n.Scheduled ? $"schedule     {n.From} to {n.To}" : "schedule     none (warm whenever on)");
        if (n.Enabled && n.Scheduled && !engine)
            o.WriteLine($"note         the engine is not running, so nothing will follow the schedule: {Distro.EngineStartCommand}");
    }

    /// <summary>Applies the ramps after a settings change; a write that failed is
    /// reported and refuses the command, since the setting did not take.</summary>
    private static int? ReportRamps(CommandContext c)
    {
        var failed = c.ApplyRamps().Where(r => !r.Written).ToList();
        foreach (var f in failed) c.Err.WriteLine($"{f.Output}: {f.Error}");
        return failed.Count > 0 ? Refused : null;
    }

    private static bool TryFlags(string[] args, CommandContext c, out Dictionary<string, string> flags)
    {
        flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                Wrong(c, $"unexpected '{arg}'");
                return false;
            }
            if (BareFlags.Contains(arg)) { flags[arg] = "true"; continue; }
            if (i + 1 >= args.Length)
            {
                Wrong(c, $"{arg} needs a value");
                return false;
            }
            flags[arg] = args[++i];
        }
        return true;
    }

    private static bool CanReadWrite(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TilingAssistantOn()
    {
        if (!Shell.TryWhich("gsettings")) return false;
        var result = Shell.Run("gsettings", ["get", "org.gnome.shell", "enabled-extensions"], 3000);
        return result.Ok && result.Stdout.Contains("tiling-assistant@ubuntu.com", StringComparison.Ordinal);
    }

    private static bool GnomeNightLightOn()
    {
        if (!Shell.TryWhich("gsettings")) return false;
        var result = Shell.Run("gsettings", ["get", "org.gnome.settings-daemon.plugins.color", "night-light-enabled"], 3000);
        return result.Ok && result.Stdout.Trim() == "true";
    }

    private static int Percent(double fraction) => (int)Math.Round(fraction * 100);

    private static int Write(TextWriter o, string text) { o.WriteLine(text); return Done; }

    private static int Wrong(CommandContext c, string message)
    {
        AppLog.Debug("command", $"asked wrongly: {message}");
        c.Err.WriteLine($"dispctrl-linux: {message}");
        return AskedWrongly;
    }

    private static int Refuse(CommandContext c, string message)
    {
        // Every hardware refusal passes through here, so the log keeps the reason
        // a command did nothing.
        AppLog.Warn("command", $"refused: {message}");
        c.Err.WriteLine($"dispctrl-linux: {message}");
        return Refused;
    }
}
