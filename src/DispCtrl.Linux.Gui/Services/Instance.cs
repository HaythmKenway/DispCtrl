using System.Net.Sockets;
using System.Text;
using DispCtrl.Linux.Engine;

namespace DispCtrl.Linux.Gui.Services;

/// <summary>One window process per session: a second start hands its request
/// (open the window, the quick panel, or only the tray) to the first and ends.</summary>
/// <remarks>
/// With the tray icon on, the process lives from sign-in; opening DispCtrl
/// from the app grid used to start a second one with a second icon. A Unix
/// socket beside the engine's, mode 0600, in the per-user runtime folder.
/// </remarks>
public static class Instance
{
    public const string Window = "window", Panel = "panel", Tray = "tray";

    private static string SocketPath => Path.Combine(Runtime.Directory, "dispctrl-linux-gui.sock");

    /// <summary>Hands <paramref name="request"/> to a running window process;
    /// false when there is none.</summary>
    public static bool TrySend(string request)
    {
        if (!File.Exists(SocketPath) || SocketPath.Length > Runtime.MaxSocketPath) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(request + "\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Takes requests from later starts, calling <paramref name="handle"/>
    /// on a background thread for each.</summary>
    public static void Listen(Action<string> handle)
    {
        if (SocketPath.Length > Runtime.MaxSocketPath) return;
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // A socket file nobody answers on was left by a process that was killed.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            listener.Dispose();
            return;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            listener.Dispose();
            try { File.Delete(SocketPath); } catch (IOException) { }
        };
        new Thread(() =>
        {
            while (true)
            {
                Socket client;
                try { client = listener.Accept(); }
                catch (Exception) { return; }
                using (client)
                {
                    try
                    {
                        var buffer = new byte[64];
                        int n = client.Receive(buffer);
                        var request = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                        if (request is Window or Panel or Tray) handle(request);
                    }
                    catch (SocketException) { }
                }
            }
        }) { IsBackground = true, Name = "single instance" }.Start();
    }
}
