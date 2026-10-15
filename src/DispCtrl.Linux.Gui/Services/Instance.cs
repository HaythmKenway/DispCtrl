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
    private static string LockPath => Path.Combine(Runtime.Directory, "dispctrl-linux-gui.lock");

    private static FileStream? _lock;

    /// <summary>What <see cref="TrySend"/> found.</summary>
    public enum Outcome
    {
        /// <summary>No window process: this one should become it.</summary>
        NoInstance,
        /// <summary>The running one took the request and answered.</summary>
        Handled,
        /// <summary>One is running but did not answer in time: its interface is blocked.</summary>
        Unresponsive,
    }

    /// <summary>True while another live process holds the instance lock. The
    /// kernel drops the lock when its holder dies, so unlike a socket file it
    /// is never stale.</summary>
    public static bool IsRunning()
    {
        try
        {
            using var probe = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Hands <paramref name="request"/> to a running window process and
    /// waits for it to say the request reached its interface.</summary>
    public static Outcome TrySend(string request, TimeSpan? wait = null)
    {
        if (!File.Exists(SocketPath) || SocketPath.Length > Runtime.MaxSocketPath) return Outcome.NoInstance;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 2000;
            socket.ReceiveTimeout = (int)(wait ?? TimeSpan.FromSeconds(4)).TotalMilliseconds;
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(request + "\n"));
            var reply = new byte[8];
            int n = socket.Receive(reply);
            return n > 0 ? Outcome.Handled : Outcome.Unresponsive;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
        {
            return Outcome.Unresponsive;
        }
        catch (SocketException)
        {
            // Refused or gone: a socket file left by a process that was killed.
            return Outcome.NoInstance;
        }
    }

    /// <summary>Takes requests from later starts, calling <paramref name="handle"/>
    /// on a background thread for each; it returns once the request has been
    /// carried out, and only then is the sender told.</summary>
    public static void Listen(Action<string> handle)
    {
        if (SocketPath.Length > Runtime.MaxSocketPath) return;
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another live process owns the socket: deleting it would orphan that one.
            Log.Write("instance: another window process holds the lock; not listening");
            return;
        }
        catch (UnauthorizedAccessException) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            // Held the lock, so a socket file still there is a dead process's.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(8);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"instance: cannot listen: {ex.Message}");
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
                catch (Exception ex) { Log.Write($"instance: listener stopped: {ex.Message}"); return; }
                // Each on its own thread: a client that connects and says nothing
                // must not stop every later start from being heard.
                new Thread(() => Serve(client, handle)) { IsBackground = true, Name = "single instance client" }.Start();
            }
        }) { IsBackground = true, Name = "single instance" }.Start();
    }

    private static void Serve(Socket client, Action<string> handle)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                var buffer = new byte[64];
                int n = client.Receive(buffer);
                var request = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                Log.Write($"instance: request '{request}'");
                if (request is not (Window or Panel or Tray)) return;
                handle(request);
                client.Send("ok\n"u8);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"instance: request failed: {ex.Message}");
            }
        }
    }
}
