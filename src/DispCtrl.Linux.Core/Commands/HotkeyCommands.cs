using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Settings;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Commands;

public static partial class CommandRunner
{
    /// <summary><c>hotkeys</c>: global shortcuts that run any dispctrl-linux
    /// command, held by the engine.</summary>
    private static int HotkeysCommand(string[] args, CommandContext c)
    {
        switch (args)
        {
            case [] or ["list"]:
            {
                var list = SettingsStore.Load().Hotkeys!;
                if (list.Count == 0) c.Out.WriteLine("No hotkeys. hotkeys reset puts the defaults back.");
                for (int i = 0; i < list.Count; i++)
                    c.Out.WriteLine($"{i + 1,2}  {(list[i].Enabled ? "  " : "- ")}{list[i].Keys,-22} {list[i].Command}");
                if (c.Engine is { } engine) c.Out.WriteLine($"\n{engine.HotkeyStatus()}");
                else if (!EngineClient.IsRunning()) c.Out.WriteLine($"\nThe engine holds them, and it is not running: {Distribution.Distro.EngineStartCommand}");
                return Done;
            }
            case ["add", var keys, .. var command] when command.Length > 0:
            {
                if (Shortcut.Parse(keys) is null) return Wrong(c, $"'{keys}' is not a shortcut: modifiers and a key, such as Ctrl+Alt+N or Super+F9");
                string text = string.Join(' ', command);
                if (HotkeyCommandProblem(text) is { } why) return Wrong(c, why);
                SettingsStore.Update(s =>
                {
                    s.Hotkeys!.RemoveAll(h => SameKeys(h.Keys, keys));
                    s.Hotkeys.Add(new HotkeyBinding { Keys = Shortcut.Parse(keys)!.Text, Command = text });
                });
                c.Out.WriteLine($"{keys}: {text}");
                return AwakeNote(c);
            }
            case ["remove", var which]:
            {
                int removed = 0;
                SettingsStore.Update(s => removed = Index(s.Hotkeys!, which) is int i ? Remove(s.Hotkeys!, i) : 0);
                return removed == 0 ? Wrong(c, $"no hotkey '{which}' (hotkeys lists them)") : Write(c.Out, $"Removed {which}.");
            }
            case ["on" or "off", var which]:
            {
                bool on = args[0] == "on", found = false;
                SettingsStore.Update(s => { if (Index(s.Hotkeys!, which) is int i) { s.Hotkeys![i].Enabled = on; found = true; } });
                return found ? Write(c.Out, $"{which} {(on ? "on" : "off")}.") : Wrong(c, $"no hotkey '{which}' (hotkeys lists them)");
            }
            case ["reset"]:
                SettingsStore.Update(s => s.Hotkeys = HotkeyBinding.Defaults());
                c.Out.WriteLine("The default hotkeys are back.");
                return Done;
            default:
                return Wrong(c, "hotkeys takes list, add KEYS COMMAND..., remove KEYS|N, on|off KEYS|N or reset");
        }

        static int Remove(List<HotkeyBinding> list, int i) { list.RemoveAt(i); return 1; }
    }

    private static int? Index(List<HotkeyBinding> list, string which)
    {
        if (int.TryParse(which, out int n) && n >= 1 && n <= list.Count) return n - 1;
        int i = list.FindIndex(h => SameKeys(h.Keys, which));
        return i >= 0 ? i : null;
    }

    private static bool SameKeys(string a, string b) =>
        Shortcut.Parse(a) is { } x && Shortcut.Parse(b) is { } y && x.Modifiers == y.Modifiers
        && string.Equals(x.Keysym, y.Keysym, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a hotkey's command would never run: an unknown verb, or one
    /// that only makes sense in a terminal.</summary>
    internal static string? HotkeyCommandProblem(string command)
    {
        var verb = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (verb is "engine" or "help" or "doctor" or "hotkeys") return $"'{verb}' is not something a hotkey can do";
        return Verbs.Contains(verb) ? null : $"'{verb}' is not a dispctrl-linux command (dispctrl-linux help lists them)";
    }

    /// <summary><c>tray show</c>: the quick panel, from the window's process when
    /// it runs (the tray's), or a new one otherwise.</summary>
    private static int TrayShow(CommandContext c)
    {
        var socket = Path.Combine(Runtime.Directory, "dispctrl-linux-gui.sock");
        if (File.Exists(socket))
        {
            try
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(socket));
                s.Send(Encoding.UTF8.GetBytes("panel\n"));
                return Done;
            }
            catch (SocketException) { }
        }
        var gui = Path.Combine(AppContext.BaseDirectory, "dispctrl-linux-gui");
        if (!File.Exists(gui)) return Refuse(c, "the window (dispctrl-linux-gui) is not installed beside this program");
        try { Process.Start(new ProcessStartInfo(gui, "--quick-panel") { UseShellExecute = false })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception ex) { return Refuse(c, ex.Message); }
        return Done;
    }
}
