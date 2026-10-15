using System.Globalization;

namespace DispCtrl.Linux.Lighting;

/// <summary>One LED drawn as a key: <paramref name="Id"/> is its canonical key
/// ("a", "lshift", "num7") or null when it is not a key of the layout.</summary>
/// <param name="Label">What the cap says, kept short to fit it ("Shift", "7" on the numpad); tooltips add the LED.</param>
/// <param name="X">Left edge, in key units (a letter key is 1 wide).</param>
/// <param name="OnGrid">False for the LEDs on the strip below the keys (logo, lightbar, underglow, unused).</param>
public sealed record KeyCap(int Led, string? Id, string Label, double X, double Y, double W, double H, bool OnGrid);

/// <summary>Where a keyboard's LEDs sit, in key units, with the strip of the rest below.</summary>
public sealed record KeyboardGeometry(IReadOnlyList<KeyCap> Keys, double Width, double Height, string Source)
{
    private Dictionary<(int, int), List<KeyCap>>? _buckets;

    /// <summary>The key under a point, in key units; null between keys.</summary>
    /// <remarks>Keys are bucketed by half unit, so a pointer move during a drag
    /// looks at a handful of keys rather than all of them (the window calls this
    /// on every pointer event).</remarks>
    public KeyCap? KeyAt(double x, double y)
    {
        if (_buckets is null)
        {
            var buckets = new Dictionary<(int, int), List<KeyCap>>();
            foreach (var k in Keys)
                for (int cx = (int)Math.Floor(k.X * 2); cx < Math.Ceiling((k.X + k.W) * 2); cx++)
                    for (int cy = (int)Math.Floor(k.Y * 2); cy < Math.Ceiling((k.Y + k.H) * 2); cy++)
                    {
                        if (!buckets.TryGetValue((cx, cy), out var list)) buckets[(cx, cy)] = list = [];
                        list.Add(k);
                    }
            _buckets = buckets;
        }
        if (!_buckets.TryGetValue(((int)Math.Floor(x * 2), (int)Math.Floor(y * 2)), out var near)) return null;
        foreach (var k in near)
            if (x >= k.X && x < k.X + k.W && y >= k.Y && y < k.Y + k.H) return k;
        return null;
    }
}

/// <summary>
/// Turns a keyboard's LEDs into key rectangles to draw and to name keys by.
/// </summary>
/// <remarks>
/// OpenRGB drivers describe a keyboard in one of two ways, and some in both.
/// Most name the LEDs ("Key: A", "Key: Left Shift"), which is placed on a
/// standard ANSI or ISO layout (full-size, tenkeyless, 75% and 60% are the same
/// layout with keys missing, so the keys found are placed at the standard
/// positions and the empty rows and columns are trimmed away). Some, the EVision
/// keyboard on the reference desk among them, name every LED "Keyboard LED 12"
/// and only give a zone matrix. That matrix is the controller's wiring grid,
/// not key positions (a wide key takes one cell and the cells beside it are
/// empty), so it is not drawn as it stands: when its rows hold as many LEDs as a
/// standard layout's rows, the LEDs are matched to those keys in order. A
/// matrix that matches nothing is drawn as the grid it is, unlabelled. LEDs
/// the layout does not place go on a strip below. Pure geometry: no device is
/// touched.
/// </remarks>
public static class KeyboardLayout
{
    private sealed record Template(string Id, string Label, double X, double Y, double W = 1, double H = 1);

    private static readonly string[] FunctionKeys = ["f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12"];

    // Standard ANSI full-size, in key units from its top-left corner.
    private static readonly List<Template> Standard = BuildStandard();

    private static readonly Dictionary<string, Template> ById = Standard.ToDictionary(t => t.Id);

