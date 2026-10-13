using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace DispCtrl.Linux.Session;

/// <summary>Keep awake: holds every inhibitor the session offers until
/// disposed, and says which it got.</summary>
/// <remarks>
/// Each desktop listens to a different one, so all of them are asked:
/// <list type="bullet">
/// <item>GNOME's session manager (suspend, and idle when the displays are to
/// stay on): what GNOME's own power settings obey.</item>
/// <item><c>org.freedesktop.ScreenSaver</c> (KDE, XFCE, Cinnamon, MATE, LXQt)
/// for the displays, and KDE's <c>PowerManagement.Inhibit</c> for sleep.</item>
/// <item>systemd-logind's (or elogind's) sleep lock on the system bus, which
/// every suspend through logind honours, whatever the desktop.</item>
/// </list>
/// The session inhibitors last as long as the D-Bus connection that asked for
/// them and logind's as long as the descriptor it hands back, so a crash
/// releases them all: nothing is left holding a laptop awake in a bag.
/// </remarks>
public sealed class Inhibitor : IDisposable
{
    private const string Who = "DispCtrl";
    private readonly List<IDisposable> _held = [];

    private Inhibitor(IReadOnlyList<string> got, IReadOnlyList<string> failed)
    {
        Holding = got;
        Failed = failed;
    }

    /// <summary>What it holds, for status: "GNOME session", "logind sleep lock" ...</summary>
    public IReadOnlyList<string> Holding { get; }

    /// <summary>What it asked for and was refused, with the reason.</summary>
    public IReadOnlyList<string> Failed { get; }

    public static Inhibitor Acquire(bool keepDisplaysOn, string reason)
    {
        var got = new List<string>();
        var failed = new List<string>();
        var held = new List<IDisposable>();

        if (Connect(DBusAddress.Session, "session bus", failed) is { } session)
        {
            held.Add(session);
            Try(got, failed, "GNOME session", () =>
            {
                // Flags: 4 suspend, 8 idle (the screen blanking and locking).
                uint flags = 4u | (keepDisplaysOn ? 8u : 0u);
                Call(session, "org.gnome.SessionManager", "/org/gnome/SessionManager", "org.gnome.SessionManager", "Inhibit", "susu",
                    (ref MessageWriter w) => { w.WriteString("dispctrl-linux"); w.WriteUInt32(0); w.WriteString(reason); w.WriteUInt32(flags); });
            });
            if (keepDisplaysOn)
            {
                Try(got, failed, "screensaver", () => Call(session, "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                    "org.freedesktop.ScreenSaver", "Inhibit", "ss", (ref MessageWriter w) => { w.WriteString(Who); w.WriteString(reason); }));
            }
            Try(got, failed, "power management", () => Call(session, "org.freedesktop.PowerManagement",
                "/org/freedesktop/PowerManagement/Inhibit", "org.freedesktop.PowerManagement.Inhibit", "Inhibit", "ss",
                (ref MessageWriter w) => { w.WriteString(Who); w.WriteString(reason); }));
        }

        if (Connect(DBusAddress.System, "system bus", failed) is { } system)
        {
            held.Add(system);
            Try(got, failed, "logind sleep lock", () =>
            {
                var fd = CallFd(system, "org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "Inhibit", "ssss",
                    (ref MessageWriter w) => { w.WriteString(keepDisplaysOn ? "sleep:idle" : "sleep"); w.WriteString(Who); w.WriteString(reason); w.WriteString("block"); });
                if (fd is not null) held.Add(fd);
            });
        }

        var inhibitor = new Inhibitor(got, failed);
        inhibitor._held.AddRange(held);
        return inhibitor;
    }

    /// <summary>Writes a call's arguments. By reference: the writer is a ref
    /// struct, and written through a copy (an Action of it) its length never
    /// moved, so the bus received a malformed call and closed the connection -
    /// every inhibitor came back "Connection closed by peer".</summary>
    private delegate void BodyWriter(ref MessageWriter writer);

    private static DBusConnection? Connect(string? address, string name, List<string> failed)
    {
        if (string.IsNullOrEmpty(address)) { failed.Add($"{name}: no address"); return null; }
        var connection = new DBusConnection(address);
        try
        {
            connection.ConnectAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            return connection;
        }
        catch (Exception ex)
        {
            connection.Dispose();
            failed.Add($"{name}: {Reason(ex)}");
            return null;
        }
    }

    private static void Try(List<string> got, List<string> failed, string name, Action call)
    {
        try { call(); got.Add(name); }
        catch (Exception ex) { failed.Add($"{name}: {Reason(ex)}"); }
    }

    private static void Call(DBusConnection connection, string destination, string path, string @interface, string member,
        string signature, BodyWriter body)
    {
        var task = connection.CallMethodAsync(Build(connection, destination, path, @interface, member, signature, body),
            (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null);
        if (!task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("no answer within 5 s");
    }

    private static SafeFileHandle? CallFd(DBusConnection connection, string destination, string path, string @interface, string member,
        string signature, BodyWriter body)
    {
        var task = connection.CallMethodAsync(Build(connection, destination, path, @interface, member, signature, body),
            (Message m, object? _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null);
        return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : throw new TimeoutException("no answer within 5 s");
    }

    private static MessageBuffer Build(DBusConnection connection, string destination, string path, string @interface, string member,
        string signature, BodyWriter body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static string Reason(Exception ex)
    {
        var inner = ex is AggregateException { InnerException: { } i } ? i : ex;
        return inner is DBusErrorReplyException dbus ? dbus.ErrorName.Replace("org.freedesktop.DBus.Error.", "", StringComparison.Ordinal) : inner.Message;
    }

    public void Dispose()
    {
        // The descriptor first, then the buses: closing a connection drops its
        // session inhibitors, closing the descriptor drops logind's.
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            try { _held[i].Dispose(); } catch (Exception) { }
        }
        _held.Clear();
    }
}
