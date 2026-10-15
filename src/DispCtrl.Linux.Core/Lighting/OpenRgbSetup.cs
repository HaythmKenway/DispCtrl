using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Lighting;

/// <summary>How an OpenRGB found on this machine is run.</summary>
public enum OpenRgbKind { Binary, AppImage, Flatpak, Snap }

public sealed record OpenRgbInstall(OpenRgbKind Kind, string Path)
{
    public string Describe() => Kind switch
    {
        OpenRgbKind.Flatpak => "OpenRGB from Flatpak (org.openrgb.OpenRGB)",
        OpenRgbKind.Snap => $"OpenRGB from a snap ({Path})",
        OpenRgbKind.AppImage => $"the OpenRGB AppImage at {Path}",
        _ => $"OpenRGB at {Path}",
    };
}

/// <summary>Where OpenRGB may be; a value so the finder can be checked on a scratch tree.</summary>
public sealed record OpenRgbRoots(string Path, string Home, string DataFolder, string SystemFlatpak, string SnapBin)
{
    public static OpenRgbRoots Current() => new(
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OpenRgbSetup.DataFolder, "/var/lib/flatpak", "/snap/bin");
}

/// <summary>Finds, starts and, when there is none, downloads OpenRGB, so
/// lighting does not begin with "install it and start its server yourself".
/// No driver is embedded: OpenRGB stays the program that talks to the hardware.</summary>
/// <remarks>
/// The server is started headless (<c>--server</c>). <c>--startminimized</c> is
/// avoided on purpose: that mode exits after the device scan on some builds.
/// AppImages are run with <c>--appimage-extract-and-run</c> because FUSE is
/// often missing, and the process is detached into its own session so it
/// outlives the command that started it.
/// </remarks>
public static class OpenRgbSetup
{
    /// <summary>The pinned release. The SHA-256 values were computed from the
    /// files downloaded from the release page, not copied from a listing.</summary>
    public const string Version = "1.0";

    public sealed record Asset(string File, string Url, string Sha256);

    private const string ReleaseUrl = "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.0/";

    /// <summary>The release AppImage for each architecture OpenRGB publishes that this client runs on.</summary>
    public static Asset? AssetFor(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => new("OpenRGB_1.0_x86_64_81bbe18.AppImage",
            ReleaseUrl + "OpenRGB_1.0_x86_64_81bbe18.AppImage", "a77d9fea9ab1e59e5ec2b5cec4ab22d50f0433594c4e83eb13de4e5c3cfadebf"),
        System.Runtime.InteropServices.Architecture.Arm64 => new("OpenRGB_1.0_arm64_81bbe18.AppImage",
            ReleaseUrl + "OpenRGB_1.0_arm64_81bbe18.AppImage", "7dfdb791ac2d08b06fd2209b8cba95d7ce3d4d63456d29ab783a06cdcec5ea97"),
        _ => null,
    };

    /// <summary>DispCtrl's data folder; $DISPCTRL_LINUX_DATA_DIR for scratch runs.</summary>
    public static string DataFolder
    {
        get
        {
            if (Environment.GetEnvironmentVariable("DISPCTRL_LINUX_DATA_DIR") is { Length: > 0 } overridden) return overridden;
            var root = Environment.GetEnvironmentVariable("SNAP_USER_COMMON") is { Length: > 0 } common
                ? common
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                    ? data : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return System.IO.Path.Combine(root, "dispctrl-linux");
        }
    }

    public static string OwnCopy(string dataFolder) => System.IO.Path.Combine(dataFolder, "openrgb", "OpenRGB.AppImage");

    /// <summary>Why OpenRGB cannot be started from the snap, or null elsewhere.</summary>
    /// <remarks>
    /// The snap's strict confinement (snapcraft.yaml) has no `home` or `process-control`
    /// plug and no raw-USB or i2c interface: it cannot run the host's openrgb or reach
    /// the hardware from an AppImage of its own. It can still connect to a server on
    /// 127.0.0.1 through its `network` plug, so the server has to be started outside it.
    /// </remarks>
    public static string? SnapLimit() => Distro.InSnap
        ? "the snap cannot start OpenRGB: strict confinement keeps programs outside the snap and the USB devices out of reach. "
          + $"In a terminal on this computer run: openrgb --server (or the AppImage with --server). The snap connects to it on 127.0.0.1:{OpenRgb.DefaultPort}"
        : null;

    public static OpenRgbInstall? Find() => Find(OpenRgbRoots.Current());

