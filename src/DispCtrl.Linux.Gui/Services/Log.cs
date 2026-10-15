using DispCtrl.Linux.Diagnostics;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>The window's diagnostics: the shared rotating log
/// (<see cref="AppLog"/>), which also echoes to stderr for the snap's journal
/// and the desktop session's log - a tray click that opened nothing leaves no
/// other trace.</summary>
public static class Log
{
    public static void Write(string message) => AppLog.Auto("gui", message);
}
