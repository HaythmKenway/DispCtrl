using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace DispCtrl.Linux.Gui.Services.Tray;

/// <summary>
/// The tray icon: a StatusNotifierItem and its com.canonical.dbusmenu menu,
/// served on the session bus by this process.
/// </summary>
/// <remarks>
/// Avalonia has a tray icon of its own, and it worked outside the snap; in it,
/// every call to the menu was refused by AppArmor, so the icon showed and its
/// menu never opened. Avalonia serves the menu at
/// <c>/net/avaloniaui/dbusmenu/&lt;guid&gt;</c>, a path no snap interface allows,
/// and has no way to choose another. The unity7 interface allows exactly
/// <c>/StatusNotifierItem</c> and <c>/StatusNotifierItem/menu</c>, the paths
/// libappindicator uses, so this serves both there, and works the same
/// outside the snap. Calls arrive on the connection's thread; the menu is
/// swapped whole under a lock, and clicks are handed to the UI thread.
/// </remarks>
public sealed class StatusNotifierItem : IDisposable
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/StatusNotifierItem/menu";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";

    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    private DBusConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly (int Width, int Height, byte[] Argb)[] _icons;
    private readonly string _name;
    private Dictionary<int, TrayMenuItem> _items = new() { [0] = new TrayMenuItem("") };
    private uint _revision = 1;
    private string _toolTip = "DispCtrl";

    /// <summary>A left click on the icon (Activate).</summary>
    public event Action? Activated;

    /// <summary>The mouse wheel over the icon: positive up, negative down.</summary>
    public event Action<int>? Scrolled;

    private StatusNotifierItem(DBusConnection connection, string name, (int, int, byte[])[] icons)
    {
        _connection = connection;
        _name = name;
        _icons = icons;
    }

    /// <summary>Puts the icon in the tray; throws when there is no session bus,
    /// or nothing on it hosts a tray unless <paramref name="waitForHost"/> says to
    /// wait for one.</summary>
    /// <param name="icons">The icon at one or more sizes, as ARGB32 in network
    /// byte order (what the specification asks for).</param>
    /// <param name="waitForHost">The sign-in start: the shell's tray extension
    /// may load after this process, and registering then is all that is needed.</param>
    public static async Task<StatusNotifierItem> StartAsync((int Width, int Height, byte[] Argb)[] icons, bool waitForHost = false)
    {
        var address = DBusAddress.Session ?? throw new InvalidOperationException("no session bus");
        // org.kde.StatusNotifierItem-PID-N: the only names the snap may own for it.
        string name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
        var item = new StatusNotifierItem(await OpenAsync(address, name), name, icons);
        item.Serve(item._connection);
        try { await item.RegisterAsync(); }
        catch (Exception ex) when (waitForHost && ex is not OutOfMemoryException)
        {
            Log.Write($"tray: no tray host yet ({ex.Message}); registering when one appears");
        }
        _ = item.SuperviseAsync(address);
        return item;
    }

    /// <summary>The icon was registered again, on a new connection or with a tray
    /// host that came back: whoever builds the menu should do so again.</summary>
    public event Action? Reregistered;

    private static async Task<DBusConnection> OpenAsync(string address, string name)
    {
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        await connection.RequestNameAsync(name, RequestNameOptions.Default);
        return connection;
    }

    private void Serve(DBusConnection connection)
    {
        connection.AddMethodHandler(new Handler(ItemPath, HandleItem));
        connection.AddMethodHandler(new Handler(MenuPath, HandleMenu));
    }

    /// <summary>Keeps the icon alive for the life of the process: registers it
    /// again when the tray host restarts (the shell, or its AppIndicator
    /// extension reloading, forgets every icon), and opens a new connection when
    /// the bus one drops. Without this the icon stays on screen or goes, and
    /// clicking it reaches nothing.</summary>
    private async Task SuperviseAsync(string address)
    {
        while (!_stop.IsCancellationRequested)
        {
            var connection = _connection;
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _ = WatchHostAsync(connection, watching.Token);
            Exception? reason;
            try { reason = await connection.DisconnectedAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { reason = ex; }
            watching.Cancel();
            if (_stop.IsCancellationRequested) return;
            Log.Write($"tray: the bus connection dropped ({reason?.Message ?? "closed"}); reconnecting");

            var delay = TimeSpan.FromSeconds(1);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, _stop.Token);
                    var next = await OpenAsync(address, _name);
                    Serve(next);
                    _connection = next;
                    connection.Dispose();
                    try { await RegisterAsync(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"tray: not registered yet: {ex.Message}"); }
                    Reregistered?.Invoke();
                    Log.Write("tray: reconnected");
                    break;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Write($"tray: reconnecting failed: {ex.Message}");
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
    }

    /// <summary>Registers whenever a new process takes the watcher's name.</summary>
    private async Task WatchHostAsync(DBusConnection connection, CancellationToken stop)
    {
        try
        {
            using var watcher = await connection.WatchNameOwnerAsync(WatcherName);
            string? registeredWith = null;
            while (!stop.IsCancellationRequested)
            {
                string owner = await watcher.WaitForOwnerAsync(stop);
                if (owner != registeredWith)
                {
                    // The first owner seen is the one StartAsync registered with.
                    if (registeredWith is not null) Log.Write("tray: the tray host changed; registering again");
                    bool first = registeredWith is null;
                    registeredWith = owner;
                    if (!first)
                    {
                        await ReRegisterAsync();
                        Reregistered?.Invoke();
                    }
                }
                try { await Task.Delay(Timeout.Infinite, watcher.GetOwnerChangedCancellationToken(owner)).WaitAsync(stop); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { await Task.Delay(100, stop); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"tray: watching the tray host failed: {ex.Message}"); }
    }

    private async Task RegisterAsync()
    {
        var connection = _connection;
        MessageBuffer Build()
        {
            var w = connection.GetMessageWriter();
            try
            {
                w.WriteMethodCallHeader(WatcherName, "/StatusNotifierWatcher", WatcherName,
                    "RegisterStatusNotifierItem", "s", MessageFlags.None);
                w.WriteString(_name);
                return w.CreateMessage();
            }
            finally { w.Dispose(); }
        }
        // A tray host that is there but stuck would otherwise hold this forever.
        await connection.CallMethodAsync(Build()).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Registers again: a tray host that restarted has forgotten the
    /// icon. A host that still knows it ignores the repeat.</summary>
    public async Task ReRegisterAsync()
    {
        try { await RegisterAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"tray: register failed: {ex.Message}"); }
    }

    /// <summary>Replaces the whole menu and tells the tray it changed.</summary>
    public void SetMenu(IEnumerable<TrayMenuItem> items)
    {
        var root = new TrayMenuItem("");
        root.Children.AddRange(items);
        var all = new Dictionary<int, TrayMenuItem>();
        int next = 0;
        void Number(TrayMenuItem node)
        {
            node.Id = next++;
            all[node.Id] = node;
            foreach (var child in node.Children) Number(child);
        }
        Number(root);
        uint revision;
        lock (_lock)
        {
            _items = all;
            revision = ++_revision;
        }
        Signal(MenuPath, MenuInterface, "LayoutUpdated", "ui", (ref MessageWriter w) => { w.WriteUInt32(revision); w.WriteInt32(0); });
    }

    public void SetToolTip(string text)
    {
        lock (_lock)
        {
            if (_toolTip == text) return;
            _toolTip = text;
        }
        Signal(ItemPath, ItemInterface, "NewToolTip", "", (ref MessageWriter _) => { });
    }

    private delegate void Body(ref MessageWriter writer);

    private void Signal(string path, string @interface, string member, string signature, Body body)
    {
        var w = _connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(null, path, @interface, member, signature.Length == 0 ? null : signature);
            body(ref w);
            _connection.TrySendMessage(w.CreateMessage());
        }
        finally { w.Dispose(); }
    }

    // ---- org.kde.StatusNotifierItem ------------------------------------------

    private void HandleItem(MethodContext context)
    {
        var request = context.Request;
        string member = request.MemberAsString ?? "";
        string iface = request.InterfaceAsString ?? "";

        if (iface == "org.freedesktop.DBus.Properties")
        {
            var reader = request.GetBodyReader();
            reader.ReadString();
            if (member == "Get")
            {
                string property = reader.ReadString();
                if (!ItemProperties.Contains(property)) { context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", property); return; }
                Reply(context, "v", (ref MessageWriter w) => WriteItemProperty(ref w, property));
            }
            else if (member == "GetAll")
            {
                Reply(context, "a{sv}", (ref MessageWriter w) =>
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var property in ItemProperties)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(property);
                        WriteItemProperty(ref w, property);
                    }
                    w.WriteDictionaryEnd(dict);
                });
            }
            else context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "read only");
            return;
        }
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ItemXml]);
            return;
        }

        switch (member)
        {
            case "Activate":
                Log.Write("tray: Activate received");
                Post(() => Activated?.Invoke());
                break;
            case "Scroll":
                var reader = request.GetBodyReader();
                int delta = reader.ReadInt32();
                string orientation = reader.ReadString();
                if (orientation.Equals("vertical", StringComparison.OrdinalIgnoreCase) && delta != 0) Post(() => Scrolled?.Invoke(delta));
                break;
            case "SecondaryActivate" or "ContextMenu" or "ProvideXdgActivationToken" or "XAyatanaSecondaryActivate":
                // The tray opens the menu itself from the Menu property.
                break;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
        if (!context.NoReplyExpected) Reply(context, "", (ref MessageWriter _) => { });
    }

    private static readonly string[] ItemProperties =
    [
        "Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName", "OverlayIconPixmap",
        "AttentionIconName", "AttentionIconPixmap", "AttentionMovieName", "ToolTip", "ItemIsMenu", "Menu", "IconThemePath",
    ];

    private void WriteItemProperty(ref MessageWriter w, string property)
    {
        switch (property)
        {
            case "Category": w.WriteVariantString("Hardware"); break;
            case "Id": w.WriteVariantString("dispctrl-linux"); break;
            case "Title": w.WriteVariantString("DispCtrl"); break;
            case "Status": w.WriteVariantString("Active"); break;
            case "WindowId": w.WriteVariantInt32(0); break;
            case "IconName" or "OverlayIconName" or "AttentionIconName" or "AttentionMovieName" or "IconThemePath":
                w.WriteVariantString(""); break;
            case "IconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, _icons);
                break;
            case "OverlayIconPixmap" or "AttentionIconPixmap":
                w.WriteSignature("a(iiay)");
                WritePixmaps(ref w, []);
                break;
            case "ToolTip":
                string tip;
                lock (_lock) tip = _toolTip;
                w.WriteSignature("(sa(iiay)ss)");
                w.WriteStructureStart();
                w.WriteString("");
                WritePixmaps(ref w, []);
                w.WriteString("DispCtrl");
                w.WriteString(tip == "DispCtrl" ? "" : tip);
                break;
            case "ItemIsMenu": w.WriteVariantBool(false); break;
            case "Menu": w.WriteVariantObjectPath(MenuPath); break;
        }
    }

    private static void WritePixmaps(ref MessageWriter w, (int Width, int Height, byte[] Argb)[] icons)
    {
        var array = w.WriteArrayStart(DBusType.Struct);
        foreach (var (width, height, argb) in icons)
        {
            w.WriteStructureStart();
            w.WriteInt32(width);
            w.WriteInt32(height);
            w.WriteArray(argb);
        }
        w.WriteArrayEnd(array);
    }

    // ---- com.canonical.dbusmenu ---------------------------------------------

    private void HandleMenu(MethodContext context)
    {
        var request = context.Request;
        string member = request.MemberAsString ?? "";
        if (request.InterfaceAsString == "org.freedesktop.DBus.Properties")
        {
            var reader = request.GetBodyReader();
            reader.ReadString();
            if (member == "Get")
            {
                string property = reader.ReadString();
                Reply(context, "v", (ref MessageWriter w) => WriteMenuProperty(ref w, property));
            }
            else if (member == "GetAll")
            {
                Reply(context, "a{sv}", (ref MessageWriter w) =>
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var property in new[] { "Version", "TextDirection", "Status", "IconThemePath" })
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(property);
                        WriteMenuProperty(ref w, property);
                    }
                    w.WriteDictionaryEnd(dict);
                });
            }
            else context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "read only");
            return;
        }
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([MenuXml]);
            return;
        }

        Dictionary<int, TrayMenuItem> items;
        uint revision;
        lock (_lock) { items = _items; revision = _revision; }

        switch (member)
        {
            case "GetLayout":
            {
                var reader = request.GetBodyReader();
                int parent = reader.ReadInt32();
                int depth = reader.ReadInt32();
                var node = items.GetValueOrDefault(parent) ?? items[0];
                Reply(context, "u(ia{sv}av)", (ref MessageWriter w) =>
                {
                    w.WriteUInt32(revision);
                    WriteNode(ref w, node, depth);
                });
                return;
            }
            case "GetGroupProperties":
            {
                var reader = request.GetBodyReader();
                var ids = reader.ReadArrayOfInt32();
                Reply(context, "a(ia{sv})", (ref MessageWriter w) =>
                {
                    var array = w.WriteArrayStart(DBusType.Struct);
                    foreach (var id in ids.Length == 0 ? items.Keys.ToArray() : ids)
                    {
                        if (!items.TryGetValue(id, out var node)) continue;
                        w.WriteStructureStart();
                        w.WriteInt32(id);
                        WriteProperties(ref w, node);
                    }
                    w.WriteArrayEnd(array);
                });
                return;
            }
            case "GetProperty":
            {
                var reader = request.GetBodyReader();
                int id = reader.ReadInt32();
                string name = reader.ReadString();
                if (!items.TryGetValue(id, out var node)) { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"no item {id}"); return; }
                Reply(context, "v", (ref MessageWriter w) =>
                {
                    switch (name)
                    {
                        case "label": w.WriteVariantString(node.Label); break;
                        case "enabled": w.WriteVariantBool(node.Enabled); break;
                        default: w.WriteVariantString(""); break;
                    }
                });
                return;
            }
            case "Event":
            {
                var reader = request.GetBodyReader();
                int id = reader.ReadInt32();
                string kind = reader.ReadString();
                if (kind == "clicked") Click(items, id);
                if (!context.NoReplyExpected) Reply(context, "", (ref MessageWriter _) => { });
                return;
            }
            case "EventGroup":
            {
                var reader = request.GetBodyReader();
                var array = reader.ReadArrayStart(DBusType.Struct);
                var missing = new List<int>();
                while (reader.HasNext(array))
                {
                    reader.AlignStruct();
                    int id = reader.ReadInt32();
                    string kind = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (!items.ContainsKey(id)) missing.Add(id);
                    else if (kind == "clicked") Click(items, id);
                }
                Reply(context, "ai", (ref MessageWriter w) => w.WriteArray(missing.ToArray()));
                return;
            }
            case "AboutToShow":
                Reply(context, "b", (ref MessageWriter w) => w.WriteBool(false));
                return;
            case "AboutToShowGroup":
                Reply(context, "aiai", (ref MessageWriter w) => { w.WriteArray(Array.Empty<int>()); w.WriteArray(Array.Empty<int>()); });
                return;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
    }

    private void Click(Dictionary<int, TrayMenuItem> items, int id)
    {
        if (items.TryGetValue(id, out var node) && node.Enabled && node.Click is { } click)
        {
            Log.Write($"tray: menu item '{node.Label}' clicked");
            Post(click);
        }
        else Log.Write($"tray: menu click {id} ignored (unknown or disabled)");
    }

    private static void WriteMenuProperty(ref MessageWriter w, string property)
    {
        switch (property)
        {
            case "Version": w.WriteVariantUInt32(3); break;
            case "TextDirection": w.WriteVariantString("ltr"); break;
            case "Status": w.WriteVariantString("normal"); break;
            default:
                w.WriteSignature("as");
                w.WriteArray(Array.Empty<string>());
                break;
        }
    }

    private static void WriteNode(ref MessageWriter w, TrayMenuItem node, int depth)
    {
        w.WriteStructureStart();
        w.WriteInt32(node.Id);
        WriteProperties(ref w, node);
        var children = w.WriteArrayStart(DBusType.Variant);
        if (depth != 0)
        {
            foreach (var child in node.Children)
            {
                w.WriteSignature("(ia{sv}av)");
                WriteNode(ref w, child, depth - 1);
            }
        }
        w.WriteArrayEnd(children);
    }

    private static void WriteProperties(ref MessageWriter w, TrayMenuItem node)
    {
        var dict = w.WriteDictionaryStart();
        void Entry(ref MessageWriter w, string key) { w.WriteDictionaryEntryStart(); w.WriteString(key); }
        if (node.IsSeparator)
        {
            Entry(ref w, "type"); w.WriteVariantString("separator");
        }
        else if (node.Id != 0)
        {
            Entry(ref w, "label"); w.WriteVariantString(node.Label.Replace("_", "__", StringComparison.Ordinal));
            if (!node.Enabled) { Entry(ref w, "enabled"); w.WriteVariantBool(false); }
            if (node.Toggle != TrayToggle.None)
            {
                Entry(ref w, "toggle-type"); w.WriteVariantString(node.Toggle == TrayToggle.Check ? "checkmark" : "radio");
                Entry(ref w, "toggle-state"); w.WriteVariantInt32(node.Checked ? 1 : 0);
            }
        }
        if (node.Children.Count > 0) { Entry(ref w, "children-display"); w.WriteVariantString("submenu"); }
        w.WriteDictionaryEnd(dict);
    }

    // ---- plumbing -----------------------------------------------------------

    private static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    private static void Reply(MethodContext context, string signature, Body body)
    {
        if (context.NoReplyExpected) return;
        var w = context.CreateReplyWriter(signature.Length == 0 ? null! : signature);
        try
        {
            body(ref w);
            context.Reply(w.CreateMessage());
        }
        finally { w.Dispose(); }
    }

    private sealed class Handler(string path, Action<MethodContext> handle) : IPathMethodHandler
    {
        public string Path => path;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try { handle(context); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"tray: {context.Request.MemberAsString}: {ex.Message}");
                if (!context.ReplySent && !context.NoReplyExpected) context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
            }
            return default;
        }
    }

    private static readonly ReadOnlyMemory<byte> ItemXml = """
        <interface name="org.kde.StatusNotifierItem">
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewToolTip"/>
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
        </interface>
        """u8.ToArray();

    private static readonly ReadOnlyMemory<byte> MenuXml = """
        <interface name="com.canonical.dbusmenu">
          <method name="GetLayout"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="as" direction="in"/><arg type="u" direction="out"/><arg type="(ia{sv}av)" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" direction="in"/><arg type="as" direction="in"/><arg type="a(ia{sv})" direction="out"/></method>
          <method name="GetProperty"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
          <method name="Event"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/><arg type="u" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" direction="in"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" direction="in"/><arg type="b" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" direction="in"/><arg type="ai" direction="out"/><arg type="ai" direction="out"/></method>
          <signal name="LayoutUpdated"><arg type="u"/><arg type="i"/></signal>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})"/><arg type="a(ias)"/></signal>
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
        </interface>
        """u8.ToArray();

    public void Dispose()
    {
        _stop.Cancel();
        _connection.Dispose();
    }
}
