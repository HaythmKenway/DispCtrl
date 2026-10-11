using System.Diagnostics;
using System.Text.Json;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.Snap;
using DispCtrl.Linux.X11;

// Never the real desk: an X display that cannot answer, and scratch settings
// and runtime folders. The runtime folder sits in /tmp because a socket path
// over 107 characters cannot be bound at all.
GammaRamp.DisplayName = ":65000";
var scratch = Directory.CreateTempSubdirectory("dcl.").FullName;
var config = Path.Combine(scratch, "config");
var runtime = Path.Combine(scratch, "run");
Directory.CreateDirectory(runtime);
File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
Environment.SetEnvironmentVariable("DISPCTRL_LINUX_CONFIG_DIR", config);
Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtime);

int failed = 0;
void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failed++;
}

try
{
    Parsing();
    ArrangementChecks();
    DistributionChecks();
    SnapGeometryChecks();
    WarmthAndSchedule();
    Targets();
    SettingsFile();
    Commands();
    Client();
    EngineProcess();
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
}

Console.WriteLine(failed == 0 ? "All Linux checks passed." : $"{failed} Linux check(s) failed.");
return failed;

void Parsing()
{
    // ddcutil detect --brief, as this desk printed it: a Dell, then the eDP
    // panel's "Invalid display" block, which used to overwrite the Dell's fields.
    const string detect = """
        Display 1
           I2C bus:          /dev/i2c-5
           Monitor:          DEL:DELL P2723DE:22WB714

        Invalid display
           I2C bus:          /dev/i2c-8
           DRM connector:    card2-eDP-1
           Monitor:          CMN::

        """;
    var monitors = Ddcutil.ParseDetect(detect);
    Check(monitors.Count == 1, "ddcutil: an Invalid display block is not a monitor");
    Check(monitors[0] is { DisplayNum: 1, Model: "DELL P2723DE", Serial: "22WB714", I2CBus: "/dev/i2c-5" },
        "ddcutil: the Invalid display block does not overwrite the monitor before it");
    Check(Ddcutil.ParseVcp("VCP 10 C 52 100\n") == new DdcVcpValue(52, 100), "ddcutil: --brief reading with a maximum");
    Check(Ddcutil.ParseVcp("VCP code 0x10 (Brightness ): current value = 70, max value = 100") is null,
        "ddcutil: the prose form is not mistaken for a reading");

    var echo = Shell.Run("printf", ["%s|", "DP-1 --off", "x"]);
    Check(echo.Ok && echo.Stdout == "DP-1 --off|x|", "shell: an argument with spaces stays one argument");
    Check(Shell.Run("dispctrl-no-such-tool", []).ExitCode == -1, "shell: a missing tool is reported, not thrown");
    Check(Shell.Run("sleep", ["5"], timeoutMs: 200).ExitCode == -2, "shell: a tool that hangs is stopped at its timeout");

    Check(!Backlight.TrySet("../../../tmp", 1, out var error) && error!.Contains("no backlight device", StringComparison.Ordinal),
        "backlight: a name that is not a device never reaches a path");
}

