using System.Runtime.InteropServices;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Session;

/// <summary>Turns every display off through DPMS, as Windows' "Turn off
/// displays" does: the computer keeps running and the first touch of the
/// keyboard or mouse brings them back.</summary>
/// <remarks>
/// DPMS is X's own (libXext), so it reaches every output at once and needs no
/// DDC/CI. Forced straight after a click or a key press, the release of that
/// same press woke the displays again at once, so the command waits for the
/// hand to leave first.
/// </remarks>
public static class DisplayPower
{
    private const string LibXext = "libXext.so.6";
    private const ushort DpmsModeOff = 3;

    /// <summary>The pause before the displays go off, for the press that asked
    /// for it to be released.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);

    public static bool TurnOff(out string? error)
    {
        Thread.Sleep(Settle);
        using var x = XConnection.Open();
        if (x is null) { error = "no X display (displays off needs an X11 session)"; return false; }
        try
        {
            if (DPMSCapable(x.Display) == 0) { error = "this X server has no DPMS, so it cannot switch its displays off"; return false; }
            // DPMS switched off in the server (xset -dpms, some desktops) ignores a forced level.
            DPMSEnable(x.Display);
            DPMSForceLevel(x.Display, DpmsModeOff);
            Xlib.XFlush(x.Display);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "libXext is missing, so DPMS cannot be reached";
            return false;
        }
    }

    [DllImport(LibXext)] private static extern int DPMSCapable(nint display);
    [DllImport(LibXext)] private static extern int DPMSEnable(nint display);
    [DllImport(LibXext)] private static extern int DPMSForceLevel(nint display, ushort level);
}
