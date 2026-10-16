using DispCtrl.Linux.Hardware;

namespace DispCtrl.Linux.Graphics;

/// <summary>The desktop's own look - dark or light, its accent colour, its
/// interface font - so the overlays look like part of it rather than a
/// foreign window.</summary>
public sealed record DesktopTheme(bool Dark, Rgba Accent, string Font)
{
    public Rgba PanelBackground => Dark ? Rgba.Hex("#2c2c2c", 0.97) : Rgba.Hex("#fafafa", 0.97);
    public Rgba PanelBorder => Dark ? new Rgba(1, 1, 1, 0.07) : new Rgba(0, 0, 0, 0.07);
    public Rgba TileBackground => Dark ? new Rgba(0, 0, 0, 0.22) : new Rgba(0, 0, 0, 0.04);
    public Rgba TileBorder => Dark ? new Rgba(1, 1, 1, 0.05) : new Rgba(0, 0, 0, 0.05);
    public Rgba ZoneFill => Dark ? new Rgba(1, 1, 1, 0.12) : new Rgba(0, 0, 0, 0.08);
    public Rgba ZoneBorder => Dark ? new Rgba(1, 1, 1, 0.10) : new Rgba(0, 0, 0, 0.09);

    /// <summary>Snap Assist's backdrop: Windows 11 draws it as acrylic, a
    /// translucent wash the desktop shows through; cairo has no blur, so a
    /// lighter, more transparent fill stands in for it.</summary>
    public Rgba AssistBackdrop => Dark ? Rgba.Hex("#202020", 0.66) : Rgba.Hex("#f3f3f3", 0.62);
    public Rgba Text => Dark ? new Rgba(1, 1, 1, 0.92) : new Rgba(0, 0, 0, 0.88);
    public Rgba SecondaryText => Dark ? new Rgba(1, 1, 1, 0.62) : new Rgba(0, 0, 0, 0.58);
    public Rgba CardBackground => Dark ? Rgba.Hex("#3a3a3a") : Rgba.Hex("#ffffff");
    /// <summary>A shadow strong enough to lift the panel off a dark desktop is a
    /// grey halo on a light one.</summary>
    public double ShadowAlpha => Dark ? 0.35 : 0.14;
    public Rgba Backdrop => new(0, 0, 0, Dark ? 0.30 : 0.18);

    public static DesktopTheme Fallback { get; } = new(true, Rgba.Hex("#3584e4"), "Sans 10");

    /// <summary>Read once per overlay from wherever this desktop keeps its look:
    /// GNOME and Budgie's gsettings, Cinnamon's and MATE's own schemas, KDE's
    /// kdeglobals, XFCE's xfconf. Anything missing falls back to a dark theme
    /// and the desktop's usual blue.</summary>
    public static DesktopTheme Current()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        bool Is(string name) => desktop.Contains(name, StringComparison.OrdinalIgnoreCase);

        string? Gs(string schema, string key)
        {
            if (!Shell.TryWhich("gsettings")) return null;
            var r = Shell.Run("gsettings", ["get", schema, key], 2000);
            return r.Ok ? r.Stdout.Trim().Trim('\'') : null;
        }
        string? Kde(string group, string key)
        {
            var tool = Shell.TryWhich("kreadconfig6") ? "kreadconfig6" : Shell.TryWhich("kreadconfig5") ? "kreadconfig5" : null;
            if (tool is null) return null;
            var r = Shell.Run(tool, ["--file", "kdeglobals", "--group", group, "--key", key], 2000);
            return r.Ok && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
        }
        string? Xfce(string path)
        {
            if (!Shell.TryWhich("xfconf-query")) return null;
            var r = Shell.Run("xfconf-query", ["-c", "xsettings", "-p", path], 2000);
            return r.Ok && r.Stdout.Trim().Length > 0 ? r.Stdout.Trim() : null;
        }

        string? scheme = null, gtkTheme = null, font = null, accentName = null;
        Rgba? accent = null;
        if (Is("KDE"))
        {
            gtkTheme = Kde("General", "ColorScheme");
            font = FontFromQt(Kde("General", "font"));
            accent = AccentFromRgb(Kde("General", "AccentColor"));
        }
        else if (Is("XFCE"))
        {
            gtkTheme = Xfce("/Net/ThemeName");
            font = Xfce("/Gtk/FontName");
        }
        else
        {
            var (schema, themeKey) = Is("X-Cinnamon") || Is("Cinnamon") ? ("org.cinnamon.desktop.interface", "gtk-theme")
                : Is("MATE") ? ("org.mate.interface", "gtk-theme")
                : ("org.gnome.desktop.interface", "gtk-theme");
            gtkTheme = Gs(schema, themeKey);
            font = Gs(schema, "font-name");
            if (schema.StartsWith("org.gnome", StringComparison.Ordinal))
            {
                scheme = Gs(schema, "color-scheme");
                accentName = Gs(schema, "accent-color");
            }
        }

        bool dark = scheme == "prefer-dark" || (gtkTheme?.Contains("dark", StringComparison.OrdinalIgnoreCase) ?? false);
        return new DesktopTheme(dark, accent ?? AccentFrom(accentName, gtkTheme ?? "", Is("KDE")), font ?? Fallback.Font);
    }

    /// <summary>Qt stores a font as "Noto Sans,10,-1,5,..."; cairo wants "Noto Sans 10".</summary>
    internal static string? FontFromQt(string? qt)
    {
        var parts = qt?.Split(',');
        if (parts is not { Length: >= 2 } || parts[0].Length == 0) return null;
        return double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0
            ? $"{parts[0]} {size.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : parts[0];
    }

    /// <summary>Plasma 6 keeps the accent as "r,g,b" under General/AccentColor.</summary>
    internal static Rgba? AccentFromRgb(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 3 }) return null;
        var c = new double[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], out var n) || n is < 0 or > 255) return null;
            else c[i] = n / 255.0;
        return new Rgba(c[0], c[1], c[2]);
    }

    // GNOME 47's accent names, and Yaru's variants (Yaru-blue-dark ...), which is
    // where Ubuntu 24.04 keeps the accent.
    private static readonly Dictionary<string, string> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blue"] = "#3584e4", ["teal"] = "#2190a4", ["green"] = "#3a944a", ["yellow"] = "#c88800",
        ["orange"] = "#ed5b00", ["red"] = "#e62d42", ["pink"] = "#d56199", ["purple"] = "#9141ac",
        ["slate"] = "#6f8396", ["bark"] = "#787859", ["sage"] = "#657b69", ["olive"] = "#4b8501",
        ["viridian"] = "#03875b", ["prussiangreen"] = "#308280", ["magenta"] = "#b34cb3",
    };

    internal static Rgba AccentFrom(string? accentColour, string gtkTheme, bool kde = false)
    {
        if (accentColour is not null && Accents.TryGetValue(accentColour, out var hex)) return Rgba.Hex(hex);
        if (gtkTheme.StartsWith("Yaru", StringComparison.OrdinalIgnoreCase))
        {
            var parts = gtkTheme.Split('-');
            if (parts.Length > 1 && Accents.TryGetValue(parts[1], out var yaru)) return Rgba.Hex(yaru);
            return Rgba.Hex("#e95420"); // Yaru's default, Ubuntu orange
        }
        return Rgba.Hex(kde ? "#3daee9" : "#3584e4"); // Breeze blue, GNOME blue
    }
}