void ArrangementChecks()
{
    // This desk: the Dell above, the laptop panel below it and a little right;
    // then a portrait monitor, and one connected but switched off.
    List<XRandROutput> outputs =
    [
        new("eDP", true, true, 1920, 1080, 333, 1440, 344, 193, 0, 144),
        new("DP-1-0", true, false, 2560, 1440, 0, 0, 597, 336, 0, 59.95),
        new("HDMI-A-0", true, false, 1080, 1920, 2560, 0, 527, 296, 90, 60),
        new("DP-1-1", true, false, 0, 0, 0, 0, 0, 0),
    ];
    Check(outputs[3] is { Connected: true, Active: false }, "xrandr: connected but switched off is not lit");
    var portrait = DesktopLayout.Physical(outputs[2], 0, 0, physical: true);
    Check(Math.Abs(portrait.WidthMm - 296) < 0.01 && Math.Abs(portrait.HeightMm - 527) < 0.01,
        "arrangement: a portrait monitor is drawn tall at its real size");

    var desk = outputs.Take(2).ToList();
    Check(DesktopLayout.Plan(desk, new Dictionary<string, (int, int)> { ["eDP"] = (0, 0) }, out _) is { } overlap
        && overlap.Contains("overlap", StringComparison.Ordinal), "arrangement: an overlap is refused");
    Check(DesktopLayout.Plan(desk, new Dictionary<string, (int, int)> { ["eDP"] = (5000, 0) }, out _) is { } gap
        && gap.Contains("touch", StringComparison.Ordinal), "arrangement: a gap is refused");
    Check(DesktopLayout.Plan(desk, new Dictionary<string, (int, int)> { ["eDP"] = (-1920, 0) }, out var left) is null
        && left["eDP"] == (0, 0) && left["DP-1-0"] == (1920, 0),
        "arrangement: a position left of 0 shifts the whole desktop, as X has no negative coordinates");
    Check(DesktopLayout.Plan(outputs, new Dictionary<string, (int, int)> { ["DP-1-1"] = (0, 0) }, out _) is { } off
        && off.Contains("switched off", StringComparison.Ordinal), "arrangement: an output that is off cannot be placed");
    var side = DesktopLayout.SideBySide(desk);
    Check(side["DP-1-0"] == (0, 0) && side["eDP"] == (2560, 0), "arrangement: reset is left to right, tops aligned");

    Check(XRandR.ScreenFor([(0, 0, 2560, 1440), (333, 1440, 1920, 1080)]) == (2560, 2520),
        "xrandr: the screen reaches the furthest right and bottom edges, from 0,0");
    Check(XRandR.ScreenFor([(2560, 0, 1920, 1080), (0, 0, 2560, 1440)]) == (4480, 1440),
        "xrandr: side by side, the screen is both widths");

    // The monitor name descriptor, as the Dell's EDID carries it.
    var edid = new byte[128];
    byte[] descriptor = [0, 0, 0, 0xFC, 0, .. "DELL P2723DE\n"u8];
    descriptor.CopyTo(edid, 72);
    Check(XRandR.EdidName(edid) == "DELL P2723DE", "edid: the monitor name descriptor");
    Check(XRandR.EdidName(new byte[128]) is null, "edid: a panel that names nothing has no name");
}

void DistributionChecks()
{
    // ID and ID_LIKE as these distributions write them.
    Check(Distro.Detect("NAME=\"Linux Mint\"\nID=linuxmint\nID_LIKE=\"ubuntu debian\"\n") == DistroFamily.Debian, "distro: Mint is Debian's family");
    Check(Distro.Detect("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n") == DistroFamily.Fedora, "distro: Rocky is Fedora's family");
    Check(Distro.Detect("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n") == DistroFamily.Suse, "distro: Tumbleweed is SUSE");
    Check(Distro.Detect("ID=endeavouros\nID_LIKE=arch\n") == DistroFamily.Arch, "distro: EndeavourOS is Arch's family");
    Check(Distro.Detect("ID=alpine\n") == DistroFamily.Alpine && Distro.Detect("ID=void\n") == DistroFamily.Void,
        "distro: Alpine and Void");
    Check(Distro.Detect("") == DistroFamily.Unknown, "distro: no os-release is unknown, not a guess");

    // The snap has no systemd unit: its engine is an app of its own.
    var snapName = Environment.GetEnvironmentVariable("SNAP_NAME");
    try
    {
        Environment.SetEnvironmentVariable("SNAP_NAME", "dispctrl-linux");
        Check(Distro.EngineStartCommand.Contains("dispctrl-linux.engine") && !Distro.EngineStartCommand.Contains("systemctl")
            && Distro.EngineStartsAtSignIn, "distro: the snap starts its engine app, not a systemd unit it does not have");
    }
    finally { Environment.SetEnvironmentVariable("SNAP_NAME", snapName); }

    var xrandr = Distro.Libraries.First(l => l.Soname == "libXrandr.so.2");
    Check(Distro.PackageOf(xrandr, DistroFamily.Debian) == "libxrandr2" && Distro.PackageOf(xrandr, DistroFamily.Fedora) == "libXrandr"
        && Distro.PackageOf(xrandr, DistroFamily.Suse) == "libXrandr2" && Distro.PackageOf(xrandr, DistroFamily.Arch) == "libxrandr",
        "distro: each family's own name for libXrandr");
    Check(Distro.Libraries.All(l => l.Packages.Length == 7), "distro: every library names a package for all seven families");

    var calls = Backlight.LogindCalls("amdgpu_bl2", "50").ToList();
    Check(calls.Select(c => c.Tool).SequenceEqual(["busctl", "dbus-send", "gdbus"]),
        "backlight: logind through busctl, then dbus-send, then gdbus (systemd-less distributions)");
    Check(calls[1].Args.Contains("uint32:50") && calls[2].Args.Contains("'amdgpu_bl2'") && calls[2].Args.Contains("uint32 50"),
        "backlight: dbus-send and gdbus spell the arguments their own way");

    Environment.SetEnvironmentVariable("SNAP_NAME", "dispctrl-linux");
    Check(Distro.InSnap && Distro.DdcutilAdvice.Contains("snap cannot reach", StringComparison.Ordinal),
        "snap: DDC/CI is said to be unavailable, not to need installing");
    Environment.SetEnvironmentVariable("SNAP_NAME", null);
}

