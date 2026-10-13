using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Session;

/// <summary>The desktop's light or dark theme, read and set where the desktop
/// keeps it: GNOME's (and Budgie's, Cinnamon's, MATE's) gsettings, KDE's colour
/// scheme, XFCE's xfconf.</summary>
/// <remarks>
/// GNOME 42 and later read <c>color-scheme</c> for their own apps; GTK 3 apps
/// and the shell's theme still follow the GTK theme's name, so a theme with a
/// "-dark" sibling (Yaru, Adwaita, Yaru-purple) is switched with it, as
/// Ubuntu's own Appearance page does. A theme with no dark sibling is left as
/// it is rather than set to one that does not exist.
/// </remarks>
public static class DarkMode
{
    private const string GnomeInterface = "org.gnome.desktop.interface";

    /// <summary>True for dark, false for light, null when this desktop's theme
    /// cannot be read.</summary>
    public static bool? Read()
    {
        if (Gsettings(["get", GnomeInterface, "color-scheme"]) is { } scheme)
            return scheme.Contains("prefer-dark", StringComparison.Ordinal);
        if (Kde() is { } kde) return kde.Contains("dark", StringComparison.OrdinalIgnoreCase);
        if (Xfce() is { } xfce) return xfce.Contains("dark", StringComparison.OrdinalIgnoreCase);
        return null;
    }

    public static bool Set(bool dark, out string? error)
    {
        if (Gsettings(["get", GnomeInterface, "color-scheme"]) is not null)
        {
            var set = Shell.Run("gsettings", ["set", GnomeInterface, "color-scheme", dark ? "prefer-dark" : "default"], 5000);
            if (!set.Ok) { error = $"gsettings refused: {set.Stderr.Trim()}"; return false; }
            if (Gsettings(["get", GnomeInterface, "gtk-theme"]) is { } theme && Sibling(theme.Trim().Trim('\''), dark) is { } other)
                Shell.Run("gsettings", ["set", GnomeInterface, "gtk-theme", other], 5000);
            error = null;
            return true;
        }
        if (Shell.TryWhich("plasma-apply-colorscheme"))
        {
            var r = Shell.Run("plasma-apply-colorscheme", [dark ? "BreezeDark" : "BreezeLight"], 10_000);
            error = r.Ok ? null : r.Stderr.Trim();
            return r.Ok;
        }
        if (Xfce() is { } xfce && Sibling(xfce, dark) is { } next)
        {
            var r = Shell.Run("xfconf-query", ["-c", "xsettings", "-p", "/Net/ThemeName", "-s", next], 5000);
            error = r.Ok ? null : r.Stderr.Trim();
            return r.Ok;
        }
        error = "this desktop keeps its theme somewhere DispCtrl cannot reach (GNOME, KDE and XFCE are supported)";
        return false;
    }

    /// <summary>The dark or light sibling of a GTK theme name, or null when it
    /// is already that or has none DispCtrl can be sure of.</summary>
    internal static string? Sibling(string theme, bool dark)
    {
        bool isDark = theme.EndsWith("-dark", StringComparison.OrdinalIgnoreCase);
        if (dark == isDark) return null;
        if (dark) return KnownLight(theme) ? $"{theme}-dark" : null;
        return theme[..^"-dark".Length];

        static bool KnownLight(string t) =>
            t.StartsWith("Yaru", StringComparison.Ordinal) || t is "Adwaita" or "Greybird" or "Arc" or "Pop" or "Mint-Y" || t.StartsWith("Mint-Y-", StringComparison.Ordinal);
    }

    private static string? Gsettings(string[] args)
    {
        if (!Shell.TryWhich("gsettings")) return null;
        var r = Shell.Run("gsettings", args, 3000);
        return r.Ok ? r.Stdout.Trim() : null;
    }

    private static string? Kde()
    {
        if (!Shell.TryWhich("kreadconfig6") && !Shell.TryWhich("kreadconfig5")) return null;
        var tool = Shell.TryWhich("kreadconfig6") ? "kreadconfig6" : "kreadconfig5";
        var r = Shell.Run(tool, ["--file", "kdeglobals", "--group", "General", "--key", "ColorScheme"], 3000);
        return r.Ok && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
    }

    private static string? Xfce()
    {
        if (!Shell.TryWhich("xfconf-query")) return null;
        var r = Shell.Run("xfconf-query", ["-c", "xsettings", "-p", "/Net/ThemeName"], 3000);
        return r.Ok ? r.Stdout.Trim() : null;
    }
}
