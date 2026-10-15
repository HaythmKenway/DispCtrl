using DispCtrl.Linux.Graphics;
using DispCtrl.Linux.X11;

namespace DispCtrl.Linux.Snap;

/// <summary>One window offered by Snap Assist.</summary>
public sealed record AssistCandidate(ulong Id, string Title, string? App, Rect Frame);

/// <summary>Draws the snap overlays. Everything is drawn from plain values onto
/// any canvas, so the same code fills the live overlay and the offscreen PNGs
/// the checks look at.</summary>
public static class SnapPainter
{
    // Windows 11's radii: 8 for the flyout, 4 inside it, larger for the
    // Snap Assist surface and its cards, which float on soft shadows.
    private const double PanelRadius = 12, TileRadius = 6, ZoneRadius = 4, PreviewRadius = 12, CardRadius = 10, AssistRadius = 16;

    /// <summary>The bar at the top while a window is dragged.</summary>
    public static void Handle(Canvas c, SnapPanel panel, DesktopTheme theme, Rect origin)
    {
        var h = Local(panel.Handle, origin);
        c.Shadow(h.X, h.Y, h.Width, h.Height, h.Height / 2.0, 6, theme.ShadowAlpha * 0.7);
        c.RoundedRect(h.X, h.Y, h.Width, h.Height, h.Height / 2.0);
        c.FillPreserve(theme.PanelBackground);
        c.Stroke(theme.PanelBorder, 1);
    }

    /// <summary>The layouts, the hovered zone lit in the accent colour, and where
    /// a window dropped there would go.</summary>
    public static void Panel(Canvas c, SnapPanel panel, ZoneRef? hover, int gap, DesktopTheme theme, Rect origin)
    {
        if (hover is { } preview)
        {
            var target = Local(panel.Target(preview, gap), origin);
            c.RoundedRect(target.X + 6, target.Y + 6, target.Width - 12, target.Height - 12, PreviewRadius);
            c.FillPreserve(theme.Accent.WithAlpha(0.16));
            c.Stroke(theme.Accent.WithAlpha(0.45), 1.5);
        }

        var p = Local(panel.Bounds, origin);
        c.Shadow(p.X, p.Y, p.Width, p.Height, PanelRadius, 18, theme.ShadowAlpha * 0.8);
        c.RoundedRect(p.X + 0.5, p.Y + 0.5, p.Width - 1, p.Height - 1, PanelRadius);
        c.FillPreserve(theme.PanelBackground);
        c.Stroke(theme.PanelBorder, 1);

        for (int l = 0; l < panel.Layouts.Count; l++)
        {
            var tile = Local(panel.Tiles[l], origin);
            bool tileLit = hover is { } h && h.Layout == l;
            c.RoundedRect(tile.X + 0.5, tile.Y + 0.5, tile.Width - 1, tile.Height - 1, TileRadius);
            c.FillPreserve(tileLit ? theme.Accent.WithAlpha(0.08) : theme.TileBackground);
            c.Stroke(tileLit ? theme.Accent.WithAlpha(0.35) : theme.TileBorder, 1);
            for (int z = 0; z < panel.Layouts[l].Zones.Count; z++)
            {
                var zone = new ZoneRef(l, z);
                var r = Local(panel.ZoneInTile(zone), origin);
                bool lit = hover == zone;
                c.RoundedRect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1, ZoneRadius);
                c.FillPreserve(lit ? theme.Accent.WithAlpha(0.9) : theme.ZoneFill);
                c.Stroke(lit ? theme.Accent.WithAlpha(0.9) : theme.ZoneBorder, 1);
            }
        }
    }

    /// <summary>Snap Assist: the free zone, holding a card for each window that
    /// could fill it. <paramref name="picture"/> draws a window's live picture
    /// into a card; null draws its initial instead (minimized windows, tests).</summary>
    public static void Assist(Canvas c, Rect zone, IReadOnlyList<AssistCandidate> candidates, int? hover, DesktopTheme theme, Rect origin,
        Action<Canvas, AssistCandidate, Rect>? picture)
    {
        var z = Local(zone, origin);
        c.Shadow(z.X + 6, z.Y + 6, z.Width - 12, z.Height - 12, AssistRadius, 20, theme.ShadowAlpha * 0.6);
        c.RoundedRect(z.X + 6, z.Y + 6, z.Width - 12, z.Height - 12, AssistRadius);
        c.FillPreserve(theme.AssistBackdrop);
        c.Stroke(theme.PanelBorder, 1);

        c.Text("Choose a window for this space", z.X + AssistGrid.Margin, z.Y + AssistGrid.Margin,
            z.Width - 2 * AssistGrid.Margin, theme.Text, $"{FontFamily(theme.Font)} 14px");
        c.Text("Esc, or a click elsewhere, leaves it empty", z.X + AssistGrid.Margin, z.Y + AssistGrid.Margin + 22,
            z.Width - 2 * AssistGrid.Margin, theme.SecondaryText, theme.Font);

        var cards = AssistGrid.Cards(zone, candidates.Count, AssistGrid.AspectOf(candidates));
        for (int i = 0; i < cards.Count; i++)
        {
            var card = Local(cards[i], origin);
            bool lit = hover == i;
            var thumb = new Rect(card.X, card.Y, card.Width, card.Height - AssistGrid.TitleHeight);
            if (lit)
            {
                // A soft lift under the pointer, as Windows draws it: a light
                // wash, not a hard outline.
                c.RoundedRect(card.X - 8, card.Y - 8, card.Width + 16, card.Height + 16, CardRadius + 6);
                c.Fill(theme.Accent.WithAlpha(0.14));
            }
            c.Shadow(thumb.X, thumb.Y, thumb.Width, thumb.Height, CardRadius, lit ? 12 : 8, theme.ShadowAlpha * (lit ? 0.8 : 0.5));
            c.RoundedRect(thumb.X, thumb.Y, thumb.Width, thumb.Height, CardRadius);
            c.Fill(theme.CardBackground);
            if (picture is not null) picture(c, candidates[i], thumb);
            else Initial(c, candidates[i], thumb, theme);
            c.RoundedRect(thumb.X + 0.5, thumb.Y + 0.5, thumb.Width - 1, thumb.Height - 1, CardRadius);
            c.Stroke(lit ? theme.Accent.WithAlpha(0.7) : theme.PanelBorder, lit ? 1.5 : 1);
            c.Text(candidates[i].Title.Length > 0 ? candidates[i].Title : candidates[i].App ?? "Window",
                card.X, thumb.Bottom + 7, card.Width, lit ? theme.Text : theme.SecondaryText, theme.Font, centre: true);
        }
    }

    /// <summary>The first letter of the app, for a window with no picture.</summary>
    public static void Initial(Canvas c, AssistCandidate candidate, Rect r, DesktopTheme theme)
    {
        string name = candidate.App ?? candidate.Title;
        string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
        int size = Math.Max(14, Math.Min(r.Width, r.Height) / 3);
        c.Text(letter, r.X, r.Y + (r.Height - size * 1.35) / 2, r.Width, theme.SecondaryText, $"{FontFamily(theme.Font)} {size}px", semibold: true, centre: true);
    }

    private static string FontFamily(string font)
    {
        int space = font.LastIndexOf(' ');
        return space > 0 && double.TryParse(font[(space + 1)..], out _) ? font[..space] : font;
    }

    private static Rect Local(Rect r, Rect origin) => new(r.X - origin.X, r.Y - origin.Y, r.Width, r.Height);
}