void WarmthAndSchedule()
{
    var neutral = Warmth.Multipliers(Warmth.NeutralKelvin);
    Check(Math.Abs(neutral.R - 1) < 1e-9 && Math.Abs(neutral.G - 1) < 1e-9 && Math.Abs(neutral.B - 1) < 1e-9,
        "warmth: 6500 K is exactly identity");
    Check(Warmth.KelvinFor(0) == 6500 && Warmth.KelvinFor(100) == 1900 && Warmth.KelvinFor(150) == 1900,
        "warmth: strength spans 6500 K to 1900 K and is clamped");
    bool warmer = true;
    var previous = neutral;
    for (int s = 10; s <= 100; s += 10)
    {
        var m = Warmth.Multipliers(Warmth.KelvinFor(s));
        warmer &= m.R == 1 && m.G <= previous.G && m.B < previous.B;
        previous = m;
    }
    Check(warmer, "warmth: more strength never brightens green or blue, and red stays whole");

    TimeOnly T(int h, int m) => new(h, m);
    Check(Schedule.IsActive(T(23, 0), T(20, 0), T(7, 0)) && Schedule.IsActive(T(6, 59), T(20, 0), T(7, 0))
        && !Schedule.IsActive(T(7, 0), T(20, 0), T(7, 0)) && !Schedule.IsActive(T(12, 0), T(20, 0), T(7, 0)),
        "schedule: a window across midnight");
    Check(Schedule.IsActive(T(9, 0), T(9, 0), T(17, 0)) && !Schedule.IsActive(T(17, 0), T(9, 0), T(17, 0)),
        "schedule: a window within the day starts inclusive and ends exclusive");
    Check(!Schedule.IsActive(T(9, 0), T(9, 0), T(9, 0)), "schedule: equal ends are empty");
    var now = new DateTime(2026, 10, 4, 21, 30, 0);
    Check(Schedule.NextBoundary(now, T(20, 0), T(7, 0)) == new DateTime(2026, 10, 5, 7, 0, 0)
        && Schedule.NextBoundary(now.Date.AddHours(12), T(20, 0), T(7, 0)) == new DateTime(2026, 10, 4, 20, 0, 0),
        "schedule: the next boundary, today or tomorrow");
    Check(Schedule.TryParse("7:05", out var t) && t == T(7, 5) && !Schedule.TryParse("25:00", out _) && !Schedule.TryParse("8pm", out _),
        "schedule: times are 24-hour HH:mm");
}

