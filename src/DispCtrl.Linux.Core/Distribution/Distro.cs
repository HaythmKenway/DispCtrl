using System.Runtime.InteropServices;

namespace DispCtrl.Linux.Distribution;

/// <summary>Which family of distributions this is, from <c>/etc/os-release</c>,
/// so advice names the package manager and package that exist here.</summary>
public enum DistroFamily { Unknown, Debian, Fedora, Suse, Arch, Alpine, Void, Gentoo }

/// <summary>The native libraries DispCtrl loads, and how to install each on
/// every family.</summary>
/// <remarks>
/// The packages carry .NET with them, so these are all that must come from the
/// system. A tarball install has no package manager to pull them in, and a
/// missing one surfaced as a <see cref="DllNotFoundException"/> naming a
/// soname nobody knows the package for; <c>doctor</c> names the package.
/// </remarks>
public static class Distro
{
    /// <summary>A library DispCtrl loads, what needs it, and its package per family:
    /// Debian, Fedora, SUSE, Arch, Alpine, Void, Gentoo.</summary>
    public sealed record Library(string Soname, string NeededFor, string[] Packages);

    public static readonly IReadOnlyList<Library> Libraries =
    [
        new("libX11.so.6", "everything on X11", ["libx11-6", "libX11", "libX11-6", "libx11", "libx11", "libX11", "x11-libs/libX11"]),
        new("libXrandr.so.2", "night light, dimming and arranging displays", ["libxrandr2", "libXrandr", "libXrandr2", "libxrandr", "libxrandr", "libXrandr", "x11-libs/libXrandr"]),
        new("libXi.so.6", "snapping by dragging", ["libxi6", "libXi", "libXi6", "libxi", "libxi", "libXi", "x11-libs/libXi"]),
        new("libXtst.so.6", "Stay active's pointer nudge", ["libxtst6", "libXtst", "libXtst6", "libxtst", "libxtst", "libXtst", "x11-libs/libXtst"]),
        new("libXext.so.6", "turning the displays off (DPMS)", ["libxext6", "libXext", "libXext6", "libxext", "libxext", "libXext", "x11-libs/libXext"]),
        new("libXfixes.so.3", "snap layouts", ["libxfixes3", "libXfixes", "libXfixes3", "libxfixes", "libxfixes", "libXfixes", "x11-libs/libXfixes"]),
        new("libXcomposite.so.1", "Snap Assist's window pictures", ["libxcomposite1", "libXcomposite", "libXcomposite1", "libxcomposite", "libxcomposite", "libXcomposite", "x11-libs/libXcomposite"]),
        new("libcairo.so.2", "snap layouts", ["libcairo2", "cairo", "libcairo2", "cairo", "cairo", "cairo", "x11-libs/cairo"]),
        new("libpango-1.0.so.0", "the snap layouts' labels", ["libpango-1.0-0", "pango", "libpango-1_0-0", "pango", "pango", "pango", "x11-libs/pango"]),
        new("libpangocairo-1.0.so.0", "the snap layouts' labels", ["libpangocairo-1.0-0", "pango", "libpango-1_0-0", "pango", "pango", "pango", "x11-libs/pango"]),
        new("libfontconfig.so.1", "the window", ["libfontconfig1", "fontconfig", "libfontconfig1", "fontconfig", "fontconfig", "fontconfig", "media-libs/fontconfig"]),
        new("libICE.so.6", "the window", ["libice6", "libICE", "libICE6", "libice", "libice", "libICE", "x11-libs/libICE"]),
        new("libSM.so.6", "the window", ["libsm6", "libSM", "libSM6", "libsm", "libsm", "libSM", "x11-libs/libSM"]),
    ];

    /// <summary>Running from the snap, whose strict confinement cannot reach
    /// /dev/i2c-*: no ddcutil, staged or the host's, can talk to a monitor.</summary>
    public static bool InSnap => Environment.GetEnvironmentVariable("SNAP_NAME") == "dispctrl-linux";

    /// <summary>Why external monitors' own brightness cannot be set here, and
    /// what to do: the reason in the snap, the install command elsewhere.</summary>
    public static string DdcutilAdvice => InSnap
        ? "the snap cannot reach monitors over DDC/CI (strict confinement keeps /dev/i2c-* out); the .deb, .rpm or tarball can"
        : $"ddcutil is not installed ({InstallCommand([Ddcutil])})";

