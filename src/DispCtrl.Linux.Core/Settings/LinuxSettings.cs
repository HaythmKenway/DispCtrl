using System.Text.Json;
using System.Text.Json.Serialization;
using DispCtrl.Linux.Ramps;

namespace DispCtrl.Linux.Settings;

/// <summary>Everything the Linux client stores. Hardware levels (DDC/CI,
/// backlight) are not stored: the monitor holds them itself.</summary>
public sealed class LinuxSettings
{
    public NightLightSettings NightLight { get; set; } = new();

    /// <summary>Software dimming per XRandR output, 0.1 to 1. Absent means 1.</summary>
    public Dictionary<string, double> Dim { get; set; } = new(StringComparer.Ordinal);

    public SnapSettings Snap { get; set; } = new();

    public AwakeSettings Awake { get; set; } = new();

    public DarkModeSettings DarkMode { get; set; } = new();

    public TraySettings Tray { get; set; } = new();

    public UnisonSettings Unison { get; set; } = new();

    public LightingSettings Lighting { get; set; } = new();

    public OledCareSettings OledCare { get; set; } = new();

    public FocusSettings Focus { get; set; } = new();

    public MonitorSleepSettings MonitorSleep { get; set; } = new();

    public RoomLightSettings RoomLight { get; set; } = new();

    /// <summary>Global shortcuts, each running a dispctrl-linux command.
    /// Null until first read: then <see cref="HotkeyBinding.Defaults"/>.</summary>
    public List<HotkeyBinding>? Hotkeys { get; set; }

    /// <summary>Named lists of steps, run from a trigger, a hotkey or the command line.</summary>
    public List<Feature> Features { get; set; } = [];

    /// <summary>When something happens, run a feature.</summary>
    public List<Trigger> Triggers { get; set; } = [];

    /// <summary>Apply a preset while an app is in front.</summary>
    public List<AppRule> AppRules { get; set; } = [];

    /// <summary>Saved desks, in the order they were made.</summary>
    public List<Preset> Presets { get; set; } = [];

    /// <summary>Properties a newer build wrote: kept and saved back untouched.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Pulls every value back inside its range, so a hand edit can
    /// never ask for a black screen or an unreadable time.</summary>
    public void Normalise()
    {
        NightLight ??= new NightLightSettings();
        NightLight.Normalise();
        Snap ??= new SnapSettings();
        Snap.Normalise();
        Awake ??= new AwakeSettings();
        Awake.Normalise();
        DarkMode ??= new DarkModeSettings();
        Tray ??= new TraySettings();
        Hotkeys ??= HotkeyBinding.Defaults();
        Hotkeys.RemoveAll(h => h is null || string.IsNullOrWhiteSpace(h.Keys) || string.IsNullOrWhiteSpace(h.Command));
        Features ??= [];
        Features.RemoveAll(f => f is null || string.IsNullOrWhiteSpace(f.Name));
        Triggers ??= [];
        Triggers.RemoveAll(t => t is null || !Trigger.Events.Contains(t.Event));
        AppRules ??= [];
        AppRules.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.App) || string.IsNullOrWhiteSpace(r.Preset));
        foreach (var r in AppRules) r.DelaySeconds = Math.Clamp(r.DelaySeconds, 0, 600);
        Lighting ??= new LightingSettings();
        OledCare ??= new OledCareSettings();
        OledCare.Normalise();
        Focus ??= new FocusSettings();
        Focus.DimPercent = Math.Clamp(Focus.DimPercent, 10, 90);
        RoomLight ??= new RoomLightSettings();
        RoomLight.Min = Math.Clamp(RoomLight.Min, 0, 100);
        RoomLight.Max = Math.Clamp(RoomLight.Max, RoomLight.Min, 100);
        MonitorSleep ??= new MonitorSleepSettings();
        MonitorSleep.Minutes = Math.Clamp(MonitorSleep.Minutes, 1, 600);
        Unison ??= new UnisonSettings();
        Unison.Normalise();
        Presets ??= [];
        Presets.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        foreach (var preset in Presets) preset.Normalise();
        Dim ??= new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in Dim.Keys.ToList())
        {
            double value = Dim[key];
            if (double.IsNaN(value) || value >= 1) Dim.Remove(key);
            else Dim[key] = Math.Max(RampTarget.LowestDim, value);
        }
    }
}

public sealed class NightLightSettings
{
    public const int DefaultStrength = 50;

    public bool Enabled { get; set; }

    /// <summary>0 to 100: 6500 K to 1900 K (<see cref="Warmth.KelvinFor"/>).</summary>
    public int Strength { get; set; } = DefaultStrength;

    /// <summary>On: warm only between <see cref="From"/> and <see cref="To"/>.
    /// Off: warm whenever <see cref="Enabled"/>.</summary>
    public bool Scheduled { get; set; }