void Targets()
{
    var settings = new LinuxSettings();
    settings.Normalise();
    Check(RampTarget.For(settings, "DP-1", T(22)).IsIdentity, "ramp: nothing on is identity");

    settings.Dim["DP-1"] = 0.5;
    settings.NightLight.Enabled = true;
    settings.NightLight.Strength = 60;
    var both = RampTarget.For(settings, "DP-1", T(22));
    var warmOnly = RampTarget.For(settings, "eDP", T(22));
    Check(both.Peaks.Red == 0.5 && Math.Abs(both.Peaks.Blue - warmOnly.Blue * 0.5) < 1e-9,
        "ramp: warmth and dimming compose in one target");

    settings.NightLight.Scheduled = true;
    settings.NightLight.From = "20:00";
    settings.NightLight.To = "07:00";
    Check(RampTarget.For(settings, "eDP", T(12)).IsIdentity && !RampTarget.For(settings, "eDP", T(23)).IsIdentity,
        "ramp: a schedule warms inside its hours only");

    settings.Dim["eDP"] = 0.01;
    settings.Normalise();
    Check(settings.Dim["eDP"] == RampTarget.LowestDim, "ramp: dimming is never below the lowest level");
    settings.Dim["eDP"] = 1;
    settings.Normalise();
    Check(!settings.Dim.ContainsKey("eDP"), "ramp: full level is stored as no dimming");

    var target = new RampTarget(1, 0.8, 0.6, 0.5);
    Check(target.Matches(new RampPeaks(0.5, 0.4, 0.3)) && !target.Matches(new RampPeaks(0.5, 0.4, 0.31)),
        "ramp: a ramp read back is compared within a 16-bit step");

    static TimeOnly T(int h) => new(h, 0);
}

void SettingsFile()
{
    var saved = SettingsStore.Update(s =>
    {
        s.NightLight.Enabled = true;
        s.NightLight.Strength = 140;
        s.NightLight.From = "nonsense";
    });
    Check(saved.NightLight.Strength == 100 && saved.NightLight.From == "20:00", "settings: values are pulled back into range");
    var loaded = SettingsStore.Load();
    Check(loaded.NightLight.Enabled && loaded.NightLight.Strength == 100, "settings: a save is read back");

    // A file from a newer build: everything it adds survives this one's save.
    File.WriteAllText(SettingsStore.FilePath, """
        { "nightLight": { "enabled": false, "strength": 30, "transitionSeconds": 20 },
          "dim": { "DP-1": 0.7 }, "hotkeys": [ { "keys": "Ctrl+Alt+N" } ] }
        """);
    SettingsStore.Update(s => s.NightLight.Strength = 35);
    using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsStore.FilePath)))
    {
        var root = doc.RootElement;
        Check(root.TryGetProperty("hotkeys", out var hk) && hk.GetArrayLength() == 1
            && root.GetProperty("nightLight").GetProperty("transitionSeconds").GetInt32() == 20
            && root.GetProperty("nightLight").GetProperty("strength").GetInt32() == 35
            && root.GetProperty("dim").GetProperty("DP-1").GetDouble() == 0.7,
            "settings: properties a newer build wrote are kept, at the root and inside");
    }

    File.WriteAllText(SettingsStore.FilePath, "{ not json");
    var fresh = SettingsStore.Load();
    Check(!fresh.NightLight.Enabled && File.Exists(SettingsStore.FilePath + ".bad") && !File.Exists(SettingsStore.FilePath),
        "settings: a file that is not JSON is set aside, not overwritten, and defaults load");

    // Two writers changing different things at once lose neither.
    Parallel.For(0, 40, i => SettingsStore.Update(s =>
    {
        if (i % 2 == 0) s.Dim[$"out{i}"] = 0.5;
        else s.NightLight.Strength = i;
    }));
    Check(SettingsStore.Load().Dim.Count == 20, "settings: concurrent updates are serialised, none lost");
    File.Delete(SettingsStore.FilePath);
}

