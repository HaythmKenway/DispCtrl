using Tmds.DBus.Protocol;

namespace DispCtrl.Linux.Session;

/// <summary>
/// The room's light from the ambient light sensor, through iio-sensor-proxy
/// (the service GNOME and KDE read it from), and the brightness it asks for.
/// </summary>
/// <remarks>
/// The sensor is claimed for as long as this object lives, as the service
/// requires, and released with the connection. Brightness follows the
/// logarithm of the light, as eyes do: a dark room at 1 lux and a bright one
/// at 1000 are the ends of the scale, between each person's lowest and highest.
/// </remarks>
public sealed class RoomLight : IDisposable
{
    private const string Service = "net.hadess.SensorProxy", Path = "/net/hadess/SensorProxy";
    private readonly DBusConnection _system;

    private RoomLight(DBusConnection system) => _system = system;

    /// <summary>The sensor, claimed; null with the reason when there is none.</summary>
    public static RoomLight? Open(out string? why)
    {
        DBusConnection? system = null;
        try
        {
            system = new DBusConnection(DBusAddress.System!);
            system.ConnectAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            var light = new RoomLight(system);
            if (light.Get(r => r.ReadVariantValue().GetBool(), "HasAmbientLight") != true)
            {
                light.Dispose();
                why = "this computer has no ambient light sensor that iio-sensor-proxy can read";
                return null;
            }
            light.Call("ClaimLight");
            why = null;
            return light;
        }
        catch (Exception ex)
        {
            system?.Dispose();
            why = ex.GetBaseException() is DBusErrorReplyException { ErrorName: "org.freedesktop.DBus.Error.ServiceUnknown" }
                ? "iio-sensor-proxy is not installed or not running"
                : $"the light sensor could not be reached ({ex.GetBaseException().Message})";
            return null;
        }
    }

    /// <summary>The light now, in lux; null when the sensor gives none.</summary>
    public double? Lux() => Get(r => r.ReadVariantValue().GetDouble(), "LightLevel");

    /// <summary>The brightness, 0 to 100, for <paramref name="lux"/> between
    /// <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static int LevelFor(double lux, int min, int max)
    {
        double share = Math.Clamp(Math.Log10(Math.Max(lux, 0) + 1) / 3.0, 0, 1);
        return (int)Math.Round(min + (max - min) * share);
    }

    private T? Get<T>(Func<Reader, T> read, string property) where T : struct
    {
        MessageBuffer Build()
        {
            var w = _system.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(Service, Path, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
                w.WriteString(Service);
                w.WriteString(property);
                return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }
        var task = _system.CallMethodAsync(Build(), (Message m, object? _) => read(m.GetBodyReader()), null);
        return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : null;
    }

    private void Call(string member)
    {
        MessageBuffer Build()
        {
            var w = _system.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(Service, Path, Service, member, null, MessageFlags.None);
                return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }
        _system.CallMethodAsync(Build()).Wait(TimeSpan.FromSeconds(3));
    }

    public void Dispose() => _system.Dispose();
}
