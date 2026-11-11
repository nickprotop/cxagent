using SharpConsoleUI;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>One part of a share bar: its share of the whole, 0 to 1, and its colour.</summary>
public sealed record BarPart(double Share, Color Colour);

/// <summary>
/// The side panel's share bars: parts of one whole, each in its colour, the rest a track — an instance's share of the
/// session's tokens, or the lead against its workers.
///
/// <para>FROM THE THEME, READ WHEN DRAWN, as the gauge is: a redraw after a theme change recolours them.</para>
/// </summary>
public static class PanelBars
{
    // THE ROLES INSTANCES ARE COLOURED BY, in order — not caution or danger, which would read as an alarm.
    private static readonly ColorRole[] Roles = [ColorRole.Primary, ColorRole.Secondary, ColorRole.Tertiary, ColorRole.Info, ColorRole.Success];

    // WHAT EACH ROLE FALLS BACK TO BEFORE A THEME IS SET — distinct, so the order still tells instances apart.
    private static readonly Color[] Fallbacks =
        [new(0x4f, 0xc3, 0xf7), new(0xb3, 0x9d, 0xdb), new(0x80, 0xcb, 0xc4), new(0x90, 0xca, 0xf9), new(0x81, 0xc7, 0x84)];

    /// <summary>How many colours the sequence has before it repeats.</summary>
    public static int Palette => Roles.Length;

    /// <summary>The colour of the instance at <paramref name="index"/> in the panel's order — the same each draw.</summary>
    public static Color InstanceColour(int index) => ColorScheme.RoleRgb(Roles[index % Roles.Length], Fallbacks[index % Roles.Length]);

    /// <summary>The colour of each cell of a bar <paramref name="width"/> columns wide.</summary>
    public static List<Color> Cells(IReadOnlyList<BarPart> parts, int width)
    {
        var cells = new List<Color>(width);
        var at = 0.0;
        foreach (var part in parts)
        {
            if (part.Share <= 0) continue;
            var end = Math.Min(1, at + part.Share);
            // ROUNDED AT EACH EDGE, so the parts tile the bar with no gap or overlap — and A SLIVER STAYS SEEN: a share
            // above nothing takes at least one cell.
            var from = (int)Math.Round(at * width);
            var to = Math.Max(from + 1, (int)Math.Round(end * width));
            while (cells.Count < Math.Min(to, width)) cells.Add(part.Colour);
            at = end;
        }
        while (cells.Count < width) cells.Add(PanelGauge.Track);
        return cells;
    }

    /// <summary>The bar as markup, one background-coloured cell per column.</summary>
    public static string Markup(IReadOnlyList<BarPart> parts, int width) =>
        string.Concat(Cells(parts, width).Select(c => $"[on {c.ToMarkup()}] [/]"));
}
