using SharpConsoleUI;
using SharpConsoleUI.Helpers;

namespace CxAgent.UI;

/// <summary>What a gauge shows: how full, as a share of the whole, and where a mark sits — or null for none.</summary>
/// <param name="Fraction">How full, 0 to 1; anything outside is clamped.</param>
/// <param name="Tick">Where the mark sits, 0 to 1, or null for no mark.</param>
public sealed record GaugeReading(double Fraction, double? Tick);

/// <summary>One column of a gauge: whether it is filled, whether the mark is on it, and its colour.</summary>
public sealed record GaugeCell(bool Filled, bool Tick, Color Colour);

/// <summary>
/// The side panel's gauge: one cell per column, the filled part along a gradient from the theme's calm colour through
/// caution to danger, the rest a quiet track, and a mark — where auto-compress starts — drawn on whichever it falls on.
///
/// <para>THE GRADIENT RUNS ALONG THE BAR, NOT WITH THE FILL: a cell's colour is where it sits, so a half-full gauge is calm
/// and only a nearly full one reaches the danger end — the colour of the last filled cell says how close it is.</para>
///
/// <para>FROM THE THEME, READ WHEN DRAWN: the colours are the theme's roles, so redrawing after a theme change is all a
/// recolour takes. No window here, so a test reads the cells.</para>
/// </summary>
public static class PanelGauge
{
    /// <summary>The empty end's colour.</summary>
    public static Color Calm => ColorScheme.RoleRgb(ColorScheme.Affirmative, new Color(0x4c, 0xaf, 0x50));

    /// <summary>The middle of the gradient.</summary>
    public static Color Caution => ColorScheme.RoleRgb(ColorScheme.Caution, new Color(0xff, 0xc1, 0x07));

    /// <summary>The full end's colour.</summary>
    public static Color Danger => ColorScheme.RoleRgb(ColorScheme.Destructive, new Color(0xf4, 0x43, 0x36));

    /// <summary>The unfilled part: the panel's surface carried a little toward muted text, visible without competing.</summary>
    public static Color Track => PaletteColors.Mix(ColorScheme.PanelSurface, ColorScheme.MutedRgb, 0.3);

    /// <summary>The cells of a gauge <paramref name="width"/> columns wide.</summary>
    public static List<GaugeCell> Cells(GaugeReading reading, int width)
    {
        var filled = (int)Math.Round(Math.Clamp(reading.Fraction, 0, 1) * width);
        int? tick = reading.Tick is { } t ? Math.Clamp((int)Math.Round(Math.Clamp(t, 0, 1) * width), 0, width - 1) : null;
        var gradient = ColorGradient.FromColors(Calm, Caution, Danger);
        var cells = new List<GaugeCell>(width);
        for (var i = 0; i < width; i++)
        {
            var on = i < filled;
            cells.Add(new GaugeCell(on, i == tick, on ? gradient.Interpolate(width == 1 ? 1 : (double)i / (width - 1)) : Track));
        }
        return cells;
    }

    /// <summary>The gauge as markup: a solid bar of background colour, the mark a thin line in the panel's text colour.</summary>
    public static string Markup(GaugeReading reading, int width)
    {
        var mark = ColorScheme.RoleRgb(ColorScheme.Structure, new Color(0xe0, 0xe0, 0xe0)).ToMarkup();
        var sb = new System.Text.StringBuilder();
        foreach (var cell in Cells(reading, width))
            sb.Append(cell.Tick ? $"[{mark} on {cell.Colour.ToMarkup()}]│[/]" : $"[on {cell.Colour.ToMarkup()}] [/]");
        return sb.ToString();
    }
}
