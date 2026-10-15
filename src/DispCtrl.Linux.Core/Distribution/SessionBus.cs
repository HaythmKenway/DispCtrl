using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace DispCtrl.Linux.Distribution;

/// <summary>A just-enough D-Bus client to claim and look up one well-known name
/// on the session bus.</summary>
/// <remarks>
/// The name is the one place every flavour of DispCtrl can meet: a snap's mount
/// namespace, runtime folder and /tmp are its own, so a lock file or socket
/// cannot be shared with the .deb's engine, but the session bus is the user's
/// and is reached by every confined app. The engine owns the name for as long
/// as it lives, and the bus drops it when the connection closes, so a crashed
/// engine never leaves it stale. No library: a D-Bus package would be one more
/// thing for the packages and the snap to carry for three calls.
/// </remarks>
public sealed class SessionBus : IDisposable
{
    /// <summary>The name the engine claims.</summary>
    public const string EngineName = "org.dispctrl.Linux";

    /// <summary>What asking for a name came to.</summary>
    public enum Claim
    {
        /// <summary>This process owns it now (or already did).</summary>
        Owner,
        /// <summary>Another connection - another install's engine - owns it.</summary>
        Taken,
        /// <summary>The bus refused the question, as a snap without the matching slot is refused.</summary>
        Denied,
    }

    private readonly Socket _socket;
    private uint _serial;

    private SessionBus(Socket socket) => _socket = socket;

    [DllImport("libc")]
    private static extern uint getuid();

    /// <summary>Connects and says hello, or null when there is no session bus or
    /// it will not take this process.</summary>
    public static SessionBus? Connect(string? address = null, uint? uid = null)
    {
        address ??= Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var endpoint = EndPoint(address) ?? EndPoint(DefaultAddress());
        if (endpoint is null) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            SendTimeout = 2000,
            ReceiveTimeout = 2000,
        };
        try
        {
            socket.Connect(endpoint);
            string id = Convert.ToHexString(Encoding.ASCII.GetBytes((uid ?? getuid()).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToLowerInvariant();
            socket.Send(Encoding.ASCII.GetBytes($"\0AUTH EXTERNAL {id}\r\n"));
            var line = ReadLine(socket);
            if (!line.StartsWith("OK", StringComparison.Ordinal)) { socket.Dispose(); return null; }
            socket.Send("BEGIN\r\n"u8);
            var bus = new SessionBus(socket);
            return bus.Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "Hello", "", []) is { IsError: false } ? bus : Fail(bus);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static SessionBus? Fail(SessionBus bus) { bus.Dispose(); return null; }

    private static string DefaultAddress() =>
        $"unix:path={Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{getuid()}"}/bus";

    /// <summary>The socket address in a D-Bus address string; null for one that
    /// is not a Unix socket.</summary>
    internal static UnixDomainSocketEndPoint? EndPoint(string? address)
    {
        foreach (var candidate in (address ?? "").Split(';'))
        {
            if (!candidate.StartsWith("unix:", StringComparison.Ordinal)) continue;
            foreach (var pair in candidate[5..].Split(','))
            {
                if (pair.StartsWith("path=", StringComparison.Ordinal)) return new UnixDomainSocketEndPoint(pair[5..]);
                if (pair.StartsWith("abstract=", StringComparison.Ordinal)) return new UnixDomainSocketEndPoint("\0" + pair[9..]);
            }
        }
        return null;
    }

    /// <summary>Asks for <paramref name="name"/> without queueing for it.</summary>
    public Claim RequestName(string name)
    {
        var body = new Writer();
        body.String(name);
        body.U32(4); // DBUS_NAME_FLAG_DO_NOT_QUEUE
        var reply = Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName", "su", body.Bytes());
        if (reply.IsError || reply.Body.Length < 4) return Claim.Denied;
        return BitConverter.ToUInt32(reply.Body) is 1 or 4 ? Claim.Owner : Claim.Taken;
    }

    /// <summary>Whether anything owns <paramref name="name"/>; null when the bus would not say.</summary>
    public bool? NameHasOwner(string name)
    {
        var body = new Writer();
        body.String(name);
        var reply = Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", body.Bytes());
        if (reply.IsError || reply.Body.Length < 4) return null;
        return BitConverter.ToUInt32(reply.Body) != 0;
    }

    private readonly record struct Reply(bool IsError, byte[] Body);

    private Reply Call(string destination, string path, string iface, string member, string signature, byte[] body)
    {
        var fields = new Writer();
        Field(fields, 1, 'o', path);
        Field(fields, 2, 's', iface);
        Field(fields, 3, 's', member);
        Field(fields, 6, 's', destination);
        if (signature.Length > 0)
        {
            fields.Align(8);
            fields.Byte(8);
            fields.Byte(1); fields.Byte((byte)'g'); fields.Byte(0);
            fields.Byte((byte)signature.Length); fields.Raw(Encoding.ASCII.GetBytes(signature)); fields.Byte(0);
        }
        var fieldBytes = fields.Bytes();

        var message = new Writer();
        message.Byte((byte)'l'); message.Byte(1); message.Byte(0); message.Byte(1);
        message.U32((uint)body.Length);
        message.U32(++_serial);
        message.U32((uint)fieldBytes.Length);
        message.Raw(fieldBytes);
        message.Align(8);
        message.Raw(body);
        _socket.Send(message.Bytes());

        // Signals (NameAcquired) can arrive first; the first return or error
        // is this call's, as only one is ever outstanding.
        for (int i = 0; i < 8; i++)
        {
            var head = ReadExactly(16);
            if (head[0] != 'l') throw new InvalidDataException("big-endian D-Bus peer");
            uint bodyLength = BitConverter.ToUInt32(head, 4), fieldLength = BitConverter.ToUInt32(head, 12);
            if (bodyLength > 65536 || fieldLength > 65536) throw new InvalidDataException("oversized D-Bus message");
            int padded = (int)((fieldLength + 7) & ~7u);
            var rest = ReadExactly(padded + (int)bodyLength);
            if (head[1] is 2 or 3) return new Reply(head[1] == 3, rest[padded..]);
        }
        throw new InvalidDataException("no reply from the bus");
    }

    private static void Field(Writer w, byte code, char type, string value)
    {
        w.Align(8);
        w.Byte(code);
        w.Byte(1); w.Byte((byte)type); w.Byte(0);
        w.String(value);
    }

    private byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _socket.Receive(buffer, read, count - read, SocketFlags.None);
            if (n == 0) throw new IOException("the bus closed the connection");
            read += n;
        }
        return buffer;
    }

    private static string ReadLine(Socket socket)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (text.Length < 256 && socket.Receive(one) == 1)
        {
            text.Append((char)one[0]);
            if (one[0] == '\n') break;
        }
        return text.ToString();
    }

    public void Dispose() => _socket.Dispose();

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public void Byte(byte b) => _stream.WriteByte(b);
        public void Raw(byte[] bytes) => _stream.Write(bytes);
        public byte[] Bytes() => _stream.ToArray();
        public void Align(int n) { while (_stream.Length % n != 0) _stream.WriteByte(0); }
        public void U32(uint v) { Align(4); _stream.Write(BitConverter.GetBytes(v)); }

        public void String(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            U32((uint)bytes.Length);
            _stream.Write(bytes);
            _stream.WriteByte(0);
        }
    }
}