    public string From { get; set; } = "20:00";
    public string To { get; set; } = "07:00";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public void Normalise()
    {
        Strength = Math.Clamp(Strength, 0, 100);
        if (!Schedule.TryParse(From, out var from)) from = new TimeOnly(20, 0);
        if (!Schedule.TryParse(To, out var to)) to = new TimeOnly(7, 0);
        From = Schedule.Format(from);
        To = Schedule.Format(to);
    }

    /// <summary>Whether warmth applies at <paramref name="now"/>.</summary>
    public bool ActiveAt(TimeOnly now)
    {
        if (!Enabled || Strength == 0) return false;
        if (!Scheduled) return true;
        Schedule.TryParse(From, out var from);
        Schedule.TryParse(To, out var to);
        return Schedule.IsActive(now, from, to);
    }
}

/// <summary>Snap layouts and Snap Assist (Linux only, X11).</summary>
public sealed class SnapSettings
{
    /// <summary>The engine watches for drags and holds the shortcut.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dragging a window to the top centre of a display drops the
    /// layouts down, as on Windows 11.</summary>
    public bool DragToTop { get; set; } = true;

    /// <summary>The shortcut that opens the layouts for the window in front, in
    /// the form Super+Z, Ctrl+Alt+S. Empty: none.</summary>
    public string Shortcut { get; set; } = "Super+Z";

    /// <summary>After a window snaps, offer the other windows for the zones left.</summary>
    public bool Assist { get; set; } = true;

    /// <summary>Pixels between snapped windows and around them, 0 to 32.</summary>
    public int Gap { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public void Normalise()
    {
        Gap = Math.Clamp(Gap, 0, 32);
        Shortcut = (Shortcut ?? "").Trim();
    }
}

/// <summary>Keep awake and Stay active (Screen care on Windows).</summary>
public sealed class AwakeSettings
{
    /// <summary>Off; on until switched off; or on until <see cref="Until"/>.</summary>
    public AwakeMode Mode { get; set; }

    /// <summary>When a timed keep awake ends, as local time in ISO 8601.</summary>
    public DateTimeOffset? Until { get; set; }

    /// <summary>Also keep the displays from blanking and the screen from
    /// locking, not only the computer from sleeping.</summary>
    public bool KeepDisplaysOn { get; set; } = true;