    /// <summary>The command that starts the engine now; with systemd it also
    /// enables it for every sign-in.</summary>
    /// <remarks>
    /// The snap has no systemd unit (its engine is the <c>dispctrl-linux.engine</c>
    /// app, started at sign-in by snapd's autostart), so the unit's command
    /// failed there with "Unit file dispctrl-linux-engine.service does not
    /// exist". A tarball install without systemd starts it from an autostart
    /// entry at sign-in instead.
    /// </remarks>
    public static string EngineStartCommand =>
        InSnap ? "setsid dispctrl-linux.engine &"
        : Directory.Exists("/run/systemd/system") ? "systemctl --user enable --now dispctrl-linux-engine"
        : "setsid dispctrl-linux engine &";

    /// <summary>Whether <see cref="EngineStartCommand"/> covers later sign-ins
    /// by itself (systemd) or only this one (the snap and autostart do the rest).</summary>
    public static bool EngineStartsAtSignIn => InSnap || !Directory.Exists("/run/systemd/system");

    /// <summary>ddcutil's package, which has the same name everywhere it is packaged.</summary>
    public const string Ddcutil = "ddcutil";

    public static DistroFamily Family { get; } = Detect(ReadOsRelease());

    /// <summary>The libraries that cannot be loaded here.</summary>
    public static List<Library> Missing() =>
        Libraries.Where(l =>
        {
            if (!NativeLibrary.TryLoad(l.Soname, out var handle)) return true;
            NativeLibrary.Free(handle);
            return false;
        }).ToList();

    /// <summary>The command that installs <paramref name="libraries"/> on this family.</summary>
    public static string InstallCommand(IEnumerable<Library> libraries) =>
        InstallCommand(libraries.Select(l => PackageOf(l, Family)));

    public static string InstallCommand(IEnumerable<string> packages)
    {
        var list = string.Join(' ', packages.Distinct());
        return Family switch
        {
            DistroFamily.Debian => $"sudo apt install {list}",
            DistroFamily.Fedora => $"sudo dnf install {list}",
            DistroFamily.Suse => $"sudo zypper install {list}",
            DistroFamily.Arch => $"sudo pacman -S {list}",
            DistroFamily.Alpine => $"sudo apk add {list}",
            DistroFamily.Void => $"sudo xbps-install {list}",
            DistroFamily.Gentoo => $"sudo emerge {list}",
            _ => $"install {list} with your package manager",
        };
    }

    internal static string PackageOf(Library library, DistroFamily family) => family switch
    {
        DistroFamily.Fedora => library.Packages[1],
        DistroFamily.Suse => library.Packages[2],
        DistroFamily.Arch => library.Packages[3],
        DistroFamily.Alpine => library.Packages[4],
        DistroFamily.Void => library.Packages[5],
        DistroFamily.Gentoo => library.Packages[6],
        // Debian's names are the ones most often searched for.
        _ => library.Packages[0],
    };

    private static string ReadOsRelease()
    {
        foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try { return File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    /// <summary>The family from <c>ID</c> and <c>ID_LIKE</c>: Ubuntu, Mint and
    /// Pop!_OS say debian; Rocky, Alma and CentOS say fedora or rhel; Manjaro
    /// and EndeavourOS say arch.</summary>
    internal static DistroFamily Detect(string osRelease)
    {
        var ids = new List<string>();
        foreach (var line in osRelease.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ID=", StringComparison.Ordinal) && !trimmed.StartsWith("ID_LIKE=", StringComparison.Ordinal)) continue;
            var value = trimmed[(trimmed.IndexOf('=') + 1)..].Trim('"', '\'');
            ids.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var id in ids)
        {
            switch (id)
            {
                case "debian" or "ubuntu": return DistroFamily.Debian;
                case "fedora" or "rhel" or "centos": return DistroFamily.Fedora;
                case "suse" or "opensuse" or "sles" or "opensuse-tumbleweed" or "opensuse-leap": return DistroFamily.Suse;
                case "arch" or "archlinux": return DistroFamily.Arch;
                case "alpine": return DistroFamily.Alpine;
                case "void": return DistroFamily.Void;
                case "gentoo": return DistroFamily.Gentoo;
            }
        }
        return DistroFamily.Unknown;
    }
}