    private static List<Template> BuildStandard()
    {
        var keys = new List<Template>();
        void Add(string id, string label, double x, double y, double w = 1, double h = 1) => keys.Add(new Template(id, label, x, y, w, h));

        Add("esc", "Esc", 0, 0);
        for (int i = 0; i < 12; i++) Add(FunctionKeys[i], $"F{i + 1}", 2 + i + i / 4 * 0.5, 0);
        Add("prtsc", "PrtSc", 15.25, 0); Add("scrlk", "ScrLk", 16.25, 0); Add("pause", "Pause", 17.25, 0);

        string row1 = "`1234567890-=";
        for (int i = 0; i < row1.Length; i++) Add(row1[i].ToString(), row1[i].ToString(), i, 1.5);
        Add("bksp", "Backspace", 13, 1.5, 2);
        Add("ins", "Ins", 15.25, 1.5); Add("home", "Home", 16.25, 1.5); Add("pgup", "PgUp", 17.25, 1.5);
        Add("numlk", "NumLk", 18.5, 1.5); Add("num/", "/", 19.5, 1.5); Add("num*", "*", 20.5, 1.5); Add("num-", "-", 21.5, 1.5);

        Add("tab", "Tab", 0, 2.5, 1.5);
        string row2 = "qwertyuiop[]";
        for (int i = 0; i < row2.Length; i++) Add(row2[i].ToString(), row2[i].ToString().ToUpperInvariant(), 1.5 + i, 2.5);
        Add("\\", "\\", 13.5, 2.5, 1.5);
        Add("del", "Del", 15.25, 2.5); Add("end", "End", 16.25, 2.5); Add("pgdn", "PgDn", 17.25, 2.5);
        Add("num7", "7", 18.5, 2.5); Add("num8", "8", 19.5, 2.5); Add("num9", "9", 20.5, 2.5); Add("num+", "+", 21.5, 2.5, 1, 2);

        Add("caps", "Caps", 0, 3.5, 1.75);
        string row3 = "asdfghjkl;'";
        for (int i = 0; i < row3.Length; i++) Add(row3[i].ToString(), row3[i].ToString().ToUpperInvariant(), 1.75 + i, 3.5);
        Add("enter", "Enter", 12.75, 3.5, 2.25);
        Add("num4", "4", 18.5, 3.5); Add("num5", "5", 19.5, 3.5); Add("num6", "6", 20.5, 3.5);

        Add("lshift", "Shift", 0, 4.5, 2.25);
        string row4 = "zxcvbnm,./";
        for (int i = 0; i < row4.Length; i++) Add(row4[i].ToString(), row4[i].ToString().ToUpperInvariant(), 2.25 + i, 4.5);
        Add("rshift", "Shift", 12.25, 4.5, 2.75);
        Add("up", "Up", 16.25, 4.5);
        Add("num1", "1", 18.5, 4.5); Add("num2", "2", 19.5, 4.5); Add("num3", "3", 20.5, 4.5); Add("numenter", "Ent", 21.5, 4.5, 1, 2);

        Add("lctrl", "Ctrl", 0, 5.5, 1.25); Add("lwin", "Win", 1.25, 5.5, 1.25); Add("lalt", "Alt", 2.5, 5.5, 1.25);
        Add("space", "Space", 3.75, 5.5, 6.25);
        Add("ralt", "Alt", 10, 5.5, 1.25); Add("fn", "Fn", 11.25, 5.5, 1.25); Add("menu", "Menu", 12.5, 5.5, 1.25); Add("rctrl", "Ctrl", 13.75, 5.5, 1.25);
        Add("left", "Left", 15.25, 5.5); Add("down", "Down", 16.25, 5.5); Add("right", "Right", 17.25, 5.5);
        Add("num0", "0", 18.5, 5.5, 2); Add("num.", ".", 20.5, 5.5);

        return keys;
    }

    // The ISO keyboard's different keys: the extra key beside Left Shift, the
    // key above Enter that ANSI calls backslash, and the tall Enter.
    private static readonly Dictionary<string, Template> Iso = new()
    {
        ["lshift"] = new("lshift", "Shift", 0, 4.5, 1.25),
        ["iso<"] = new("iso<", "<", 1.25, 4.5),
        ["iso#"] = new("iso#", "#", 12.75, 3.5),
        ["enter"] = new("enter", "Enter", 13.75, 2.5, 1.25, 2),
    };