void Commands()
{
    (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandRunner.Run(args, new CommandContext(o, e, InEngine: false, () => []));
        return (code, o.ToString(), e.ToString());
    }

    Check(Run("frobnicate").Code == CommandRunner.AskedWrongly, "commands: an unknown command is asked wrongly");
    Check(Run("brightness", "150", "--ddc", "1").Code == CommandRunner.AskedWrongly, "commands: brightness above 100 is asked wrongly");
    Check(Run("brightness", "50").Code == CommandRunner.AskedWrongly, "commands: brightness without a target is asked wrongly");
    Check(Run("brightness", "50", "--ddc").Code == CommandRunner.AskedWrongly, "commands: a flag without its value is asked wrongly");
    Check(Run("brightness", "50", "--backlight", "nope").Code == CommandRunner.AskedWrongly, "commands: an unknown backlight is asked wrongly");
    Check(Run("dim", "0.05", "--all").Code == CommandRunner.AskedWrongly, "commands: dimming below 10% is refused as a black screen");
    Check(Run("dim", "0.5", "--all").Code == CommandRunner.Refused, "commands: dimming without an X display is refused");
    Check(Run("nightlight", "on").Code == CommandRunner.Refused && !SettingsStore.Load().NightLight.Enabled,
        "commands: night light without an X display is refused and not saved");
    Check(Run("nightlight", "--from", "25:00").Code == CommandRunner.AskedWrongly, "commands: an impossible time is asked wrongly");
    Check(Run("nightlight", "--from", "21:00", "--to", "21:00").Code == CommandRunner.AskedWrongly,
        "commands: a schedule that starts and ends together is asked wrongly");
    var schedule = Run("nightlight", "--from", "21:30", "--to", "6:15");
    var stored = SettingsStore.Load().NightLight;
    Check(schedule.Code == CommandRunner.Done && stored is { Scheduled: true, From: "21:30", To: "06:15", Enabled: false },
        "commands: a schedule is stored without switching night light on");
    Check(Run("nightlight").Out.Contains("21:30 to 06:15", StringComparison.Ordinal), "commands: nightlight reports its schedule");
    Check(Run("display", "set", "--output", "DP-1").Code == CommandRunner.AskedWrongly,
        "commands: display set with nothing to set is asked wrongly");
    Check(Run("display", "arrange", "DP-1").Code == CommandRunner.AskedWrongly,
        "commands: a place without a position is asked wrongly");
    Check(Run("display", "arrange", "DP-1=0,0").Code == CommandRunner.Refused,
        "commands: arranging without an X display is refused, never sent to the desk");
    Check(Run("version").Out.StartsWith("dispctrl-linux ", StringComparison.Ordinal), "commands: version");
    File.Delete(SettingsStore.FilePath);
}

