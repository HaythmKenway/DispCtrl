using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>Whether the desktop asks for no animation. The window then gets
/// the "reduced" class, which the styles use to switch their transitions off.</summary>
/// <remarks>Read once, off the UI thread: it starts gsettings, which must
/// never hold up the first frame. Desktops without it animate.</remarks>
public static class Motion
{
    public static bool Reduced { get; private set; }

    public static void Watch(Window window, Action? changed = null) => _ = Task.Run(() =>
    {
        bool reduced = false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (process is not null && process.WaitForExit(1500))
                reduced = process.StandardOutput.ReadToEnd().Trim() == "false";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        Dispatcher.UIThread.Post(() =>
        {
            Reduced = reduced;
            window.Classes.Set("reduced", reduced);
            changed?.Invoke();
        });
    });
}
