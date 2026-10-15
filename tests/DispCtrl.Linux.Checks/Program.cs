using System.Diagnostics;
using System.Text.Json;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Commands;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Presets;
using DispCtrl.Linux.Session;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.Snap;
using DispCtrl.Linux.X11;

// Never the real desk: an X display that cannot answer, and scratch settings
// and runtime folders. The runtime folder sits in /tmp because a socket path
// over 107 characters cannot be bound at all.
GammaRamp.DisplayName = ":65000";
Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "");
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
    NewFeatureChecks();
    MajorReleaseChecks();
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
          "dim": { "DP-1": 0.7 }, "cloudSync": [ { "keys": "Ctrl+Alt+N" } ] }
        """);
    SettingsStore.Update(s => s.NightLight.Strength = 35);
    using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsStore.FilePath)))
    {
        var root = doc.RootElement;
        Check(root.TryGetProperty("cloudSync", out var hk) && hk.GetArrayLength() == 1
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

void NewFeatureChecks()
{
    // ddcutil capabilities, as the Dell P2723DE on this desk prints them (cut).
    const string caps = """
        Model: P2723DE
        MCCS version: 2.1
        VCP Features:
           Feature: 04 (Restore factory defaults)
           Feature: 10 (Brightness)
           Feature: 12 (Contrast)
           Feature: 14 (Select color preset)
              Values:
                 05: 6500 K
                 0b: User 1
           Feature: 60 (Input Source)
              Values:
                 1b: Unrecognized value
                 0f: DisplayPort-1
                 11: HDMI-1
           Feature: AA (Screen Orientation)
           Feature: D6 (Power mode)
           Feature: E2 (Manufacturer specific feature)
              Values: 00 02 04 0E 12 14 (interpretation unavailable)
        """;
    var features = Ddcutil.ParseCapabilities(caps);
    Check(features.Count == 8 && features.First(f => f.Code == 0x60).Values.Select(v => v.Value).SequenceEqual(new byte[] { 0x0F, 0x11 }),
        "ddcutil: capabilities list each feature and its named values, without 'Unrecognized value'");
    Check(features.First(f => f.Code == 0xE2).Values.Count == 0, "ddcutil: a manufacturer feature's bare value list is not read as names");
    Check(Ddcutil.IsWritable(features, 0x12) && Ddcutil.IsWritable(features, 0x60) && !Ddcutil.IsWritable(features, 0x62),
        "ddcutil: a listed control is writable, an unlisted one is not");
    Check(!Ddcutil.IsWritable(features, 0x04) && !Ddcutil.IsWritable(features, 0xD6) && !Ddcutil.IsWritable(features, 0xAA) && !Ddcutil.IsWritable(features, 0xE2),
        "ddcutil: factory reset, power mode, orientation and manufacturer codes are never written");
    Check(Ddcutil.ParseVcp("VCP 60 SNC x0f\n") == new DdcVcpValue(15, null), "ddcutil: a list value is read in hex");

    Check(CommandRunner.TryDuration("30m", out var d1) && d1 == TimeSpan.FromMinutes(30)
        && CommandRunner.TryDuration("1h30m", out var d2) && d2 == TimeSpan.FromMinutes(90)
        && CommandRunner.TryDuration("45", out var d3) && d3 == TimeSpan.FromMinutes(45),
        "awake: 30m, 1h30m and a bare number of minutes");
    Check(!CommandRunner.TryDuration("0", out _) && !CommandRunner.TryDuration("h", out _) && !CommandRunner.TryDuration("8d", out _)
        && !CommandRunner.TryDuration("200h", out _), "awake: nothing, nonsense and more than a week are refused");
    var awake = new AwakeSettings { Mode = AwakeMode.Until, Until = DateTimeOffset.Now.AddMinutes(-1) };
    Check(!awake.ActiveAt(DateTimeOffset.Now) && new AwakeSettings { Mode = AwakeMode.On }.ActiveAt(DateTimeOffset.Now),
        "awake: a timed keep awake ends at its time");
    var orphan = new AwakeSettings { Mode = AwakeMode.Until };
    orphan.Normalise();
    Check(orphan.Mode == AwakeMode.Off, "awake: a timed keep awake with no time is off");

    Check(DarkMode.Sibling("Yaru", true) == "Yaru-dark" && DarkMode.Sibling("Yaru-purple-dark", false) == "Yaru-purple"
        && DarkMode.Sibling("Yaru-dark", true) is null && DarkMode.Sibling("SomeTheme", true) is null,
        "dark mode: a GTK theme switches to its dark sibling only where one is known to exist");

    // A laptop below-left of a 1440p monitor, and a projector connected but off.
    XRandROutput[] desk =
    [
        new("eDP", true, true, 1920, 1080, 333, 1440, 344, 194, 0, 144),
        new("DP-1", true, false, 2560, 1440, 0, 0, 597, 336, 0, 59.95),
        new("HDMI-1", true, false, 0, 0, 0, 0, 0, 0),
    ];
    var modes = new Dictionary<string, List<XRandRMode>>
    {
        ["eDP"] = [new(1, 1920, 1080, 144, true), new(2, 1280, 720, 60, false)],
        ["DP-1"] = [new(3, 2560, 1440, 59.95, true), new(4, 1920, 1080, 60, false), new(5, 1280, 720, 60, false)],
        ["HDMI-1"] = [new(6, 1280, 720, 60, true)],
    };
    var extend = DisplayPlans.Extend(desk, modes);
    Check(extend.Count == 3 && extend.Single(p => p.Name == "HDMI-1") is { Enabled: true, Width: 1280, X: 2560, Y: 0 },
        "plans: extend lights an output that was off in its preferred mode, on the right");
    Check(DisplayPlans.Duplicate(desk, modes, out var dup) is null && dup.All(p => p is { Width: 1280, Height: 720, X: 0, Y: 0 }),
        "plans: duplicate uses the largest size every display offers, all at 0,0");
    Check(DisplayPlans.Only(true, desk, modes, out var only) is null
        && only.Single(p => p.Enabled).Name == "eDP" && only.Count(p => !p.Enabled) == 2,
        "plans: internal keeps the built-in panel and switches the rest off");
    Check(DisplayPlans.Only(false, [desk[0]], modes, out _) is { } noExternal && noExternal.Contains("no external", StringComparison.Ordinal),
        "plans: external with no external display is refused, not a black desk");
    Check(DisplayPlans.ChangeMode("DP-1", 1920, 1080, 60, 0, desk, out var smaller) is null
        && smaller.Single(p => p.Name == "eDP") is { X: 333, Y: 1080 },
        "plans: a smaller mode pulls the display below it up to its new edge");
    Check(DisplayPlans.ChangeMode("eDP", 1920, 1080, 144, 90, desk, out var turned) is null
        && turned.Single(p => p.Name == "eDP").Extent == (1080, 1920),
        "plans: a turned display swaps its sides on the desktop");
    Check(DisplayPlans.Changes(DisplayPlans.Extend(desk, modes), desk).Select(p => p.Name).SequenceEqual(["HDMI-1"]),
        "plans: outputs already where they are asked to be are left alone");

    var preset = new Preset
    {
        Name = "Desk",
        Outputs = [new() { Name = "DP-1", Model = "DELL P2723DE", Enabled = true, Width = 2560, Height = 1440, Primary = true },
                   new() { Name = "eDP", Enabled = false }],
    };
    var names = new Dictionary<string, string> { ["DP-1"] = "DELL P2723DE" };
    Check(PresetDesk.Mismatch(preset, desk, names) is null, "presets: the same monitor on the same port matches");
    Check(PresetDesk.Mismatch(preset, desk, new Dictionary<string, string> { ["DP-1"] = "LG ULTRAGEAR" }) is { } other
        && other.Contains("LG ULTRAGEAR", StringComparison.Ordinal), "presets: another monitor on that port does not");
    Check(PresetDesk.LayoutPlan(preset, desk, names, out var plan, out var primary) is null
        && primary == "DP-1" && plan.Single(p => p.Name == "eDP").Enabled == false,
        "presets: the layout plan lights, switches off and names the main display as saved");

    var saved = SettingsStore.Update(s =>
    {
        s.Presets.Add(new Preset { Name = "  Evening ", Skip = ["layout", "wallpaper"] });
        s.Presets.Add(new Preset { Name = " " });
    });
    Check(saved.Presets.Count == 1 && saved.Presets[0] is { Name: "Evening" } p && p.Skip.SequenceEqual(["layout"]),
        "presets: names are trimmed, nameless ones dropped and unknown parts forgotten");

    (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandRunner.Run(args, new CommandContext(o, e, InEngine: false, () => []));
        return (code, o.ToString(), e.ToString());
    }
    Check(Run("awake", "for", "soon").Code == CommandRunner.AskedWrongly, "commands: awake for a non-time is asked wrongly");
    var held = Run("awake", "for", "45m");
    Check(held.Code == CommandRunner.Done && SettingsStore.Load().Awake is { Mode: AwakeMode.Until, Until: not null }
        && held.Out.Contains("engine", StringComparison.Ordinal), "commands: a timed keep awake is saved, and says the engine holds it");
    Check(Run("awake", "off").Code == CommandRunner.Done && SettingsStore.Load().Awake.Mode == AwakeMode.Off, "commands: keep awake off");
    Check(Run("preset", "save", "x", "--skip", "wallpaper").Code == CommandRunner.AskedWrongly, "commands: an unknown preset part is asked wrongly");
    Check(Run("preset", "apply", "nothing-called-this").Code == CommandRunner.AskedWrongly, "commands: an unknown preset is asked wrongly");
    Check(Run("preset", "rename", "Evening", "Night").Code == CommandRunner.Done && SettingsStore.Load().Presets.Single().Name == "Night",
        "commands: a preset is renamed");
    Check(Run("preset", "delete", "night").Code == CommandRunner.Done && SettingsStore.Load().Presets.Count == 0,
        "commands: a preset is deleted, its name matched without case");
    Check(Run("display", "set", "--output", "DP-1", "--mode", "big").Code == CommandRunner.AskedWrongly, "commands: a mode that is not a size is asked wrongly");
    Check(Run("display", "set", "--output", "DP-1", "--rotate", "sideways").Code == CommandRunner.AskedWrongly, "commands: an unknown turn is asked wrongly");
    Check(Run("display", "project", "mirror").Code == CommandRunner.AskedWrongly, "commands: an unknown projection is asked wrongly");
    Check(Run("display", "project", "extend").Code == CommandRunner.Refused, "commands: projecting without an X display is refused");
    Check(Run("darkmode", "schedule", "maybe").Code == CommandRunner.AskedWrongly, "commands: darkmode schedule takes on or off");
    Check(Run("tray", "click", "middle").Code == CommandRunner.AskedWrongly, "commands: tray click takes panel or window");
    Check(Run("help").Out.Contains("preset save NAME", StringComparison.Ordinal) && Run("help").Out.Contains("awake for", StringComparison.Ordinal),
        "commands: help names the new commands");
    var suggested = PresetDesk.Suggested();
    Check(suggested.Count >= 6 && suggested.All(t => t.BrightnessAll is not null && t.Outputs.Count == 0 && !t.Holds(PresetParts.Layout) && t.KeepSchedule),
        "presets: every ready-made one sets each display's brightness, moves no display and keeps night light's hours");
    Check(suggested.First(t => t.Name == "Gaming") is { PictureMode: "Game", NightLight.Enabled: false } g && !g.Holds(PresetParts.Theme)
        && suggested.First(t => t.Name == "Power saver").Skip.Count == 5,
        "presets: a ready-made preset holds only the parts it sets");
    Check(Run("preset", "add", "Night", "Movie").Code == CommandRunner.Done
        && SettingsStore.Load().Presets.Select(p => p.Name).SequenceEqual(["Night", "Movie"]), "commands: ready-made presets are added by name");
    Check(Run("preset", "add", "all").Code == CommandRunner.Done && SettingsStore.Load().Presets.Count == suggested.Count,
        "commands: add all adds the rest and keeps the ones there");
    Check(Run("preset", "add", "Disco").Code == CommandRunner.AskedWrongly, "commands: an unknown ready-made preset is asked wrongly");
    SettingsStore.Update(s => { s.NightLight.Scheduled = true; s.NightLight.From = "21:00"; s.NightLight.To = "06:30"; s.Dim["DP-1"] = 0.6; });
    // Night light only: its brightness and theme would reach the real desk.
    var night = SettingsStore.Load().Presets.First(p => p.Name == "Night");
    PresetDesk.Apply(new Preset
    {
        Name = night.Name, NightLight = night.NightLight, KeepSchedule = night.KeepSchedule,
        Skip = [PresetParts.Layout, PresetParts.Brightness, PresetParts.Theme, PresetParts.Controls],
    });
    var kept = SettingsStore.Load();
    Check(kept.NightLight is { Enabled: true, Strength: 75, Scheduled: true, From: "21:00", To: "06:30" } && kept.Dim["DP-1"] == 0.6,
        "presets: a ready-made preset switches night light and its strength, and keeps its hours and the dimming");
    File.Delete(SettingsStore.FilePath);
}

void MajorReleaseChecks()
{
    // Window tools: a window's place carried to another display's work area.
    var from = new Rect(0, 0, 2560, 1400);
    var to = new Rect(2560, 0, 1920, 1040);
    Check(DispCtrl.Linux.Placement.WindowMoves.Carry(new Rect(0, 0, 800, 600), from, to) == new Rect(2560, 0, 800, 600)
        && DispCtrl.Linux.Placement.WindowMoves.Carry(new Rect(1760, 800, 800, 600), from, to) == new Rect(3680, 440, 800, 600),
        "placement: a window keeps its corner on the display it moves to");
    Check(DispCtrl.Linux.Placement.WindowMoves.Carry(new Rect(0, 0, 2560, 1400), from, to) == to,
        "placement: a window larger than the display it moves to is fitted to it");
    var monitors = new List<MonitorInfo>
    {
        new("DP-1", true, new Rect(1920, 0, 2560, 1440), new Rect(1920, 0, 2560, 1400)),
        new("eDP", false, new Rect(0, 360, 1920, 1080), new Rect(0, 360, 1920, 1080)),
    };
    Check(monitors[DispCtrl.Linux.Placement.WindowMoves.Neighbour(monitors, monitors[1], 1)].Name == "DP-1"
        && monitors[DispCtrl.Linux.Placement.WindowMoves.Neighbour(monitors, monitors[0], 1)].Name == "eDP",
        "placement: next is left to right, wrapping round");
    Check(DispCtrl.Linux.Placement.WindowMoves.Span(monitors.Select(m => m.WorkArea)) == new Rect(0, 360, 4480, 1040),
        "placement: a span covers every display, within the height they share");

    Check(BrightnessTargets.Scale(0, 20, 80) == 20 && BrightnessTargets.Scale(100, 20, 80) == 80 && BrightnessTargets.Scale(50, 20, 80) == 50,
        "unison: each display moves within its own range");
    Check(DispCtrl.Linux.Automation.FeatureRunner.Split("preset apply \"Late night\"").SequenceEqual(["preset", "apply", "Late night"]),
        "features: quotes hold a name with a space");
    Check(DispCtrl.Linux.Automation.FeatureRunner.Problem("wait 500") is null && DispCtrl.Linux.Automation.FeatureRunner.Problem("wait soon") is not null
        && DispCtrl.Linux.Automation.FeatureRunner.Problem("nightlight on") is null && DispCtrl.Linux.Automation.FeatureRunner.Problem("frobnicate") is not null
        && DispCtrl.Linux.Automation.FeatureRunner.Problem("engine stop") is not null,
        "features: steps are checked before they are saved");

    var power = Directory.CreateDirectory(Path.Combine(scratch, "power")).FullName;
    Directory.CreateDirectory(Path.Combine(power, "AC"));
    Directory.CreateDirectory(Path.Combine(power, "BAT0"));
    File.WriteAllText(Path.Combine(power, "AC", "type"), "Mains\n");
    File.WriteAllText(Path.Combine(power, "AC", "online"), "0\n");
    File.WriteAllText(Path.Combine(power, "BAT0", "type"), "Battery\n");
    bool? onBattery = DispCtrl.Linux.Automation.SessionEvents.Mains(power);
    File.WriteAllText(Path.Combine(power, "AC", "online"), "1\n");
    Check(onBattery == false && DispCtrl.Linux.Automation.SessionEvents.Mains(power) == true,
        "triggers: mains power is read from the kernel's power supplies");
    Directory.Delete(Path.Combine(power, "BAT0"), true);
    Check(DispCtrl.Linux.Automation.SessionEvents.Mains(power) is null, "triggers: a desktop with no battery reports no power changes");

    Check(DispCtrl.Linux.Session.RoomLight.LevelFor(0, 20, 100) == 20 && DispCtrl.Linux.Session.RoomLight.LevelFor(1000, 20, 100) == 100
        && DispCtrl.Linux.Session.RoomLight.LevelFor(30, 20, 100) is > 50 and < 70,
        "room light: brightness follows the logarithm of the light, within its range");

    var scrubbed = CommandRunner.Scrub($"monitor DELL P2723DE|22WB714 at {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/x by {Environment.UserName}");
    Check(!scrubbed.Contains("22WB714", StringComparison.Ordinal) && !scrubbed.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal),
        "report: serials and the home folder are taken out");

    Check(HotkeyBinding.Defaults().All(h => Shortcut.Parse(h.Keys) is not null && CommandRunner.HotkeyCommandProblem(h.Command) is null),
        "hotkeys: every default is a shortcut and a command that exists");
    Check(!HotkeyBinding.Defaults().Any(h => h.Keys is "Ctrl+Alt+L" or "Ctrl+Alt+Backspace"),
        "hotkeys: none takes the lock screen's key or X's own Ctrl+Alt+Backspace");

    var desk = new Preset { Name = "Desk", Desk = true, Outputs = [new() { Name = "DP-1", Model = "DELL", Enabled = true }, new() { Name = "eDP", Enabled = true }] };
    Check(PresetDesk.FitsExactly(desk, new Dictionary<string, string?> { ["DP-1"] = "DELL", ["eDP"] = null })
        && !PresetDesk.FitsExactly(desk, new Dictionary<string, string?> { ["eDP"] = null })
        && !PresetDesk.FitsExactly(desk, new Dictionary<string, string?> { ["DP-1"] = "LG", ["eDP"] = null }),
        "desk profiles: exactly the displays saved, with the same monitors, and no other");

    OpenRgbChecks();

    (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandRunner.Run(args, new CommandContext(o, e, InEngine: false, () => []));
        return (code, o.ToString(), e.ToString());
    }
    Check(Run("features", "add", "Late", "nightlight 80; frobnicate").Code == CommandRunner.AskedWrongly, "commands: a feature with an unknown step is refused");
    Check(Run("features", "add", "Late", "nightlight 80; wait 10").Code == CommandRunner.Done
        && SettingsStore.Load().Features.Single() is { Name: "Late", Steps.Count: 2 }, "commands: a feature is saved with its steps");
    Check(Run("triggers", "add", "sunrise", "Late").Code == CommandRunner.AskedWrongly, "commands: an unknown event is asked wrongly");
    Check(Run("triggers", "add", "at-time", "Late").Code == CommandRunner.AskedWrongly, "commands: at-time needs a time");
    Check(Run("triggers", "add", "at-time", "Late", "--match", "22:30").Code == CommandRunner.Done
        && SettingsStore.Load().Triggers.Single() is { Event: "at-time", Match: "22:30", Feature: "Late" }, "commands: a trigger is saved");
    Check(Run("hotkeys", "add", "N", "nightlight toggle").Code == CommandRunner.AskedWrongly, "commands: a hotkey needs a modifier");
    Check(Run("hotkeys", "add", "Ctrl+Alt+M", "preset", "apply", "Movie").Code == CommandRunner.Done
        && SettingsStore.Load().Hotkeys!.Any(h => h.Keys == "Ctrl+Alt+M" && h.Command == "preset apply Movie"), "commands: a hotkey is added");
    Check(Run("hotkeys", "remove", "Ctrl+Alt+M").Code == CommandRunner.Done && !SettingsStore.Load().Hotkeys!.Any(h => h.Keys == "Ctrl+Alt+M"),
        "commands: a hotkey is removed by its keys");
    Check(Run("oled", "on", "--idle-minutes", "5", "--dim", "40").Code == CommandRunner.Done
        && SettingsStore.Load().OledCare is { Enabled: true, IdleMinutes: 5, DimPercent: 40 }, "commands: OLED care is set");
    Check(Run("oled", "--dim", "99").Code == CommandRunner.AskedWrongly, "commands: OLED care's dimming is held to its range");
    Check(Run("focus", "on", "--dim", "5").Code == CommandRunner.AskedWrongly, "commands: focus's dimming is held to its range");
    // A port nothing listens on: the machine running the checks may have
    // OpenRGB's server up (this desk does), and it must not be reached.
    SettingsStore.Update(s => s.Lighting.Port = 9);
    Check(Run("rgb").Code == CommandRunner.Refused && Run("rgb").Err.Contains("OpenRGB", StringComparison.Ordinal),
        "commands: lighting without OpenRGB running says what to start");
    Check(Run("rgb", "colour", "chartreuse-ish").Code == CommandRunner.AskedWrongly, "commands: a colour that is not one is asked wrongly");
    Check(Run("pin").Code == CommandRunner.Refused, "commands: pinning without an X display is refused");
    Check(Run("unison", "range", "x", "80", "20").Code == CommandRunner.AskedWrongly, "commands: a unison range lowest first");
    File.Delete(SettingsStore.FilePath);
}

void OpenRgbChecks()
{
    // A device as OpenRGB protocol 3 lays it out: two modes, one zone, three LEDs.
    var blob = new MemoryStream();
    var w = new BinaryWriter(blob);
    void Str(string text) { var b = System.Text.Encoding.UTF8.GetBytes(text + "\0"); w.Write((ushort)b.Length); w.Write(b); }
    w.Write(0u); w.Write(5);
    Str("Keychron K2"); Str("Keychron"); Str("desc"); Str("1.0"); Str("serial"); Str("usb");
    w.Write((ushort)2); w.Write(1);
    foreach (var (name, flags) in new[] { ("Direct", 1u << 5), ("Static", 1u << 6) })
    {
        Str(name); w.Write(0); w.Write(flags); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(100u);
        w.Write(1u); w.Write(1u); w.Write(0u); w.Write(100u); w.Write(0u); w.Write(name == "Static" ? 2u : 1u);
        w.Write((ushort)(name == "Static" ? 1 : 0)); if (name == "Static") w.Write(0x0000FFu);
    }
    w.Write((ushort)1); Str("Keys"); w.Write(2); w.Write(3u); w.Write(3u); w.Write(3u); w.Write((ushort)0);
    w.Write((ushort)3); foreach (var led in new[] { "A", "B", "C" }) { Str(led); w.Write(0u); }
    w.Write((ushort)3); w.Write(0u); w.Write(0u); w.Write(0u);
    w.Flush();
    var device = DispCtrl.Linux.Lighting.OpenRgb.Parse(0, blob.ToArray(), 3);
    Check(device is { Name: "Keychron K2", Vendor: "Keychron", Type: 5, ActiveMode: 1, LedCount: 3 } && device.Modes.Count == 2
        && device.Modes[1] is { Name: "Static", ColorMode: 2 } && device.Modes[1].Colors.SequenceEqual([0x0000FFu]),
        "openrgb: a device is read as protocol 3 lays it out");
    var mode = DispCtrl.Linux.Lighting.OpenRgb.SerializeMode(device.Modes[1], 3);
    Check(BitConverter.ToUInt32(mode) == mode.Length && BitConverter.ToInt32(mode, 4) == 1,
        "openrgb: an effect is written with its size and index first");
    Check(DispCtrl.Linux.Lighting.OpenRgb.Pack(0xFF, 0x80, 0x10) == 0x1080FFu, "openrgb: colours are red in the low byte");

    // A server that answers as OpenRGB does, to the handshake and the device list.
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var leds = new List<byte[]>();
    var server = Task.Run(() =>
    {
        using var client = listener.AcceptTcpClient();
        var s = client.GetStream();
        var header = new byte[16];
        void Reply(uint packet, byte[] body)
        {
            var h = new byte[16];
            "ORGB"u8.CopyTo(h);
            BitConverter.GetBytes(packet).CopyTo(h, 8);
            BitConverter.GetBytes((uint)body.Length).CopyTo(h, 12);
            s.Write(h); s.Write(body);
        }
        try
        {
            while (true)
            {
                s.ReadExactly(header);
                uint packet = BitConverter.ToUInt32(header, 8);
                var body = new byte[BitConverter.ToUInt32(header, 12)];
                s.ReadExactly(body);
                switch (packet)
                {
                    case 40: Reply(40, BitConverter.GetBytes(4u)); break;
                    case 0: Reply(0, BitConverter.GetBytes(1u)); break;
                    case 1: Reply(1, blob.ToArray()); break;
                    case 1050: leds.Add(body); break;
                }
            }
        }
        catch (Exception) { }
    });
    using (var rgb = DispCtrl.Linux.Lighting.OpenRgb.Connect("127.0.0.1", port))
    {
        var devices = rgb.Devices();
        rgb.SetColour(devices[0], 0xFF, 0x93, 0x29);
    }
    server.Wait(3000);
    listener.Stop();
    Check(leds.Count == 1 && BitConverter.ToUInt16(leds[0], 4) == 3 && leds[0][6] == 0xFF && leds[0][7] == 0x93 && leds[0][8] == 0x29,
        "openrgb: a colour reaches every LED, after the handshake settles on protocol 3");
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
        // Not GNOME: the engine would write its hotkeys into GNOME's real settings.
        info.Environment["XDG_CURRENT_DESKTOP"] = "";
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
