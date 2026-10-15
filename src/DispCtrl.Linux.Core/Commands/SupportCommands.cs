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

    /// <summary><c>report</c>: what a problem report needs, with serials, the
    /// account name and home folder removed; <c>--open</c> starts a GitHub
    /// issue with it for the person to read before sending.</summary>
    private static int ReportCommand(string[] args, CommandContext c)
    {
        if (!TrySplit(args, out var verbs, out var flags, c)) return AskedWrongly;
        if (verbs.Length > 0) return Wrong(c, "report takes --what \"what happened\" and --open");
        var doctor = new StringWriter();
        Doctor(c with { Out = doctor });
        var text = new System.Text.StringBuilder();
        text.AppendLine($"dispctrl-linux {Version}, {(Distro.InSnap ? "snap" : "package")}, {Distro.Family}, {RuntimeInformationText()}");
        text.AppendLine($"session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")} / {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")}");
        text.AppendLine();
        text.Append(doctor);
        var report = Scrub(text.ToString());
        string what = flags.GetValueOrDefault("--what") ?? "";
        c.Out.WriteLine(report);
        if (!flags.ContainsKey("--open")) return Write(c.Out, "Nothing was sent. report --open --what \"...\" starts a GitHub issue with this, for you to read first.");
        var body = $"**What happened**\n\n{what}\n\n**Diagnostics** (from dispctrl-linux report)\n\n```\n{report}\n```";
        var url = $"{Repository}/issues/new?title={Uri.EscapeDataString(what.Length > 0 ? what : "Problem report")}&body={Uri.EscapeDataString(body)}";
        if (url.Length > 7500) url = url[..7500];
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { c.Out.WriteLine(url); }
        return Done;
    }

    private static string RuntimeInformationText() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}".ToLowerInvariant();

    /// <summary>Takes out what identifies the person or the hardware: the home
    /// folder and account name, serial numbers, and DDC/CI bus paths.</summary>
    internal static string Scrub(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.Ordinal);
        var user = Environment.UserName;
        if (user.Length > 2) text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "<user>");
        text = Regex.Replace(text, @"(?<=\|)[^|\s]{4,}", "<serial>");
        return text;
    }

    /// <summary><c>wallpaper FILE</c>: the desktop's picture. GNOME keeps one
    /// for every display; KDE and XFCE are set on each.</summary>
    private static int WallpaperCommand(string[] args, CommandContext c)
    {
        if (args.Length != 1) return Wrong(c, "wallpaper takes a picture's path");
        var file = Path.GetFullPath(args[0]);
        if (!File.Exists(file)) return Wrong(c, $"no picture at {file}");
        var uri = new Uri(file).AbsoluteUri;
        if (Shell.TryWhich("gsettings") && Shell.Run("gsettings", ["get", "org.gnome.desktop.background", "picture-uri"], 3000).Ok)
        {
            bool ok = Shell.Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri", uri], 5000).Ok;
            Shell.Run("gsettings", ["set", "org.gnome.desktop.background", "picture-uri-dark", uri], 5000);
            return ok ? Write(c.Out, "Wallpaper set (GNOME shows one picture on every display).") : Refuse(c, "GNOME refused the picture");
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