void SnapGeometryChecks()
{
    // The laptop on this desk: 1870x1048 work area beside a dock.
    var area = new Rect(383, 1472, 1870, 1048);
    bool meet = true, inside = true;
    foreach (var layout in SnapLayouts.All)
    {
        var rects = layout.Zones.Select(z => z.On(area)).ToList();
        long total = rects.Sum(r => r.Area);
        meet &= total == area.Area;
        inside &= rects.All(r => r.Intersect(area) == r);
    }
    Check(meet && inside, "snap: every layout's zones tile the work area exactly, no gap or overlap");

    var halves = SnapLayouts.Halves.Zones.Select(z => z.On(area, 8)).ToList();
    Check(halves[0].X - area.X == 8 && halves[1].X - halves[0].Right == 8 && area.Right - halves[1].Right == 8
        && halves[0].Y - area.Y == 8 && area.Bottom - halves[0].Bottom == 8,
        "snap: a gap is the same between windows as at the edges");

    Check(!SnapLayouts.For(area).Contains(SnapLayouts.Thirds) && SnapLayouts.For(new Rect(0, 0, 2560, 1440)).Contains(SnapLayouts.Thirds),
        "snap: thirds only where each third has room for a window");
    Check(SnapLayouts.For(new Rect(0, 0, 3440, 1440)).Contains(SnapLayouts.Centre) && SnapLayouts.For(new Rect(0, 0, 1080, 1920))[0] == SnapLayouts.Stacked,
        "snap: an ultrawide gets a centre column, a portrait monitor stacks");

    var monitor = new MonitorInfo("eDP", false, new Rect(383, 1440, 1920, 1080), area);
    var panel = SnapPanel.For(monitor);
    Check(panel.Bounds.CenterX - area.CenterX is >= -1 and <= 1 && panel.Bounds.Y == area.Y + SnapPanel.TopOffset,
        "snap: the panel sits at the top centre of the work area");
    bool hits = panel.AllZones().All(z => panel.HitTest(panel.ZoneInTile(z).CenterX, panel.ZoneInTile(z).CenterY) == z);
    Check(hits, "snap: the centre of every zone in the panel hits that zone");
    Check(panel.HitTest(panel.Bounds.X - 40, panel.Bounds.Bottom + 40) is null, "snap: away from the panel nothing is hit");
    bool apart = panel.AllZones().All(a => panel.AllZones().All(b => a == b || panel.ZoneInTile(a).Intersect(panel.ZoneInTile(b)).IsEmpty));
    Check(apart, "snap: no two zones in the panel overlap");
    Check(panel.HandleHotspot.Contains(panel.Handle.CenterX, panel.Handle.CenterY) && panel.KeepOpen.Contains(panel.Bounds.CenterX, panel.Bounds.CenterY),
        "snap: the bar opens the panel, and the panel keeps itself open");
    var first = new ZoneRef(0, 0);
    Check(panel.Step(first, -1) == panel.AllZones().Last() && panel.Step(panel.Step(first, 1), -1) == first,
        "snap: the arrow keys step through every zone and wrap");
    Check(panel.Target(new ZoneRef(0, 1), 0) == SnapLayouts.Halves.Zones[1].On(area), "snap: a zone's target is that zone of the work area");

    var free = SnapLayouts.Halves.Zones[1].On(new Rect(0, 0, 2560, 1440));
    var cards = AssistGrid.Cards(free, 4);
    Check(cards.Count == 4 && cards.Select(c => c.Y).Distinct().Count() == 2 && cards.Select(c => c.X).Distinct().Count() == 2,
        "assist: four windows in a tall half are a 2 by 2 grid, not one column");
    Check(Enumerable.Range(1, 12).All(n => AssistGrid.Cards(free, n).All(c => c.Intersect(free) == c)),
        "assist: cards always fit inside the free zone");
    Check(AssistGrid.Cards(new Rect(0, 0, 40, 40), 3).Count == 0, "assist: a zone too small for cards gets none, not negative sizes");

    var super = Shortcut.Parse("Super+Z");
    Check(super is { Keysym: "z" } && super.Modifiers == Xlib.Mod4Mask && GnomeShortcuts.Accelerator(super) == "<Super>z",
        "shortcut: Super+Z, and GNOME's <Super>z");
    var pageDown = Shortcut.Parse("Ctrl+Alt+Page Down");
    Check(pageDown is { Keysym: "Next" } && GnomeShortcuts.Accelerator(pageDown) == "<Control><Alt>Next", "shortcut: named keys");
    Check(Shortcut.Parse("Z") is null && Shortcut.Parse("Super+") is null && Shortcut.Parse("Hyper+Z") is null && Shortcut.Parse("Ctrl+F13") is not null,
        "shortcut: a bare key, a missing key and an unknown modifier are refused");
    Check(super!.Matches(Xlib.Mod4Mask | Xlib.Mod2Mask | Xlib.LockMask) && !super.Matches(Xlib.Mod4Mask | Xlib.ShiftMask),
        "shortcut: Num Lock and Caps Lock do not matter, Shift does");

    // A decorated window (server-side) and a GTK one (client-side shadows):
    // placing either must leave its visible frame exactly on the target.
    var target = new Rect(100, 200, 900, 600);
    var decorated = Desktop.ClientFor(target, (2, 2, 37, 2));
    var gtk = Desktop.ClientFor(target, (-26, -26, -23, -29));
    Check(decorated == new Rect(102, 237, 896, 561) && gtk == new Rect(74, 177, 952, 652),
        "windows: the client rectangle for a target, either kind of decoration");
    Check(Desktop.WorkAreaFor(new Rect(0, 0, 2560, 1440), [new Rect(0, 0, 2560, 1440), new Rect(383, 1472, 1870, 1048)]) == new Rect(0, 0, 2560, 1440)
        && Desktop.WorkAreaFor(new Rect(383, 1440, 1920, 1080), [new Rect(0, 0, 2560, 1440), new Rect(383, 1472, 1870, 1048)]) == new Rect(383, 1472, 1870, 1048),
        "windows: each monitor gets its own work area from GNOME's list");
    Check(Xlib.RawDetailOffset == 56, "xinput: the button is read from XIRawEvent.detail, not flags");
    Check(DispCtrl.Linux.Graphics.DesktopTheme.AccentFrom(null, "Yaru-purple-dark") == DispCtrl.Linux.Graphics.Rgba.Hex("#9141ac")
        && DispCtrl.Linux.Graphics.DesktopTheme.AccentFrom(null, "Yaru-dark") == DispCtrl.Linux.Graphics.Rgba.Hex("#e95420"),
        "theme: Ubuntu's accent comes from the Yaru variant");
}

