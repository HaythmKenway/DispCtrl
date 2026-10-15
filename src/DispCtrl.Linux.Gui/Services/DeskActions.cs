using System.Diagnostics;
using DispCtrl.Linux.Distribution;
using DispCtrl.Linux.Engine;
using DispCtrl.Linux.Hardware;
using DispCtrl.Linux.Settings;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>What the tray menu and the quick panel can do, as the commands
/// the window and dispctrl-linux send, so all three agree.</summary>
public static class DeskActions
{
    /// <summary>Raised on the UI thread after an action, so the menu and panel
    /// read the new state back.</summary>
    public static event Action? Changed;

    /// <summary>Runs a command and raises <see cref="Changed"/>; the reason it
    /// was refused, or null.</summary>
    public static async Task<string?> RunAsync(params string[] args)
    {
        var reply = await CommandSender.RunAsync(args);
        Changed?.Invoke();
        return reply.ExitCode == 0 ? null : CommandSender.Reason(reply);
    }

    public static Task<string?> NightLight(bool on) => RunAsync("nightlight", on ? "on" : "off");
    public static Task<string?> NightLightStrength(int strength) => RunAsync("nightlight", strength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static Task<string?> DarkMode(bool on) => RunAsync("darkmode", on ? "on" : "off");
    public static Task<string?> StayActive(bool on) => RunAsync("stayactive", on ? "on" : "off");
    public static Task<string?> Snap(bool on) => RunAsync("snap", on ? "on" : "off");
    public static Task<string?> DisplaysOff() => RunAsync("displays-off");
    public static Task<string?> Restore() => RunAsync("restore");
    public static Task<string?> Project(string how) => RunAsync("display", "project", how);
    public static Task<string?> ApplyPreset(string name) => RunAsync("preset", "apply", name);

    /// <summary>Keep awake: "off", "on", or a span such as "30m".</summary>
    public static Task<string?> Awake(string how) => how is "off" or "on" ? RunAsync("awake", how) : RunAsync("awake", "for", how);

    public static void Identify()
    {
        if (Desk.Current.Outputs.Count > 0) DisplayIdentifier.Show(Desk.Current.Outputs, TimeSpan.FromSeconds(3));
    }

    /// <summary>Starts the engine beside this program, which leaves this
    /// session itself so it outlives the window (DISPCTRL_ENGINE_DETACH): the
    /// setsid program it was started through before is refused under the
    /// snap's confinement, and the Start button did nothing there.</summary>
    public static void StartEngine()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "dispctrl-linux");
        if (!File.Exists(exe)) return;
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("engine");
        info.Environment["DISPCTRL_ENGINE_DETACH"] = "1";
        try { Process.Start(info)?.Dispose(); } catch (System.ComponentModel.Win32Exception) { }
        _ = Task.Delay(1500).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke()));
    }

    public static Task<string?> StopEngine() => RunAsync("engine", "stop");

    /// <summary>The state the menu and panel show, read in one go off the UI thread.</summary>
    /// <param name="Connected">Connected outputs, for the display mode choices.</param>
    public sealed record State(LinuxSettings Settings, bool? Dark, bool Engine, bool NightLightAvailable, IReadOnlyList<XRandROutput> Connected);

    /// <summary>Everything the menu and panel show, off the UI thread: the theme
    /// is a gsettings call and the outputs an X round trip, and on the UI thread
    /// they held up every click on the tray icon.</summary>
    public static Task<State> ReadAsync() => Task.Run(() =>
        new State(SettingsStore.Load(), Session.DarkMode.Read(), EngineClient.IsRunning(), GammaRamp.IsAvailable(out _),
            XRandR.IsAvailable ? XRandR.Query().Where(o => o.Connected).ToList() : []));
}
