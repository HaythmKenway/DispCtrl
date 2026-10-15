using System.Globalization;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Ramps;
using DispCtrl.Linux.Session;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>awake</c>: keep the computer (and its displays) awake, for
    /// good, for a while or until a time; and Stay active.</summary>
    private static int AwakeCommand(string[] args, CommandContext c)
    {
        var now = DateTimeOffset.Now;
        switch (args)
        {
            case [] or ["status"]:
                return AwakeStatus(c);
            case ["on" or "true"]:
                SettingsStore.Update(s => { s.Awake.Mode = AwakeMode.On; s.Awake.Until = null; });
                c.Out.WriteLine("Keeping the computer awake until you switch it off.");
                return AwakeNote(c);
            case ["toggle"]:
                return AwakeCommand([SettingsStore.Load().Awake.ActiveAt(now) ? "off" : "on"], c);
            case ["off" or "false"]:
                SettingsStore.Update(s => { s.Awake.Mode = AwakeMode.Off; s.Awake.Until = null; });
                c.Out.WriteLine("Keep awake off: the computer sleeps as its power settings say.");
                return Done;
            case ["for", var span]:
                if (!TryDuration(span, out var duration))
                    return Wrong(c, "awake for takes a time such as 30m, 2h or 1h30m (minutes when no unit is given)");
                var until = now + duration;
                SettingsStore.Update(s => { s.Awake.Mode = AwakeMode.Until; s.Awake.Until = until; });
                c.Out.WriteLine($"Keeping the computer awake until {until:HH:mm}.");
                return AwakeNote(c);
            case ["until", var time]:
                if (!Schedule.TryParse(time, out var at)) return Wrong(c, "awake until takes a time such as 18:30");
                var end = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);
                if (end <= now) end = end.AddDays(1);
                SettingsStore.Update(s => { s.Awake.Mode = AwakeMode.Until; s.Awake.Until = end; });
                c.Out.WriteLine($"Keeping the computer awake until {end:HH:mm}{(end.Date != now.Date ? " tomorrow" : "")}.");
                return AwakeNote(c);
            case ["displays", var v]:
                if (!TryOnOff(v, out bool displays)) return Wrong(c, "awake displays takes on or off");
                SettingsStore.Update(s => s.Awake.KeepDisplaysOn = displays);
                c.Out.WriteLine(displays ? "While awake, the displays stay on and the screen does not lock." : "While awake, the displays may still blank and lock.");
                return Done;
            case ["stay-active", var v]:
                return StayActive([v], c);
            default:
                return Wrong(c, "awake takes on, off, for <30m|2h>, until <HH:MM>, displays on|off or stay-active on|off");
        }
    }

    private static int StayActive(string[] args, CommandContext c)
    {
        if (args is [] or ["status"])
        {
            c.Out.WriteLine($"stay active  {(SettingsStore.Load().Awake.StayActive ? "on" : "off")}");
            return Done;
        }
        if (args is ["toggle"]) args = [SettingsStore.Load().Awake.StayActive ? "off" : "on"];
        if (args.Length != 1 || !TryOnOff(args[0], out bool on)) return Wrong(c, "stayactive takes on, off or toggle");
        SettingsStore.Update(s => s.Awake.StayActive = on);
        c.Out.WriteLine(on
            ? "Stay active on: after 55 seconds without the pointer moving, it moves a pixel and back."
            : "Stay active off.");
        return on ? AwakeNote(c) : Done;
    }

    private static int AwakeStatus(CommandContext c)
    {
        var a = SettingsStore.Load().Awake;
        string state = a.Mode switch
        {
            AwakeMode.On => "on, until switched off",
            AwakeMode.Until when a.ActiveAt(DateTimeOffset.Now) => $"on, until {a.Until:HH:mm}",
            AwakeMode.Until => "off (its time has passed)",
            _ => "off",
        };
        c.Out.WriteLine($"keep awake   {state}");
        c.Out.WriteLine($"displays     {(a.KeepDisplaysOn ? "kept on while awake" : "may blank while awake")}");
        c.Out.WriteLine($"stay active  {(a.StayActive ? "on" : "off")}");
        if (c.Engine is { } engine) c.Out.WriteLine($"holding      {engine.AwakeStatus()}");
        else if (!EngineClient.IsRunning() && (a.Mode != AwakeMode.Off || a.StayActive))
            c.Out.WriteLine($"note         only the engine can hold the computer awake, and it is not running: {Distro.EngineStartCommand}");
        return Done;
    }

    private static int AwakeNote(CommandContext c)
    {
        if (!c.InEngine && !EngineClient.IsRunning())
            c.Out.WriteLine($"The engine holds it, and it is not running: {Distro.EngineStartCommand}");
        return Done;
    }

    internal static bool TryDuration(string text, out TimeSpan duration)
    {
        duration = default;
        text = text.Trim().ToLowerInvariant();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bare))
        {
            duration = TimeSpan.FromMinutes(bare);
            return bare is > 0 and <= 7 * 24 * 60;
        }
        int hours = 0, minutes = 0, number = 0;
        bool any = false, digits = false;
        foreach (char ch in text)
        {
            if (char.IsAsciiDigit(ch)) { number = number * 10 + (ch - '0'); digits = true; if (number > 100_000) return false; continue; }
            if (!digits) return false;
            if (ch == 'h') hours += number;
            else if (ch == 'm') minutes += number;
            else return false;
            number = 0; digits = false; any = true;
        }
        if (digits || !any) return false;
        duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(7);
    }

    /// <summary><c>displays-off</c>: every display off now, the computer still
    /// running; any key or the mouse wakes them.</summary>
    private static int DisplaysOff(string[] args, CommandContext c)
    {
        if (args.Length > 0) return Wrong(c, "displays-off takes no arguments");
        if (Unreachable() is { } why) return Refuse(c, why);
        if (!DisplayPower.TurnOff(out var error)) return Refuse(c, error!);
        c.Out.WriteLine("Displays off; any key or the mouse brings them back.");
        return Done;
    }

    /// <summary><c>darkmode</c>: the desktop's light or dark theme, and dark
    /// on night light's hours.</summary>
    private static int DarkModeCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["status"]:
                var dark = DarkMode.Read();
                var s = SettingsStore.Load();
                c.Out.WriteLine($"dark mode    {(dark is null ? "unknown on this desktop" : dark.Value ? "on" : "off")}");
                c.Out.WriteLine(s.DarkMode.FollowSchedule
                    ? $"schedule     dark from {s.NightLight.From} to {s.NightLight.To} (night light's hours)"
                    : "schedule     none");
                return Done;
            case ["on" or "off" or "true" or "false" or "toggle"]:
                bool on = args[0] == "toggle" ? DarkMode.Read() != true : args[0] is "on" or "true";
                if (!DarkMode.Set(on, out var error)) return Refuse(c, error!);
                c.Out.WriteLine(on ? "Dark mode on." : "Dark mode off.");
                return Done;
            case ["schedule", var v]:
                if (!TryOnOff(v, out bool follow)) return Wrong(c, "darkmode schedule takes on or off");
                var saved = SettingsStore.Update(x => x.DarkMode.FollowSchedule = follow);
                c.Out.WriteLine(follow
                    ? $"Dark from {saved.NightLight.From} to {saved.NightLight.To}, light outside; night light's hours (nightlight --from --to)."
                    : "Dark mode no longer follows a schedule.");
                return follow ? AwakeNote(c) : Done;
            default:
                return Wrong(c, "darkmode takes on, off, toggle or schedule on|off");
        }
    }
}
