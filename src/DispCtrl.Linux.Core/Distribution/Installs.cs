using System.Text.RegularExpressions;

namespace DispCtrl.Linux.Distribution;

/// <summary>Which way DispCtrl was installed.</summary>
public enum InstallKind { Snap, Package, Tarball, Other }

/// <summary>Another DispCtrl install on this machine, and how to remove it.</summary>
/// <param name="Running">An engine of it is alive; the footprint alone is only a finding.</param>
public sealed record OtherInstall(InstallKind Kind, string Evidence, string Remedy, bool Running);

/// <summary>Finds a second DispCtrl install, because two of them - a snap beside
/// the .deb, say - each run an engine and a tray that fight over the same gamma
/// ramps, hotkeys and settings.</summary>
/// <remarks>
/// The installers refuse first (package-guard.sh, install.sh). This is the
/// check that cannot be skipped: it runs in every engine, window and
/// <c>doctor</c>, and it works under the snap's confinement as far as that
/// allows. A confined process reads /proc/&lt;pid&gt;/cmdline of others but
/// usually not their exe link, and sees neither the host's /usr nor the real
/// home, so what it cannot see it reports as nothing rather than guessing;
/// the session bus name (<see cref="SessionBus.EngineName"/>) covers the rest.
/// Every location is a parameter so the checks run against a fake root.
/// </remarks>
public static class Installs
{
    /// <summary>The files and processes of the machine to look at.</summary>
    public sealed record Probe(string Root, string Home, string OwnExe, int OwnPid, uint Uid, DistroFamily Family)
    {
        public static Probe Current()
        {
            // The snap's $HOME is its own folder; the real one is where a tarball lives.
            var home = Environment.GetEnvironmentVariable("SNAP_REAL_HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new("/", home, Environment.ProcessPath ?? "", Environment.ProcessId, GetUid(), Distro.Family);
        }

        internal string At(string path) => Path.Combine(Root, path.TrimStart('/'));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    private static readonly Regex SnapRevision = new(@"^/snap/dispctrl-linux/[^/]+/", RegexOptions.Compiled);

    /// <summary>How the program at <paramref name="exe"/> was installed, from where it is.</summary>
    public static InstallKind KindOf(string exe)
    {
        exe = Clean(exe);
        if (exe.StartsWith("/snap/dispctrl-linux/", StringComparison.Ordinal)
            || exe.StartsWith("/var/lib/snapd/snap/dispctrl-linux/", StringComparison.Ordinal)) return InstallKind.Snap;
        if (exe.StartsWith("/usr/lib/dispctrl-linux/", StringComparison.Ordinal) || exe.StartsWith("/usr/bin/dispctrl-linux", StringComparison.Ordinal)) return InstallKind.Package;
        if (exe.Contains("/.local/lib/dispctrl-linux/", StringComparison.Ordinal) || exe.Contains("/.local/bin/dispctrl-linux", StringComparison.Ordinal)) return InstallKind.Tarball;
        return InstallKind.Other;
    }

    private static string Clean(string exe)
    {
        const string deleted = " (deleted)";
        if (exe.EndsWith(deleted, StringComparison.Ordinal)) exe = exe[..^deleted.Length];
        return SnapRevision.Replace(exe, "/snap/dispctrl-linux/current/");
    }

    /// <summary>Whether two program paths are the same install. Revisions of a
    /// snap are one install; two folders of no known kind are two.</summary>
    internal static bool SameInstall(string a, string b)
    {
        var ka = KindOf(a);
        if (ka != KindOf(b)) return false;
        return ka != InstallKind.Other || Path.GetDirectoryName(Clean(a)) == Path.GetDirectoryName(Clean(b));
    }

    /// <summary>The engines of other installs that are running now: in /proc by
    /// command line, owned by this user.</summary>
    public static List<OtherInstall> RunningElsewhere(Probe probe)
    {
        var found = new List<OtherInstall>();
        string proc = probe.At("proc");
        if (!Directory.Exists(proc)) return found;
        foreach (var dir in Directory.EnumerateDirectories(proc))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid) || pid == probe.OwnPid) continue;
            try
            {
                var args = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 2 || args[1] != "engine" || Path.GetFileName(args[0]) != "dispctrl-linux") continue;
                if (!OwnedBy(dir, probe.Uid)) continue;

                string? exe = null;
                try { exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget; } catch (IOException) { }
                // Without the exe link, a command line that is not a path says
                // nothing about which install it is, so it is not counted.
                exe ??= Path.IsPathRooted(args[0]) ? args[0] : null;
                if (exe is null || SameInstall(exe, probe.OwnExe)) continue;

