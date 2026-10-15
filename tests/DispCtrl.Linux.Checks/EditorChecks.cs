using System.Diagnostics;
using DispCtrl.Linux.Arrangement;
using DispCtrl.Linux.Hardware;

/// <summary>The arrangement editor behind the Displays page's canvas: what a
/// drag, a key press and the inspector may do, without a display.</summary>
internal static class EditorChecks
{
    public static void Run(Action<bool, string> check)
    {
        // The Dell, 1920x1080, with the 2880x1800 panel to its right.
        List<XRandROutput> desk =
        [
            new("DP-1", true, true, 1920, 1080, 0, 0, 527, 296),
            new("eDP", true, false, 2880, 1800, 1920, 0, 300, 190),
        ];
        var editor = new ArrangementEditor(desk);
        check(!editor.IsChanged && !editor.CanUndo, "editor: a fresh desk has nothing staged");

        check(editor.Nudge("eDP", ArrangementEditor.Direction.Down) && editor.Positions["eDP"].Y > 0
            && editor.Positions["eDP"].X < 1920 + 2880, "editor: a nudge moves to a legal place below");
        check(editor.IsChanged && editor.CanUndo, "editor: a nudge is a change that can be undone");
        check(editor.Undo() && !editor.IsChanged && editor.Positions["eDP"] == (1920, 0), "editor: undo puts it back");
        check(!editor.Nudge("eDP", ArrangementEditor.Direction.Right), "editor: nothing further right is refused, not invented");

        check(editor.TrySet("eDP", 4000, 0) is { } gap && gap.Contains("touch", StringComparison.Ordinal),
            "editor: a typed position with a gap is refused");
        check(editor.TrySet("eDP", 100, 0) is { } overlap && overlap.Contains("overlap", StringComparison.Ordinal),
            "editor: a typed position that overlaps is refused");
        check(editor.TrySet("eDP", 0, 1080) is null && editor.Positions["eDP"] == (0, 1080),
            "editor: a typed position that touches is staged");
        editor.Revert();
        check(!editor.IsChanged && !editor.CanUndo, "editor: revert drops everything staged");

        editor.MakeMain("eDP");
        check(editor.PrimaryChanged && editor.IsChanged && editor.Primary == "eDP", "editor: making another display main is a change");
        check(editor.Undo() && editor.Primary == "DP-1" && !editor.IsChanged, "editor: undo also puts the main display back");

        editor.Drop("eDP", 0, 1080);
        check(editor.ArrangeArguments().SequenceEqual(["display", "arrange", "DP-1=0,0", "eDP=0,1080"]),
            "editor: apply sends every display's place in one command");

        var guides = ArrangementEditor.Guides(new("a", 0, 0, 100, 50), [new("b", 100, 0, 80, 50), new("c", 500, 400, 10, 10)], 2);
        check(guides.Any(g => !g.Vertical && g.At == 0) && !guides.Any(g => g.At >= 400), "guides: shared top edge shows, a far display's does not");
        check(ArrangementEditor.Guides(new("a", 0, 0, 100, 50), [new("b", 100, 20, 80, 50)], 2).Count == 1,
            "guides: touching edges alone give just the shared vertical line");

        List<XRandROutput> mirrored =
        [
            new("DP-1", true, true, 1920, 1080, 0, 0, 527, 296),
            new("HDMI-1", true, false, 1920, 1080, 0, 0, 527, 296),
            new("DP-2", true, false, 0, 0, 0, 0, 0, 0),
        ];
        check(ArrangementEditor.MirrorGroups(mirrored) is [{ Count: 2 }], "mirror: two displays at one place and size are mirrored");
        check(ArrangementEditor.SwitchedOff(mirrored) is ["DP-2"], "off: a connected display with no mode is listed as switched off");
        check(ArrangementEditor.Orientation(90) == "Portrait, turned left" && ArrangementEditor.Orientation(0) == "Landscape",
            "orientation: described in words");
        check(ArrangementEditor.PixelsPerInch(desk[0]) is 93, "density: from the panel's reported width");
        check(ArrangementEditor.PixelsPerInch(mirrored[2]) is null, "density: none when the display reports no size");

        // The drag recomputes slots and the layout on every pointer move; it has to be far cheaper than a frame.
        List<XRandROutput> big = Enumerable.Range(0, 6)
            .Select(i => new XRandROutput($"DP-{i}", true, i == 0, 1920, 1080, i * 1920, 0, 527, 296)).ToList();
        var heavy = new ArrangementEditor(big);
        var watch = Stopwatch.StartNew();
        const int rounds = 500;
        for (int i = 0; i < rounds; i++)
        {
            _ = DispCtrl.Core.Arrangement.ArrangementSlots.For(heavy.Panels(), "DP-3");
            _ = ArrangementEditor.Guides(new("DP-3", 100, 5, 1920, 1080), big.Select(o => new ArrangementEditor.Box(o.Name, o.PosX, o.PosY, o.WidthPx, o.HeightPx)), 20);
        }
        double perMove = watch.Elapsed.TotalMilliseconds / rounds;
        Console.WriteLine($"     drag step on six displays: {perMove:0.000} ms");
        check(perMove < 4, "perf: a drag step on six displays costs well under a frame");
    }
}