void Client()
{
    var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
    Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
    Check(!GammaRamp.IsAvailable(out var why) && why!.Contains("Wayland", StringComparison.Ordinal) && GammaRamp.Outputs().Count == 0,
        "ramps: a Wayland session says so rather than writing ramps XWayland ignores");
    Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", session);

    Check(EngineClient.TrySend(["status"]) is null, "client: no engine, no reply (the command runs locally)");
    File.WriteAllText(Runtime.SocketPath, "");
    Check(EngineClient.TrySend(["status"]) is null && !EngineClient.IsRunning(),
        "client: a socket file nobody listens on counts as no engine");
    File.Delete(Runtime.SocketPath);
}

void EngineProcess()
{
    var dll = Path.Combine(AppContext.BaseDirectory, "dispctrl-linux.dll");
    if (!File.Exists(dll))
    {
        Check(false, $"engine: {dll} was built");
        return;
    }

    Process Start(params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(dll);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["DISPLAY"] = ":65000";
        info.Environment.Remove("XAUTHORITY");
        return Process.Start(info)!;
    }

    using var engine = Start("engine");
    var deadline = Environment.TickCount64 + 15_000;
    while (!EngineClient.IsRunning() && Environment.TickCount64 < deadline && !engine.HasExited) Thread.Sleep(100);
    Check(EngineClient.IsRunning(), "engine: starts and listens");
    if (!EngineClient.IsRunning()) { if (!engine.HasExited) engine.Kill(); return; }

    Check(File.GetUnixFileMode(Runtime.SocketPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
        "engine: its socket is this account's alone (0600)");

    using (var second = Start("engine"))
    {
        Check(second.WaitForExit(15_000) && second.ExitCode == CommandRunner.Refused, "engine: a second one refuses to start");
    }

    var reply = EngineClient.TrySend(["nightlight", "--from", "22:00", "--to", "06:00"]);
    Check(reply is { ExitCode: 0 } && SettingsStore.Load().NightLight.From == "22:00",
        "engine: a command sent to it runs and saves");
    reply = EngineClient.TrySend(["dim", "0.5", "--all"]);
    Check(reply is { ExitCode: CommandRunner.Refused }, "engine: a refusal comes back with its exit code");
    Check(EngineClient.TrySend(["engine", "status"]) is { ExitCode: 0 } s && s.Stdout.Contains("running", StringComparison.Ordinal),
        "engine: status");

    // Several clients at once are each answered.
    var replies = Enumerable.Range(0, 8).AsParallel().Select(_ => EngineClient.TrySend(["version"])).ToList();
    Check(replies.All(r => r is { ExitCode: 0 }), "engine: concurrent clients are all answered");

    Check(EngineClient.TrySend(["engine", "stop"]) is { ExitCode: 0 }, "engine: stop is accepted");
    Check(engine.WaitForExit(10_000) && engine.ExitCode == 0 && !File.Exists(Runtime.SocketPath),
        "engine: stops cleanly and removes its socket");

    using var termed = Start("engine");
    deadline = Environment.TickCount64 + 15_000;
    while (!EngineClient.IsRunning() && Environment.TickCount64 < deadline) Thread.Sleep(100);
    Process.Start("kill", ["-TERM", termed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!.WaitForExit();
    Check(termed.WaitForExit(10_000) && termed.ExitCode == 0 && !File.Exists(Runtime.SocketPath),
        "engine: SIGTERM (systemctl stop) ends it cleanly and removes its socket");
    File.Delete(SettingsStore.FilePath);
}