                var kind = KindOf(exe);
                found.Add(new(kind, $"an engine of the {Describe(kind)} install is running (pid {pid}, {exe})", Removal(kind, probe), true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    private static bool OwnedBy(string procDir, uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(procDir, "status")))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var fields = line[4..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0 && fields[0] == uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Other installs' files, whether or not their engines run.</summary>
    public static List<OtherInstall> Footprints(Probe probe)
    {
        var own = KindOf(probe.OwnExe);
        var found = new List<OtherInstall>();
        void Add(InstallKind kind, bool present, string what)
        {
            if (present && kind != own) found.Add(new(kind, $"the {Describe(kind)} install is present ({what})", Removal(kind, probe), false));
        }

        Add(InstallKind.Snap, Directory.Exists(probe.At("snap/dispctrl-linux")) || Directory.Exists(probe.At("var/lib/snapd/snap/dispctrl-linux"))
            || Directory.Exists(Path.Combine(probe.Home, "snap", "dispctrl-linux")), "/snap/dispctrl-linux");
        Add(InstallKind.Package, File.Exists(probe.At("usr/bin/dispctrl-linux")) || Directory.Exists(probe.At("usr/lib/dispctrl-linux"))
            || File.Exists(probe.At("etc/xdg/autostart/dispctrl-linux-engine.desktop")), "/usr/lib/dispctrl-linux");
        Add(InstallKind.Tarball, Directory.Exists(Path.Combine(probe.Home, ".local/lib/dispctrl-linux"))
            || File.Exists(Path.Combine(probe.Home, ".config/systemd/user/dispctrl-linux-engine.service")), "~/.local/lib/dispctrl-linux");
        return found;
    }

    /// <summary>The finding doctor, the window and the tray show: running engines
    /// of another install first, else the session bus name when this process
    /// has no engine of its own, else other installs that are only present.
    /// Empty when there is one install.</summary>
    /// <param name="ownEngineRuns">This install's engine answers: the name on the bus is then its own.</param>
    public static List<OtherInstall> Conflicts(Probe probe, bool ownEngineRuns, Func<bool?>? busOwner = null)
    {
        var running = RunningElsewhere(probe);
        if (running.Count == 0 && !ownEngineRuns && (busOwner ?? BusHasOwner)() == true)
        {
            running.Add(new(InstallKind.Other,
                $"another DispCtrl engine holds {SessionBus.EngineName} on the session bus",
                "stop it with \"dispctrl-linux engine stop\" from the install that runs it, then remove that install", true));
        }
        running.AddRange(Footprints(probe).Where(f => running.All(r => r.Kind != f.Kind)));
        return running;
    }

    /// <summary>Set to 1 to start beside another install on purpose: the checks,
    /// which run an engine while the owner's own runs, and a developer's build.</summary>
    public const string AllowVariable = "DISPCTRL_ALLOW_SECOND_INSTALL";

    /// <summary>Why this process must not start - another install's engine is
    /// running - or null. One line, with the command that removes the other.</summary>
    public static string? Refusal(bool ownEngineRuns)
    {
        if (Environment.GetEnvironmentVariable(AllowVariable) == "1") return null;
        var running = Conflicts(Probe.Current(), ownEngineRuns).Where(c => c.Running).ToList();
        return running.Count == 0 ? null : OneLine(running);
    }

    private static bool? BusHasOwner()
    {
        using var bus = SessionBus.Connect();
        return bus?.NameHasOwner(SessionBus.EngineName);
    }

    /// <summary>One line naming the second install and how to remove it.</summary>
    public static string OneLine(IReadOnlyList<OtherInstall> others) =>
        others[0].Evidence + "; DispCtrl runs from one install only. Remove the other: " + others[0].Remedy;

    private static string Describe(InstallKind kind) => kind switch
    {
        InstallKind.Snap => "snap",
        InstallKind.Package => ".deb/.rpm package",
        InstallKind.Tarball => "tarball",
        _ => "other",
    };

    private static string Removal(InstallKind kind, Probe probe) => kind switch
    {
        InstallKind.Snap => "sudo snap remove dispctrl-linux",
        InstallKind.Package => PackageRemoval(probe.Family),
        InstallKind.Tarball => "~/.local/lib/dispctrl-linux/install.sh --uninstall",
        _ => "stop it with \"dispctrl-linux engine stop\" and remove that copy",
    };

    private static string PackageRemoval(DistroFamily family) => family switch
    {
        DistroFamily.Debian => "sudo apt remove dispctrl-linux",
        DistroFamily.Fedora => "sudo dnf remove dispctrl-linux",
        DistroFamily.Suse => "sudo zypper remove dispctrl-linux",
        DistroFamily.Arch => "sudo pacman -R dispctrl-linux",
        DistroFamily.Alpine => "sudo apk del dispctrl-linux",
        DistroFamily.Void => "sudo xbps-remove dispctrl-linux",
        _ => "remove the dispctrl-linux package with your package manager",
    };
}
