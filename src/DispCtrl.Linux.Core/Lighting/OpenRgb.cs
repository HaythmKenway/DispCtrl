using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DispCtrl.Linux.Lighting;

/// <summary>One lighting effect a device offers, as OpenRGB describes it.</summary>
public sealed record RgbMode(int Index, string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax,
    uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness, uint Direction, uint ColorMode, uint[] Colors)
{
    public const uint HasModeSpecificColor = 1 << 6, HasPerLedColor = 1 << 5;
    public const uint ColorModeNone = 0, ColorModePerLed = 1, ColorModeSpecific = 2;
}

/// <summary>A keyboard, mouse, fan hub, LED strip or other RGB device OpenRGB controls.</summary>
public sealed record RgbDevice(int Index, string Name, string Vendor, int Type, int ActiveMode, IReadOnlyList<RgbMode> Modes, int LedCount)
{
    /// <summary>OpenRGB's device types, by number.</summary>
    public string TypeName => Type switch
    {
        0 => "motherboard", 1 => "memory", 2 => "graphics card", 3 => "cooler", 4 => "LED strip", 5 => "keyboard",
        6 => "mouse", 7 => "mouse mat", 8 => "headset", 9 => "headset stand", 10 => "gamepad", 11 => "light", 12 => "speaker",
        13 => "virtual", 14 => "storage", 15 => "case", 16 => "microphone", 17 => "accessory", 18 => "keypad",
        _ => "device",
    };
}

/// <summary>
/// A client of OpenRGB's SDK server (Settings, "SDK Server", port 6742 by
/// default), which drives the lighting of keyboards, mice, fans, strips and
/// boards from hundreds of makers.
/// </summary>
/// <remarks>
/// DispCtrl does not speak to RGB devices itself: each maker has its own USB or
/// SMBus protocol, and OpenRGB already knows them and holds the permissions
/// (its udev rules) they need. The protocol is OpenRGB's own: a 16-byte header
/// ("ORGB", device index, packet id, size, little-endian) and a body. This
/// client asks for protocol 3 and reads devices as that version lays them out,
/// or as an older server's does.
/// </remarks>
public sealed class OpenRgb : IDisposable
{
    public const int DefaultPort = 6742;
    private const uint ClientProtocol = 3;
    private const uint RequestControllerCount = 0, RequestControllerData = 1, RequestProtocolVersion = 40, SetClientName = 50;
    private const uint UpdateLeds = 1050, SetCustomMode = 1100, UpdateMode = 1101;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _protocol;

