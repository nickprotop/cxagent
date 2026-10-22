using SharpConsoleUI;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>
/// Every colour a dialog uses, derived from one theme.
///
/// <para>NEARLY FLUSH WITH THE CHAT. The surface is the chat ground lifted a little in a dark theme and
/// left as it is in a light one: a dialog reads as the same material as the conversation it sits over,
/// and its border, not a contrasting slab, is what sets it apart. Everything else is a step from that
/// surface toward the accent, so a theme change moves the whole dialog together.</para>
/// </summary>
/// <param name="Surface">The dialog's background.</param>
/// <param name="Rule">Every separator inside a dialog.</param>
/// <param name="ActiveBorder">The border of the dialog that has focus.</param>
/// <param name="InactiveBorder">The border of a dialog with another on top of it.</param>
/// <param name="CautionBorder">The border of a dialog whose main answer throws something away.</param>
/// <param name="Title">The active title.</param>
/// <param name="Muted">Hints, help lines and the inactive title.</param>
/// <param name="Foreground">Normal text.</param>
/// <param name="Input">A text input's ground, so an empty field is visibly a field.</param>
/// <param name="Selection">The selected row of a list, and a focused input.</param>
public sealed record DialogPalette(
    Color Surface, Color Rule, Color ActiveBorder, Color InactiveBorder, Color CautionBorder,
    Color Title, Color Muted, Color Foreground, Color Input, Color Selection)
{
    /// <summary>The palette for <paramref name="theme"/>.</summary>
    public static DialogPalette For(ITheme theme)
    {
        var ground = theme.WindowBackgroundColor;
        var foreground = theme.WindowForegroundColor;
        var accent = ColorRoleResolver.Resolve(ColorRole.Primary, theme).Text;
        var caution = ColorRoleResolver.Resolve(ColorRole.Warning, theme).Text;

        var surface = PaletteColors.Mix(Lift(ground, 0.025), accent, 0.03);
        return new DialogPalette(
            Surface: surface,
            Rule: PaletteColors.Mix(surface, accent, 0.22),
            ActiveBorder: PaletteColors.Mix(surface, accent, 0.60),
            InactiveBorder: PaletteColors.Mix(surface, accent, 0.20),
            CautionBorder: PaletteColors.Mix(surface, caution, 0.60),
            Title: PaletteColors.EnsureContrast(accent, surface),
            Muted: PaletteColors.Mix(foreground, ground, 0.55),
            Foreground: foreground,
            Input: Raised(surface, 0.06),
            Selection: PaletteColors.Mix(Raised(surface, 0.10), accent, 0.10));
    }

    // LIGHTER IN A DARK THEME ONLY. A light theme's dialog lifted toward white would vanish into it, and
    // shaded it reads as a grey slab: flush is what keeps it the chat's own material.
    private static Color Lift(Color c, double amount) => c.IsDark() ? PaletteColors.Tint(c, amount) : c;

    // A step away from the surface on its own side: lighter on dark, darker on light.
    private static Color Raised(Color c, double amount) =>
        c.IsDark() ? PaletteColors.Tint(c, amount) : PaletteColors.Shade(c, amount);
}
