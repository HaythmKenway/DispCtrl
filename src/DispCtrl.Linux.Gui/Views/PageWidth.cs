using Avalonia.Controls;

namespace DispCtrl.Linux.Gui.Views;

/// <summary>Gives a page's root the "narrow" class below the width where a
/// control beside its text would squeeze the text; the card styles then put
/// the control under it. Measured on the page, not the window, since the
/// navigation pane takes part of the window.</summary>
public static class PageWidth
{
    public const double Narrow = 600;

    public static void Track(Control page, Panel root) =>
        page.SizeChanged += (_, e) => root.Classes.Set("narrow", e.NewSize.Width < Narrow);
}
