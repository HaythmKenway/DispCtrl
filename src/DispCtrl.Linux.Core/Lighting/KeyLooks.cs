namespace DispCtrl.Linux.Lighting;

/// <summary>Ready-made colourings for a keyboard with a light for each key.</summary>
/// <remarks>
/// A look is a rule from a key (by its layout id and position) to a colour, not a
/// list of LEDs, so it fits any keyboard <see cref="KeyboardLayout"/> can place:
/// the same Productivity look lands on a 60%, a tenkeyless and a full board. LEDs
/// the layout does not place (logo, lightbar) take the look's base colour. Pure:
/// no device is touched.
/// </remarks>
public static class KeyLooks
{
    private delegate (byte R, byte G, byte B) Rule(KeyCap key);

    private sealed record Look(string Name, string Description, (byte, byte, byte) Base, Rule Colour);

    private static (byte, byte, byte) C(string hex) => LightingControl.TryColour(hex, out var c) ? c : (byte.MaxValue, byte.MaxValue, byte.MaxValue);

    private static bool In(string? id, string ids) => id is not null && ids.Split(' ').Contains(id);

    private static bool IsFunction(string id) => id.Length is 2 or 3 && id[0] == 'f' && int.TryParse(id[1..], out _);

    private static bool IsNumpad(string id) => id.StartsWith("num", StringComparison.Ordinal);

    private static bool IsLetter(string id) => id.Length == 1 && char.IsAsciiLetter(id[0]);

    private static readonly Look[] All =
    [
        new("Productivity", "Zones you learn at a glance: letters near-white, numbers blue, F-keys green, modifiers violet, shortcut keys orange.", C("#DCE9FF"), k =>
        {
            var id = k.Id ?? "";
            if (id == "esc") return C("#FF1744");
            if (In(id, "tab bksp enter") || In(id, "numlk num/ num* num- num+")) return C("#FFAB00");
            if (In(id, "z x c v s a f")) return C("#FF6D00");
            if (In(id, "lshift rshift caps lctrl rctrl lalt ralt lwin fn menu")) return C("#7C4DFF");
            if (id == "space") return C("#FFFFFF");
            if (In(id, "up down left right ins home pgup del end pgdn")) return C("#00E5FF");
            if (id == "numenter" || IsFunction(id)) return C("#00E676");
            if (IsNumpad(id) || (id.Length == 1 && char.IsAsciiDigit(id[0])) || In(id, "` - = minus equals")) return C("#2979FF");
            if (In(id, "prtsc scrlk pause")) return C("#7C8794");
            return C("#DCE9FF");
        }),
        new("Coding", "Like an editor theme: letters calm blue, brackets and operators gold, digits green, debug keys lit.", C("#7AA2F7"), k =>
        {
            var id = k.Id ?? "";
            if (id == "esc") return C("#F7768E");
            if (In(id, "f5 f9 f10 f11")) return C("#9ECE6A");
            if (In(id, "f12 f2")) return C("#E0AF68");
            if (IsFunction(id)) return C("#565F89");
            if (In(id, "[ ] { } ( ) \\ ' ; , . / = -") || In(id, "minus equals")) return C("#E0AF68");
            if (id.Length == 1 && char.IsAsciiDigit(id[0]) || id.StartsWith("num", StringComparison.Ordinal) && id.Length == 4 && char.IsAsciiDigit(id[3])) return C("#9ECE6A");
            if (In(id, "enter tab bksp")) return C("#BB9AF7");
            if (In(id, "up down left right home end pgup pgdn")) return C("#7DCFFF");
            if (In(id, "lshift rshift lctrl rctrl lalt ralt lwin caps fn menu")) return C("#414868");
            if (id == "space") return C("#C0CAF5");
            return C("#7AA2F7");
        }),
        new("Gaming", "Deep red with the movement keys, WASD and the keys around them, in bright cyan.", C("#7A0A1E"), k =>
        {
            var id = k.Id ?? "";
            if (In(id, "w a s d")) return C("#00E5FF");
            if (In(id, "q e r f c v x z g b shift lshift lctrl space")) return C("#FF8A00");
            if (id.Length == 1 && char.IsAsciiDigit(id[0])) return C("#FF3D00");
            if (id == "esc") return C("#FFFFFF");
            if (In(id, "up down left right")) return C("#00E5FF");
            if (IsFunction(id)) return C("#B3122E");
            return C("#7A0A1E");
        }),
        new("Focus", "Easy on the eyes late at night: a dim amber glow, the home row a little brighter so your hands find it.", C("#5C2A00"), k =>
        {
            var id = k.Id ?? "";
            if (In(id, "a s d f j k l semicolon ;")) return C("#B35F00");
            if (In(id, "space enter")) return C("#8A4500");
            if (id == "esc") return C("#7A1F00");
            return C("#5C2A00");
        }),
        new("Meow", "A tabby cat: orange stripes, a cream muzzle on the space bar, green eyes, and M E O W lit up.", C("#FF8A2B"), k =>
        {
            var id = k.Id ?? "";
            if (In(id, "m e o w")) return C("#FFFFFF");
            if (In(id, "esc enter numenter")) return C("#7CFC00");
            if (In(id, "space up down left right num0 num.")) return C("#FFE6C7");
            if (IsFunction(id)) return C("#B54A00");
            // Stripes: every other pair of columns darker, running through the main block.
            return (int)Math.Floor(k.X / 2) % 2 == 0 ? C("#FF8A2B") : C("#B54A00");
        }),
    ];

    public static IReadOnlyList<string> Names => All.Select(l => l.Name).ToList();

    public static string? Describe(string name) => Find(name)?.Description;

    private static Look? Find(string name) => All.FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour of every LED of the keyboard under a look; null when no look has the name.</summary>
    public static Dictionary<int, (byte R, byte G, byte B)>? Colours(string name, RgbDevice device)
    {
        if (Find(name) is not { } look) return null;
        var colours = new Dictionary<int, (byte, byte, byte)>();
        foreach (var key in KeyboardLayout.Build(device).Keys)
            colours[key.Led] = key.Id is null || !key.OnGrid ? look.Base : look.Colour(key);
        return colours;
    }
}