    public static OpenRgbInstall? Find(OpenRgbRoots roots)
    {
        foreach (var dir in roots.Path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, "openrgb");
            if (IsExecutable(candidate)) return new(OpenRgbKind.Binary, candidate);
        }
        var local = System.IO.Path.Combine(roots.Home, ".local", "bin", "OpenRGB.AppImage");
        if (IsExecutable(local)) return new(OpenRgbKind.AppImage, local);
        var flatpakApp = System.IO.Path.Combine("app", "org.openrgb.OpenRGB");
        string[] flatpakRoots = [System.IO.Path.Combine(roots.Home, ".local", "share", "flatpak"), roots.SystemFlatpak];
        if (flatpakRoots.Any(r => Directory.Exists(System.IO.Path.Combine(r, flatpakApp))) && roots.Path.Split(':').Any(d => IsExecutable(System.IO.Path.Combine(d, "flatpak"))))
            return new(OpenRgbKind.Flatpak, "flatpak");
        var snap = System.IO.Path.Combine(roots.SnapBin, "openrgb");
        if (File.Exists(snap)) return new(OpenRgbKind.Snap, snap);
        var own = OwnCopy(roots.DataFolder);
        return IsExecutable(own) ? new(OpenRgbKind.AppImage, own) : null;
    }

    private static bool IsExecutable(string path) =>
        File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>The program and arguments that run the server; the port only when it is not OpenRGB's own.</summary>
    public static List<string> ServerCommand(OpenRgbInstall install, int port)
    {
        var argv = new List<string>();
        switch (install.Kind)
        {
            case OpenRgbKind.Flatpak: argv.AddRange(["flatpak", "run", "org.openrgb.OpenRGB"]); break;
            case OpenRgbKind.AppImage: argv.AddRange([install.Path, "--appimage-extract-and-run"]); break;
            default: argv.Add(install.Path); break;
        }
        argv.Add("--server");
        if (port != OpenRgb.DefaultPort) argv.AddRange(["--server-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return argv;
    }

    /// <summary>The shell wrapper that detaches it: a new session where setsid exists, output discarded, no stdin.</summary>
    /// <remarks>Arguments are passed positionally, never joined into the script text.</remarks>
    public static (string Program, List<string> Args) Detached(List<string> command, bool setsid)
    {
        var args = new List<string> { "-c", setsid ? "setsid \"$@\" </dev/null >/dev/null 2>&1 &" : "\"$@\" </dev/null >/dev/null 2>&1 &", "dispctrl-openrgb" };
        args.AddRange(command);
        return ("/bin/sh", args);
    }

    /// <summary>Whether the SDK server answers (a full handshake, not just an open port).</summary>
    public static bool Answers(LightingSettings s)
    {
        try { using var rgb = OpenRgb.Connect(s.Host, s.Port); return true; }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or AggregateException) { return false; }
    }

    public sealed record Outcome(bool Ok, string Message);

    private static DateTime _lastAutoAttempt = DateTime.MinValue;

    /// <summary>Starts OpenRGB's server once, when lighting asked for it and it
    /// is allowed: autoStart on, a local server, an OpenRGB to run. A failed
    /// attempt is not repeated for a minute, so a broken install does not cost
    /// every command the wait.</summary>
    public static bool AutoStart(LightingSettings s)
    {
        if (!s.AutoStart || !IsLocal(s.Host) || SnapLimit() is not null || Find() is null) return false;
        if (DateTime.UtcNow - _lastAutoAttempt < TimeSpan.FromMinutes(1)) return false;
        _lastAutoAttempt = DateTime.UtcNow;
        return Start(s, null).Ok;
    }

    public static bool IsLocal(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Starts the server headless and waits until it answers.</summary>
    public static Outcome Start(LightingSettings s, Action<string>? progress, int waitSeconds = 40)
    {
        if (Answers(s)) return new(true, $"OpenRGB is already running at {s.Host}:{s.Port}.");
        if (SnapLimit() is { } limit) return new(false, limit);
        if (!IsLocal(s.Host)) return new(false, $"OpenRGB is set to {s.Host}:{s.Port}, another computer: start its server there, or dispctrl-linux rgb server 127.0.0.1");
        var install = Find();
        if (install is null) return new(false, "no OpenRGB found: dispctrl-linux rgb setup downloads the official one");
        progress?.Invoke($"Starting {install.Describe()}...");
        var (program, args) = Detached(ServerCommand(install, s.Port), Shell.TryWhich("setsid"));
        try
        {
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var shell = Process.Start(info);
            shell?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diagnostics.AppLog.Warn("openrgb", $"could not start {install.Describe()}: {ex.Message}");
            return new(false, $"could not start {install.Describe()}: {ex.Message}");
        }
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Answers(s))
            {
                progress?.Invoke("OpenRGB answers; waiting for its device scan...");
                SettleDevices(s);
                return new(true, $"Started {install.Describe()}; its server answers at {s.Host}:{s.Port}.");
            }
            Thread.Sleep(500);
        }
        return new(false, $"started {install.Describe()}, but its server did not answer at {s.Host}:{s.Port} within {waitSeconds} seconds "
            + "(an AppImage extracts itself on the first run; try again, or run it by hand to see its output)");
    }

    /// <summary>The server answers before it has scanned the hardware; a few seconds more lets the first command see devices.</summary>
    private static void SettleDevices(LightingSettings s)
    {
        for (int i = 0; i < 16; i++)
        {
            try { using var rgb = OpenRgb.Connect(s.Host, s.Port); if (rgb.Devices().Count > 0) return; }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or AggregateException) { }
            Thread.Sleep(500);
        }
    }

    /// <summary>Whether the pinned file's SHA-256 matches; a mismatch must never be installed.</summary>
    public static bool Verify(string file, string expectedSha256)
    {
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks the downloaded file and only then moves it into place, executable.</summary>
    public static Outcome Install(string downloaded, Asset asset, string target)
    {
        if (!Verify(downloaded, asset.Sha256))
        {
            File.Delete(downloaded);
            return new(false, $"the downloaded {asset.File} does not match the pinned SHA-256 {asset.Sha256}; it was deleted and not installed");
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Move(downloaded, target, overwrite: true);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return new(true, $"OpenRGB {Version} installed at {target} (SHA-256 verified).");
    }

    /// <summary>Downloads the official AppImage into the data folder, verifies it, and installs it.</summary>
    public static Outcome Download(Action<string>? progress, Asset? asset = null, string? dataFolder = null)
    {
        if (SnapLimit() is { } limit) return new(false, limit);
        asset ??= AssetFor(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        if (asset is null) return new(false, "OpenRGB publishes no AppImage for this architecture; install it from your distribution");
        string target = OwnCopy(dataFolder ?? DataFolder);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        string part = target + ".part";
        try
        {
            progress?.Invoke($"Downloading {asset.File} (about 37 MB)...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using (var response = http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var output = File.Create(part);
                response.Content.CopyToAsync(output).GetAwaiter().GetResult();
            }
            progress?.Invoke("Checking its SHA-256...");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            if (File.Exists(part)) File.Delete(part);
            Diagnostics.AppLog.Warn("openrgb", $"could not download OpenRGB: {ex.Message}");
            return new(false, $"could not download OpenRGB: {ex.Message}. Install it from your distribution or Flathub instead");
        }
        return Install(part, asset, target);
    }

    /// <summary>Where the rules udev needs for OpenRGB to open USB devices as a user may already be.</summary>
    public static bool UdevRulesInstalled(string? root = null)
    {
        foreach (var dir in root is null ? ["/etc/udev/rules.d", "/usr/lib/udev/rules.d", "/lib/udev/rules.d", "/run/udev/rules.d"] : new[] { root })
            if (File.Exists(System.IO.Path.Combine(dir, "60-openrgb.rules"))) return true;
        return false;
    }

    /// <summary>The one line that installs the rules (OpenRGB 1.0 writes them itself), for the person to run as root; never run here.</summary>
    public static string UdevCommand(OpenRgbInstall install)
    {
        const string Target = "/etc/udev/rules.d/60-openrgb.rules", Reload = "sudo udevadm control --reload-rules && sudo udevadm trigger";
        if (install.Kind == OpenRgbKind.Flatpak)
            return $"sudo sh -c 'flatpak run org.openrgb.OpenRGB --print-udev-rules > {Target}' && {Reload}";
        var run = string.Join(' ', ServerCommand(install, OpenRgb.DefaultPort).TakeWhile(a => a != "--server").Select(Quote));
        return $"sudo {run} --generate-udev-rules {Target} && {Reload}";
    }

    private static string Quote(string s) => s.All(c => char.IsLetterOrDigit(c) || "/._-+".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>What to say about the rules, or null when there is nothing to add.</summary>
    public static string? UdevAdvice(OpenRgbInstall? install)
    {
        if (Distro.InSnap || install is null || UdevRulesInstalled()) return null;
        return "USB lighting devices need OpenRGB's udev rules (not found). As root, once: " + UdevCommand(install)
            + " - then replug the devices or sign in again. DispCtrl does not run sudo itself";
    }
}