    private OpenRgb(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>Connects to the SDK server; throws <see cref="SocketException"/>
    /// when nothing listens there (OpenRGB not running, or its server off).</summary>
    public static OpenRgb Connect(string host = "127.0.0.1", int port = DefaultPort)
    {
        var tcp = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
        if (!tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2)))
        {
            tcp.Dispose();
            throw new SocketException((int)SocketError.TimedOut);
        }
        var client = new OpenRgb(tcp);
        client.Handshake();
        return client;
    }

    private void Handshake()
    {
        Send(0, RequestProtocolVersion, U32(ClientProtocol));
        var reply = Receive(RequestProtocolVersion);
        uint server = reply.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(reply) : 0;
        _protocol = Math.Min(server, ClientProtocol);
        Send(0, SetClientName, Encoding.UTF8.GetBytes("DispCtrl\0"));
    }

    public IReadOnlyList<RgbDevice> Devices()
    {
        Send(0, RequestControllerCount, []);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(Receive(RequestControllerCount));
        var devices = new List<RgbDevice>();
        for (int i = 0; i < count; i++)
        {
            Send((uint)i, RequestControllerData, _protocol > 0 ? U32(_protocol) : []);
            devices.Add(Parse(i, Receive(RequestControllerData), _protocol));
        }
        return devices;
    }

    /// <summary>Every LED of the device one colour, in its direct (custom) mode.</summary>
    public void SetColour(RgbDevice device, byte r, byte g, byte b)
    {
        Send((uint)device.Index, SetCustomMode, []);
        var body = new byte[4 + 2 + device.LedCount * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), (ushort)device.LedCount);
        for (int i = 0; i < device.LedCount; i++)
        {
            body[6 + i * 4] = r;
            body[7 + i * 4] = g;
            body[8 + i * 4] = b;
        }
        Send((uint)device.Index, UpdateLeds, body);
    }

    /// <summary>Switches the device to one of its own effects; a colour, when
    /// given and the effect takes one, becomes its colour.</summary>
    public void SetMode(RgbDevice device, RgbMode mode, (byte R, byte G, byte B)? colour = null)
    {
        var colours = mode.Colors;
        uint colourMode = mode.ColorMode;
        if (colour is { } c && (mode.Flags & RgbMode.HasModeSpecificColor) != 0)
        {
            int n = (int)Math.Max(1, Math.Max(mode.ColorsMin, (uint)Math.Max(colours.Length, 1)));
            colours = Enumerable.Repeat(Pack(c.R, c.G, c.B), n).ToArray();
            colourMode = RgbMode.ColorModeSpecific;
        }
        Send((uint)device.Index, UpdateMode, SerializeMode(mode with { Colors = colours, ColorMode = colourMode }, _protocol));
    }

    // ---- wire format --------------------------------------------------------

    internal static RgbDevice Parse(int index, ReadOnlySpan<byte> data, uint protocol)
    {
        var r = new Reader(data);
        r.U32();                          // data size
        int type = r.I32();
        string name = r.Str();
        string vendor = protocol >= 1 ? r.Str() : "";
        r.Str(); r.Str(); r.Str(); r.Str(); // description, version, serial, location
        int modeCount = r.U16();
        int active = r.I32();
        var modes = new List<RgbMode>();
        for (int m = 0; m < modeCount; m++)
        {
            string modeName = r.Str();
            int value = r.I32();
            uint flags = r.U32(), speedMin = r.U32(), speedMax = r.U32();
            uint brightMin = protocol >= 3 ? r.U32() : 0, brightMax = protocol >= 3 ? r.U32() : 0;
            uint colorsMin = r.U32(), colorsMax = r.U32(), speed = r.U32();
            uint brightness = protocol >= 3 ? r.U32() : 0;
            uint direction = r.U32(), colorMode = r.U32();
            int colorCount = r.U16();
            var colors = new uint[colorCount];
            for (int k = 0; k < colorCount; k++) colors[k] = r.U32();
            modes.Add(new RgbMode(m, modeName, value, flags, speedMin, speedMax, brightMin, brightMax, colorsMin, colorsMax,
                speed, brightness, direction, colorMode, colors));
        }
        int zoneCount = r.U16();
        for (int z = 0; z < zoneCount; z++)
        {
            r.Str();
            r.I32();
            r.U32(); r.U32(); r.U32();      // leds min, max, count
            int matrix = r.U16();
            r.Skip(matrix);
            if (protocol >= 4)
            {
                int segments = r.U16();
                for (int s = 0; s < segments; s++) { r.Str(); r.I32(); r.U32(); r.U32(); }
            }
        }
        int ledCount = r.U16();
        for (int l = 0; l < ledCount; l++) { r.Str(); r.U32(); }
        return new RgbDevice(index, name, vendor, type, active, modes, ledCount);
    }

    internal static byte[] SerializeMode(RgbMode mode, uint protocol)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0u);                      // size, filled in below
        w.Write(mode.Index);
        var name = Encoding.UTF8.GetBytes(mode.Name + "\0");
        w.Write((ushort)name.Length);
        w.Write(name);
        w.Write(mode.Value);
        w.Write(mode.Flags);
        w.Write(mode.SpeedMin);
        w.Write(mode.SpeedMax);
        if (protocol >= 3) { w.Write(mode.BrightnessMin); w.Write(mode.BrightnessMax); }
        w.Write(mode.ColorsMin);
        w.Write(mode.ColorsMax);
        w.Write(mode.Speed);
        if (protocol >= 3) w.Write(mode.Brightness);
        w.Write(mode.Direction);
        w.Write(mode.ColorMode);
        w.Write((ushort)mode.Colors.Length);
        foreach (var c in mode.Colors) w.Write(c);
        w.Flush();
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        return bytes;
    }

    /// <summary>OpenRGB's colour: red in the low byte, then green, then blue.</summary>
    internal static uint Pack(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    private void Send(uint device, uint packet, byte[] body)
    {
        var header = new byte[16];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), packet);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)body.Length);
        _stream.Write(header);
        if (body.Length > 0) _stream.Write(body);
    }

    /// <summary>The next reply of type <paramref name="packet"/>; others (the
    /// server's "device list changed" notices) are skipped.</summary>
    private byte[] Receive(uint packet)
    {
        var header = new byte[16];
        while (true)
        {
            _stream.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new IOException("not an OpenRGB server");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            if (size > 16 << 20) throw new IOException("OpenRGB sent a reply too large to be one");
            var body = new byte[size];
            _stream.ReadExactly(body);
            if (id == packet) return body;
        }
    }

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_at..]); _at += 4; return v; }
        public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_data[_at..]); _at += 4; return v; }
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_at..]); _at += 2; return v; }
        public void Skip(int n) => _at += n;

        public string Str()
        {
            int length = U16();
            var text = Encoding.UTF8.GetString(_data.Slice(_at, length)).TrimEnd('\0');
            _at += length;
            return text;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
