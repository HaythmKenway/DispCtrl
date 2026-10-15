using System.Globalization;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Session;

/// <summary>
/// Night light on Wayland, where the compositor owns the ramps: DispCtrl's
/// settings are handed to the desktop's own night light, GNOME's or KDE's.
/// </summary>
/// <remarks>
/// GNOME's schedule is in hours as a decimal (20.5 is 20:30) and its warmth in
/// kelvin, which DispCtrl's strength maps to exactly; KDE's is read by KWin
/// from kwinrc and reloaded. Software dimming has no such route and stays an
/// X11 feature.
/// </remarks>
public static class DesktopNightLight
{
    private const string Gnome = "org.gnome.settings-daemon.plugins.color";

    public static bool Available => GnomeAvailable || Shell.TryWhich("kwriteconfig6") || Shell.TryWhich("kwriteconfig5");

    private static bool GnomeAvailable =>
        Shell.TryWhich("gsettings") && Shell.Run("gsettings", ["get", Gnome, "night-light-enabled"], 3000).Ok;

    /// <summary>Sets the desktop's night light to <paramref name="n"/>; the
    /// reason when it cannot.</summary>
    public static string? Apply(NightLightSettings n)
    {
        int kelvin = (int)Math.Round(Warmth.KelvinFor(n.Strength));
        Schedule.TryParse(n.From, out var from);
        Schedule.TryParse(n.To, out var to);
        if (GnomeAvailable)
        {
            string Hours(TimeOnly t) => (t.Hour + t.Minute / 60.0).ToString("0.##", CultureInfo.InvariantCulture);
            bool ok = Set("night-light-enabled", n.Enabled ? "true" : "false")
                & Set("night-light-temperature", $"uint32 {kelvin}")
                & Set("night-light-schedule-automatic", "false")
                & Set("night-light-schedule-from", n.Scheduled ? Hours(from) : "0")
                & Set("night-light-schedule-to", n.Scheduled ? Hours(to) : "0");
            return ok ? null : "GNOME refused its night light settings";

            static bool Set(string key, string value) => Shell.Run("gsettings", ["set", Gnome, key, value], 3000).Ok;
        }
        var tool = Shell.TryWhich("kwriteconfig6") ? "kwriteconfig6" : Shell.TryWhich("kwriteconfig5") ? "kwriteconfig5" : null;
        if (tool is null) return "on Wayland night light goes through the desktop's own, and this one is neither GNOME nor KDE";
        void Kde(string key, string value) => Shell.Run(tool, ["--file", "kwinrc", "--group", "NightColor", "--key", key, value], 3000);
        Kde("Active", n.Enabled ? "true" : "false");
        Kde("Mode", n.Scheduled ? "Times" : "Constant");
        Kde("NightTemperature", kelvin.ToString(CultureInfo.InvariantCulture));
        Kde("EveningBeginFixed", from.ToString("HHmm", CultureInfo.InvariantCulture));
        Kde("MorningBeginFixed", to.ToString("HHmm", CultureInfo.InvariantCulture));
        foreach (var bus in new[] { "qdbus6", "qdbus" })
            if (Shell.TryWhich(bus)) { Shell.Run(bus, ["org.kde.KWin", "/KWin", "reconfigure"], 5000); break; }
        return null;
    }
}