    /// <summary>Nudge the pointer by a pixel and back after 55 seconds without
    /// it moving, so apps that watch for activity (chat status, remote
    /// sessions) see someone there. The "mouse jiggler".</summary>
    public bool StayActive { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Whether the computer is to be kept awake at <paramref name="now"/>.</summary>
    public bool ActiveAt(DateTimeOffset now) => Mode switch
    {
        AwakeMode.On => true,
        AwakeMode.Until => Until is { } until && now < until,
        _ => false,
    };

    public void Normalise()
    {
        if (!Enum.IsDefined(Mode)) Mode = AwakeMode.Off;
        if (Mode == AwakeMode.Until && Until is null) Mode = AwakeMode.Off;
        if (Mode != AwakeMode.Until) Until = null;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<AwakeMode>))]
public enum AwakeMode { Off, On, Until }

/// <summary>Dark mode. Whether it is on now is the desktop's own setting,
/// read each time, so a change made in GNOME's Settings is never undone.</summary>
public sealed class DarkModeSettings
{
    /// <summary>Dark between night light's hours and light outside them,
    /// switched at each end of the schedule only.</summary>
    public bool FollowSchedule { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>The icon in the panel's tray and its quick panel.</summary>
public sealed class TraySettings
{
    /// <summary>Show the icon from sign-in on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A left click opens the quick panel; off, it opens the window.</summary>
    public bool QuickPanel { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>One brightness for every display, each within its own range: the
/// Windows app's unison slider.</summary>
public sealed class UnisonSettings
{
    /// <summary>Brightness keys and the tray's wheel move every display
    /// together through <see cref="Level"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>0 to 100, the last level set.</summary>
    public int Level { get; set; } = 60;

    /// <summary>Each display's own lowest and highest, by target key
    /// (<c>backlight:NAME</c>, <c>ddc:MODEL|SERIAL</c>). Absent: 0 to 100.</summary>
    public Dictionary<string, UnisonRange> Ranges { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Displays unison leaves alone, by target key.</summary>
    public List<string> Excluded { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public UnisonRange RangeOf(string key) => Ranges.GetValueOrDefault(key) ?? new UnisonRange();

    public void Normalise()
    {
        Level = Math.Clamp(Level, 0, 100);
        Ranges ??= new(StringComparer.Ordinal);
        Excluded ??= [];
        foreach (var r in Ranges.Values)
        {
            r.Min = Math.Clamp(r.Min, 0, 100);
            r.Max = Math.Clamp(r.Max, r.Min, 100);
        }
    }
}

public sealed class UnisonRange
{
    public int Min { get; set; }
    public int Max { get; set; } = 100;
}

/// <summary>A global shortcut and the dispctrl-linux command it runs, such as
/// Ctrl+Alt+N and <c>nightlight toggle</c>.</summary>
public sealed class HotkeyBinding
{
    public string Keys { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>The Windows app's defaults, moved where Linux desktops already
    /// use the key: Ctrl+Alt+L locks many of them, so displays off is O, and
    /// Ctrl+Alt+Backspace can end an X session, so the way back is R.</summary>
    public static List<HotkeyBinding> Defaults() =>
    [
        new() { Keys = "Ctrl+Alt+Page Up", Command = "brightness up" },
        new() { Keys = "Ctrl+Alt+Page Down", Command = "brightness down" },
        new() { Keys = "Ctrl+Alt+N", Command = "nightlight toggle" },
        new() { Keys = "Ctrl+Alt+D", Command = "tray show" },
        new() { Keys = "Ctrl+Alt+O", Command = "displays-off" },
        new() { Keys = "Ctrl+Alt+R", Command = "restore" },
        new() { Keys = "Ctrl+Alt+K", Command = "awake toggle" },
        new() { Keys = "Ctrl+Alt+P", Command = "pin toggle" },
        new() { Keys = "Ctrl+Alt+G", Command = "placement gather" },
    ];
}

/// <summary>A named list of steps: dispctrl-linux commands, <c>wait MS</c>, or
/// <c>run PROGRAM ARGS</c>.</summary>
public sealed class Feature
{
    public string Name { get; set; } = "";
    public List<string> Steps { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>When <see cref="Event"/> happens (and <see cref="Match"/> fits),
/// run <see cref="Feature"/>.</summary>
public sealed class Trigger
{
    /// <summary>The events the engine watches for.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "display-connected", "display-disconnected", "app-in-front", "app-left", "idle", "back",
        "locked", "unlocked", "on-battery", "on-power", "resumed", "at-time",
    ];

    public string Event { get; set; } = "";

    /// <summary>An output or monitor name for display events, an app's class
    /// for app events, HH:MM for at-time; empty for any.</summary>
    public string? Match { get; set; }

    /// <summary>Minutes without input, for idle.</summary>
    public int Minutes { get; set; } = 10;

    public string Feature { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>While <see cref="App"/> is in front, <see cref="Preset"/> applies;
/// when it leaves, the desk goes back, stays, or takes another preset.</summary>
public sealed class AppRule
{
    /// <summary>The app's window class, as <c>windows</c> lists it (firefox, steam, vlc).</summary>
    public string App { get; set; } = "";
    public string Preset { get; set; } = "";

    /// <summary>"previous" (the desk as it was), "keep", or a preset's name.</summary>
    public string Then { get; set; } = "previous";

    /// <summary>How long it must stay in front first, so passing through it does not.</summary>
    public int DelaySeconds { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>RGB lighting through OpenRGB's SDK server.</summary>
public sealed class LightingSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;

    /// <summary>What DispCtrl last set on every device: a colour (#RRGGBB),
    /// "off", or "mode:NAME"; what a preset saves.</summary>
    public string? Last { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>OLED care: the displays dim, in two stages, when nothing has been
/// touched for a while, so a still picture does not stay lit at full strength.</summary>
public sealed class OledCareSettings
{
    public bool Enabled { get; set; }
    public int IdleMinutes { get; set; } = 4;
    public int DimPercent { get; set; } = 50;
    public bool SecondStage { get; set; } = true;
    public int SecondMinutes { get; set; } = 10;
    public int SecondDimPercent { get; set; } = 90;

    /// <summary>Not while a window is full screen in front (a film, a game).</summary>
    public bool PauseFullscreen { get; set; } = true;

    /// <summary>Not while a media player says it is playing (MPRIS).</summary>
    public bool PauseMedia { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public void Normalise()
    {
        IdleMinutes = Math.Clamp(IdleMinutes, 1, 120);
        SecondMinutes = Math.Clamp(SecondMinutes, IdleMinutes + 1, 240);
        DimPercent = Math.Clamp(DimPercent, 10, 90);
        SecondDimPercent = Math.Clamp(SecondDimPercent, DimPercent, 95);
    }
}

/// <summary>Focus mode: everything but the window in front is dimmed.</summary>
public sealed class FocusSettings
{
    public bool Enabled { get; set; }
    public int DimPercent { get; set; } = 60;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Monitor sleep: the displays switch off (DPMS) after this long idle.</summary>
public sealed class MonitorSleepSettings
{
    public bool Enabled { get; set; }
    public int Minutes { get; set; } = 30;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Brightness that follows the room's light, through unison.</summary>
public sealed class RoomLightSettings
{
    public bool Enabled { get; set; }

    /// <summary>Unison's level in the dark, and in bright light.</summary>
    public int Min { get; set; } = 20;
    public int Max { get; set; } = 100;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