    // Matrix rows of the standard layouts, for matching a nameless controller's
    // rows by count: main block, then navigation block, then numpad.
    private static readonly string[][] FullRows =
    [
        ["esc", .. FunctionKeys, "prtsc", "scrlk", "pause"],
        [.. "`1234567890-=".Select(c => c.ToString()), "bksp", "ins", "home", "pgup", "numlk", "num/", "num*", "num-"],
        ["tab", .. "qwertyuiop[]".Select(c => c.ToString()), "\\", "del", "end", "pgdn", "num7", "num8", "num9", "num+"],
        ["caps", .. "asdfghjkl;'".Select(c => c.ToString()), "enter", "num4", "num5", "num6"],
        ["lshift", .. "zxcvbnm,./".Select(c => c.ToString()), "rshift", "up", "num1", "num2", "num3", "numenter"],
        ["lctrl", "lwin", "lalt", "space", "ralt", "fn", "menu", "rctrl", "left", "down", "right", "num0", "num."],
    ];

    private static readonly string[][] TenkeylessRows = FullRows
        .Select(r => r.Where(id => !id.StartsWith("num", StringComparison.Ordinal)).ToArray()).ToArray();

    private static readonly string[][] SixtyRows = TenkeylessRows.Skip(1)
        .Select(r => r.Where(id => id is not ("ins" or "home" or "pgup" or "del" or "end" or "pgdn" or "up" or "left" or "down" or "right")).ToArray()).ToArray();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["escape"] = "esc", ["printscreen"] = "prtsc", ["prtscn"] = "prtsc", ["printscr"] = "prtsc", ["scrolllock"] = "scrlk",
        ["pausebreak"] = "pause", ["pause/break"] = "pause", ["break"] = "pause", ["backtick"] = "`", ["grave"] = "`", ["~"] = "`",
        ["minus"] = "-", ["equals"] = "=", ["equal"] = "=", ["backspace"] = "bksp", ["insert"] = "ins", ["pageup"] = "pgup",
        ["pagedown"] = "pgdn", ["delete"] = "del", ["numlock"] = "numlk", ["capslock"] = "caps", ["return"] = "enter",
        ["leftshift"] = "lshift", ["rightshift"] = "rshift", ["shift"] = "lshift", ["leftcontrol"] = "lctrl", ["leftctrl"] = "lctrl",
        ["rightcontrol"] = "rctrl", ["rightctrl"] = "rctrl", ["control"] = "lctrl", ["ctrl"] = "lctrl",
        ["leftwindows"] = "lwin", ["leftwin"] = "lwin", ["leftsuper"] = "lwin", ["leftmeta"] = "lwin", ["leftgui"] = "lwin",
        ["windows"] = "lwin", ["win"] = "lwin", ["super"] = "lwin", ["meta"] = "lwin",
        ["leftalt"] = "lalt", ["rightalt"] = "ralt", ["altgr"] = "ralt", ["alt"] = "lalt", ["rightfn"] = "fn", ["leftfn"] = "fn",
        ["application"] = "menu", ["context"] = "menu", ["spacebar"] = "space",
        ["uparrow"] = "up", ["downarrow"] = "down", ["leftarrow"] = "left", ["rightarrow"] = "right",
        ["\\(ansi)"] = "\\", ["\\(iso)"] = "iso<", ["<"] = "iso<", ["#(iso)"] = "iso#", ["#"] = "iso#", ["enter(iso)"] = "enter",
        ["comma"] = ",", ["period"] = ".", ["dot"] = ".", ["slash"] = "/", ["semicolon"] = ";", ["quote"] = "'", ["apostrophe"] = "'",
        ["leftbracket"] = "[", ["rightbracket"] = "]", ["backslash"] = "\\", ["tilde"] = "`",
        ["numpadenter"] = "numenter", ["numberpadenter"] = "numenter", ["numpad."] = "num.", ["numberpad."] = "num.",
    };

    /// <summary>The canonical key a name stands for ("Key: Left Shift", "lshift" and
    /// "left shift" are all "lshift"); null when it names none of the layout's keys.</summary>
    public static string? Canonical(string name)
    {
        var text = name.Trim().ToLowerInvariant();
        if (text.StartsWith("key:", StringComparison.Ordinal)) text = text[4..].Trim();
        text = text.Replace(" ", "").Replace("_", "");
        if (text.Length == 0) return null;
        if (Aliases.TryGetValue(text, out var alias)) return alias;
        foreach (var prefix in new[] { "numberpad", "numpad", "keypad", "num" })
            if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length)
            {
                var rest = text[prefix.Length..];
                var id = "num" + (rest == "lock" ? "lk" : rest);
                if (ById.ContainsKey(id)) return id;
            }
        return ById.ContainsKey(text) || Iso.ContainsKey(text) ? text : null;
    }

    /// <summary>The geometry of a keyboard device. A device with neither
    /// recognisable names nor a matrix is laid out as a plain grid of unlabelled
    /// LEDs, so every LED can still be reached.</summary>
    public static KeyboardGeometry Build(RgbDevice device)
    {
        int count = device.LedCount;
        var names = Enumerable.Range(0, count).Select(i => device.Leds is { } l && i < l.Count ? l[i].Name : "").ToArray();

        var ids = names.Select(n => Canonical(n)).ToArray();
        var placed = new Dictionary<int, Template>();
        string source;
        if (ids.Count(id => id is not null) * 2 >= count && ids.Count(id => id is not null) >= 8)
        {
            bool iso = ids.Contains("iso<") || ids.Contains("iso#");
            for (int i = 0; i < count; i++)
                if (ids[i] is { } id && !placed.Values.Any(t => t.Id == id))
                    placed[i] = iso && Iso.TryGetValue(id, out var t) ? t : ById.GetValueOrDefault(id) ?? Iso[id];
            if (iso) placed = placed.Where(p => p.Value.Id != "\\").ToDictionary(p => p.Key, p => p.Value);
            source = "names";
        }
        else if (MatchMatrix(device) is { } byMatrix)
        {
            placed = byMatrix;
            source = "matrix order";
        }
        else if (RawMatrix(device) is { } raw)
        {
            return Strip(raw.Placed, count, raw.Width, raw.Height, "matrix", names);
        }
        else
        {
            return Strip([], count, 0, 0, "none", names);
        }

        if (placed.Count == 0) return Strip([], count, 0, 0, "none", names);
        double left = placed.Values.Min(t => t.X), top = placed.Values.Min(t => t.Y);
        double right = placed.Values.Max(t => t.X + t.W), bottom = placed.Values.Max(t => t.Y + t.H);
        var caps = placed.Select(p => new KeyCap(p.Key, p.Value.Id, p.Value.Label, p.Value.X - left, p.Value.Y - top, p.Value.W, p.Value.H, true)).ToList();
        return Strip(caps, count, right - left, bottom - top, source, names);
    }

    /// <summary>The LEDs of the keys by canonical id; ids come from names or labels.</summary>
    public static IReadOnlyList<int> Find(RgbDevice device, string text)
    {
        text = text.Trim();
        if (text.StartsWith("led", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[3..].Trim(' ', ':'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int led))
            return led >= 0 && led < device.LedCount ? [led] : [];
        var geometry = Build(device);
        var wanted = Canonical(text);
        var found = geometry.Keys.Where(k => wanted is not null && k.Id == wanted).Select(k => k.Led).ToList();
        if (found.Count == 0 && device.Leds is { } leds)
            found = Enumerable.Range(0, leds.Count).Where(i => leds[i].Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return found;
    }

    /// <summary>Puts the LEDs not on the grid on a strip below it, one key wide
    /// each, as wide as the grid (or 12 keys when there is none).</summary>
    private static KeyboardGeometry Strip(List<KeyCap> caps, int count, double width, double height, string source, string[] names)
    {
        var on = caps.Select(k => k.Led).ToHashSet();
        var rest = Enumerable.Range(0, count).Where(i => !on.Contains(i)).ToList();
        double perRow = Math.Max(1, Math.Floor(width > 0 ? width : 12));
        double y = caps.Count > 0 ? height + 0.5 : 0;
        for (int n = 0; n < rest.Count; n++)
        {
            double x = n % (int)perRow, row = n / (int)perRow;
            var name = names[rest[n]];
            caps.Add(new KeyCap(rest[n], null, StripLabel(name), x, y + row, 1, 1, false));
        }
        int rows = rest.Count == 0 ? 0 : (rest.Count + (int)perRow - 1) / (int)perRow;
        return new KeyboardGeometry(caps, Math.Max(width, rest.Count > 0 ? Math.Min(rest.Count, perRow) : 0), caps.Count == 0 ? 0 : y + rows, source);
    }

    private static string StripLabel(string name)
    {
        if (name.StartsWith("Key:", StringComparison.OrdinalIgnoreCase)) return name[4..].Trim();
        // "Keyboard LED 12" says nothing the tooltip's number does not.
        return name.Contains(" LED ", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[^1]) ? "" : name;
    }

    /// <summary>Each zone's matrix cells as (row, column, device LED index), the
    /// zone's LEDs numbered after the zones before it.</summary>
    private static List<(int Row, int Col, int Led)> Cells(RgbDevice device)
    {
        var cells = new List<(int, int, int)>();
        int offset = 0, rowBase = 0;
        foreach (var zone in device.Zones ?? [])
        {
            if (zone.HasMatrix)
            {
                for (int r = 0; r < zone.MatrixHeight; r++)
                    for (int c = 0; c < zone.MatrixWidth; c++)
                    {
                        uint v = zone.Map[r * zone.MatrixWidth + c];
                        if (v != RgbZone.NoLed && v < zone.LedsCount && offset + (int)v < device.LedCount) cells.Add((rowBase + r, c, offset + (int)v));
                    }
                rowBase += zone.MatrixHeight;
            }
            offset += zone.LedsCount;
        }
        return cells;
    }

    private static (List<KeyCap> Placed, double Width, double Height)? RawMatrix(RgbDevice device)
    {
        var cells = Cells(device);
        if (cells.Count < 8) return null;
        int row0 = cells.Min(c => c.Row), col0 = cells.Min(c => c.Col);
        var caps = cells.Select(c => new KeyCap(c.Led, null, "", c.Col - col0, c.Row - row0, 1, 1, true)).ToList();
        return (caps, cells.Max(c => c.Col) - col0 + 1, cells.Max(c => c.Row) - row0 + 1);
    }

    /// <summary>Matches a matrix's rows, left to right, to a standard layout's
    /// rows when every row holds the same number of LEDs; null otherwise.</summary>
    private static Dictionary<int, Template>? MatchMatrix(RgbDevice device)
    {
        var rows = Cells(device).GroupBy(c => c.Row).OrderBy(g => g.Key).Select(g => g.OrderBy(c => c.Col).ToList()).ToList();
        foreach (var variant in new[] { FullRows, TenkeylessRows, SixtyRows })
        {
            if (rows.Count != variant.Length || !rows.Select(r => r.Count).SequenceEqual(variant.Select(r => r.Length))) continue;
            var placed = new Dictionary<int, Template>();
            for (int r = 0; r < rows.Count; r++)
                for (int k = 0; k < rows[r].Count; k++)
                    placed[rows[r][k].Led] = ById[variant[r][k]];
            return placed;
        }
        return null;
    }
}
