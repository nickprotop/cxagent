using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Themes;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Every dialog colour, derived from a theme. A dark theme's dialog sits a step above its chat; a light
/// theme's is flush with it. Borders, rules and inputs are steps from that surface, so any theme gets
/// the same relationships.
/// </summary>
public class DialogPaletteTests
{
    private static ITheme Dark() => Theme.From(new ModernGrayTheme()).WithName("t-dark")
        .With(t => { t.WindowBackgroundColor = new Color(0x0d, 0x0d, 0x0d); t.PrimaryColor = new Color(0xe8, 0x9e, 0x64); }).Build();

    private static ITheme Light() => Theme.From(new ModernGrayTheme()).WithName("t-light")
        .With(t => { t.WindowBackgroundColor = new Color(0xf4, 0xf4, 0xf2); t.WindowForegroundColor = new Color(0x20, 0x20, 0x20); t.PrimaryColor = new Color(0x1f, 0x5f, 0xd0); }).Build();

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    private static double Distance(Color a, Color b) => Math.Abs(Luma(a) - Luma(b));

    [Fact]
    public void OnADarkTheme_TheSurfaceIsJustAboveTheChat()
    {
        var bg = Dark().WindowBackgroundColor;
        var p = DialogPalette.For(Dark());
        Assert.True(Luma(p.Surface) > Luma(bg), "surface lighter than the chat");
        // E+ sits about 11 luma points above the chat; the raised panels it was chosen over sit about 30.
        Assert.True(Distance(p.Surface, bg) < 16, "nearly flush: E+, not a raised panel");
    }

    [Fact]
    public void OnALightTheme_TheSurfaceStaysFlushWithTheChat()
    {
        var bg = Light().WindowBackgroundColor;
        Assert.True(Distance(DialogPalette.For(Light()).Surface, bg) < 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheActiveBorder_StandsFurtherFromTheSurfaceThanTheInactiveOne(bool dark)
    {
        var p = DialogPalette.For(dark ? Dark() : Light());
        Assert.True(Distance(p.ActiveBorder, p.Surface) > Distance(p.InactiveBorder, p.Surface));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRule_SitsBetweenTheSurfaceAndTheAccent(bool dark)
    {
        var theme = dark ? Dark() : Light();
        var p = DialogPalette.For(theme);
        var accent = theme.PrimaryColor!.Value;
        Assert.True(Distance(p.Rule, p.Surface) > 0 && Distance(p.Rule, p.Surface) < Distance(accent, p.Surface));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnBothThemes_InputsAndTheSelectionAreVisibleOnTheSurface(bool dark)
    {
        var p = DialogPalette.For(dark ? Dark() : Light());
        Assert.True(Distance(p.Input, p.Surface) >= 4, "an empty input is visibly a field");
        Assert.True(Distance(p.Selection, p.Surface) > Distance(p.Input, p.Surface), "the selection outranks an input");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTitleReadsAsText(bool dark)
    {
        var p = DialogPalette.For(dark ? Dark() : Light());
        Assert.True(Distance(p.Title, p.Surface) >= 60, $"title {p.Title} on {p.Surface}");
    }

    [Fact]
    public void SwitchingTheme_MovesThePalette()
    {
        ColorScheme.DeriveFrom(Dark());
        var before = DialogLook.Palette.Surface;
        ColorScheme.DeriveFrom(Light());
        Assert.NotEqual(before, DialogLook.Palette.Surface);
        Assert.Equal(DialogPalette.For(Light()), DialogLook.Palette);
    }
}
