using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    public const string Repository = "https://github.com/HaythmKenway/DispCtrl-linux";

    /// <summary><c>update</c>: whether a newer release is out. Only this
    /// request, with the version; nothing is downloaded.</summary>
    private static int UpdateCommand(string[] args, CommandContext c)
    {
        if (args is not ([] or ["check"])) return Wrong(c, "update takes check");
        if (Distro.InSnap) c.Out.WriteLine("The Snap Store updates the snap by itself (sudo snap refresh dispctrl-linux to do it now).");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"dispctrl-linux/{Version}");
            var json = http.GetStringAsync("https://api.github.com/repos/HaythmKenway/DispCtrl-linux/releases/latest").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v');
            bool newer = System.Version.TryParse(latest, out var l) && System.Version.TryParse(Version, out var mine) && l > mine;
            c.Out.WriteLine(newer
                ? $"DispCtrl for Linux {latest} is out (this is {Version}): {Repository}/releases/latest"
                : $"This is the newest release ({Version}).");
            return Done;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            c.Out.WriteLine($"No release has been published yet: {Repository}/releases");
            return Done;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return Refuse(c, $"the releases could not be read ({ex.GetBaseException().Message})");
        }
    }

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}".ToLowerInvariant();

    /// <summary>Takes out what identifies the person or the hardware; the
    /// log goes through the same rules (<see cref="Diagnostics.Redact"/>).</summary>
    internal static string Scrub(string text) => Diagnostics.Redact.Scrub(text);

    /// <summary>Where a kept wallpaper lives: the snap's own folder in the home
    /// directory, or DispCtrl's data folder.</summary>
    public static string WallpaperFolder()
    {
        var root = Environment.GetEnvironmentVariable("SNAP_USER_COMMON") is { Length: > 0 } common
            ? common
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
                ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "dispctrl-linux");
        return Path.Combine(root, "wallpapers");
    }

    /// <summary>The picture where GNOME can read it. In the snap a path chosen
    /// elsewhere (a portal's /run/user/…/doc grant, /tmp, another snap's
    /// folder) is not one GNOME can open, so it is copied into the wallpaper
    /// folder; a picture already there is left alone.</summary>
    public static string KeepPicture(string file)
    {
        if (!Distro.InSnap) return file;
        var folder = WallpaperFolder();
        if (Path.GetDirectoryName(file) == folder) return file;
        Directory.CreateDirectory(folder);
        // One kept at a time: the desktop holds a copy's path, not the copy.
        foreach (var old in Directory.GetFiles(folder)) File.Delete(old);
        var target = Path.Combine(folder, Path.GetFileName(file));
        File.Copy(file, target, overwrite: true);
        return target;
    }

    /// <summary><c>wallpaper FILE</c>: the desktop's picture. GNOME keeps one
    /// for every display; KDE and XFCE are set on each.</summary>
    private static int WallpaperCommand(string[] args, CommandContext c)
    {
        if (args.Length != 1) return Wrong(c, "wallpaper takes a picture's path");
        var file = Path.GetFullPath(args[0]);
        if (!File.Exists(file)) return Wrong(c, $"no picture at {file}");
        if (Shell.TryWhich("gsettings") && Shell.Run("gsettings", ["get", "org.gnome.desktop.background", "picture-uri"], 3000).Ok)
        {
            string kept;
            try { kept = KeepPicture(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refuse(c, $"the picture could not be kept where GNOME can read it: {ex.Message}"); }
            var uri = new Uri(kept).AbsoluteUri;
            var set = Shell.Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri", uri], 5000);
            Shell.Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri-dark", uri], 5000);
            // "none" shows no picture at all, whatever the URI says.
            if (Shell.Run("gsettings", ["get", "org.gnome.desktop.background", "picture-options"], 3000).Stdout.Trim() == "'none'")
                Shell.Run("gsettings", ["set", "org.gnome.desktop.background", "picture-options", "zoom"], 5000);
            Diagnostics.AppLog.Info("wallpaper", set.Ok ? "set through GNOME" : $"GNOME refused: {set.Stderr.Trim()}");
            return set.Ok ? Write(c.Out, "Wallpaper set (GNOME shows one picture on every display).")
                : Refuse(c, $"GNOME refused the picture{(set.Stderr.Trim() is { Length: > 0 } why ? $": {why}" : "")}");
        }
        if (Shell.TryWhich("plasma-apply-wallpaperimage"))
        {
            var r = Shell.Run("plasma-apply-wallpaperimage", [file], 10_000);
            return r.Ok ? Write(c.Out, "Wallpaper set on every display.") : Refuse(c, r.Stderr.Trim());
        }
        if (Shell.TryWhich("xfconf-query"))
        {
            var list = Shell.Run("xfconf-query", ["-c", "xfce4-desktop", "-l"], 5000);
            int n = 0;
            foreach (var prop in list.Stdout.Split('\n').Where(p => p.EndsWith("/last-image", StringComparison.Ordinal)))
                if (Shell.Run("xfconf-query", ["-c", "xfce4-desktop", "-p", prop.Trim(), "-s", file], 5000).Ok) n++;
            return n > 0 ? Write(c.Out, $"Wallpaper set on {n} display(s).") : Refuse(c, "XFCE listed no desktop to set");
        }
        return Refuse(c, "this desktop keeps its wallpaper where DispCtrl cannot reach it (GNOME, KDE and XFCE can be)");
    }
}
