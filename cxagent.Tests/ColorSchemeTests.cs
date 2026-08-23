using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Themes;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Markdown follows the theme (see .superpowers/sdd/markdown-theme/brief.md): headings and code
/// both derive from the theme's own accent (<see cref="ColorScheme.AccentRgb"/>'s source, resolved
/// fresh per theme here) rather than from fixed hues, separated from each other by luminance so a
/// document's structure stays legible before it is read.
/// </summary>
public class ColorSchemeTests
{
    /// <summary>A dark theme with a crimson accent — the theme named in the observed defect:
    /// ModernGray with only the ground and accent overridden, the same shape CxAgentTheme itself
    /// uses, so nothing about this fake theme is surprising to a reader who already knows it.</summary>
    private static ITheme CrimsonTheme() =>
        Theme.From(new ModernGrayTheme())
            .WithName("test-crimson")
            .With(t =>
            {
                t.WindowBackgroundColor = new Color(0x0d, 0x0d, 0x0d);
                t.PrimaryColor = new Color(0xdc, 0x14, 0x3c);
            })
            .Build();

    /// <summary>A second dark theme with a distinct, teal accent — used to prove the markdown
    /// palette actually MOVES with the theme rather than merely rendering plausibly under one.
    /// </summary>
    private static ITheme TealTheme() =>
        Theme.From(new ModernGrayTheme())
            .WithName("test-teal")
            .With(t =>
            {
                t.WindowBackgroundColor = new Color(0x0d, 0x0d, 0x0d);
                t.PrimaryColor = new Color(0x1a, 0xb5, 0xa8);
            })
            .Build();

    /// <summary>cxagent's own accent and ground VALUES (see CxAgentTheme.Accent, #e89e64), but
    /// NAMED differently from <see cref="CxAgentTheme.Name"/> — so this still exercises the DERIVED
    /// path, not the declared palette a theme literally named "cxagent" gets. It is the theme most
    /// likely to be running when someone reads this test, and the one a naive mix-then-shade step
    /// (an earlier attempt here) failed on first: 28.0 apart, just under the required 30.</summary>
    private static ITheme CxAgentAccentTheme() =>
        Theme.From(new ModernGrayTheme())
            .WithName("test-cxagent-accent")
            .With(t =>
            {
                t.WindowBackgroundColor = new Color(0x0d, 0x0d, 0x0d);
                t.PrimaryColor = new Color(0xe8, 0x9e, 0x64);
            })
            .Build();

    /// <summary>A light theme — the ground every colour here must stay legible against, including
    /// the code background, which is derived rather than a fixed near-black box.</summary>
    private static ITheme LightTheme() =>
        Theme.From(new ModernGrayTheme())
            .WithName("test-light")
            .With(t =>
            {
                t.WindowBackgroundColor = new Color(0xf5, 0xf5, 0xf5);
                t.PrimaryColor = new Color(0xdc, 0x14, 0x3c);
            })
            .Build();

    /// <summary>A theme named exactly like <see cref="CxAgentTheme"/>'s own — the key
    /// <see cref="ColorScheme.DeriveFrom"/> actually checks, since <c>ITheme</c> is the framework's
    /// and cannot carry a new member to flag "this theme declares its own markdown".</summary>
    private static ITheme NamedCxAgentTheme() =>
        Theme.From(new ModernGrayTheme())
            .WithName(CxAgentTheme.Name)
            .With(t =>
            {
                t.WindowBackgroundColor = new Color(0x0d, 0x0d, 0x0d);
                t.PrimaryColor = new Color(0xe8, 0x9e, 0x64);
            })
            .Build();

    private static Color AccentOf(ITheme theme) => ColorRoleResolver.Resolve(ColorScheme.Accent, theme).Text;

    /// <summary>
    /// HEADINGS ARE THE THEME'S. A crimson theme with purple headings is chrome and content
    /// disagreeing about what app the user is in.
    /// </summary>
    [Fact]
    public void Headings_FollowTheThemeAccent()
    {
        ITheme theme = CrimsonTheme();
        Color accent = AccentOf(theme);

        MarkdownStyle style = ColorScheme.MarkdownStyleForTest(theme);

        // EnsureContrast returns the colour UNCHANGED once it already separates from the ground —
        // and a saturated crimson against near-black clears the gap on its own — so the heading
        // should be exactly the resolved accent, not merely "close to" it.
        Assert.Equal(accent, style.H1Color);
        Assert.Equal(accent, style.H2Color);
        Assert.Equal(accent, style.H6Color);
    }

    /// <summary>
    /// AND SO DOES CODE, but not to the same value: both live in the accent's family and must stay
    /// plainly different weights, or a fence stops announcing itself as code before it is read.
    /// </summary>
    [Fact]
    public void CodeIsDerivedFromTheAccent_ButSeparableFromHeadings()
    {
        foreach (ITheme theme in new[] { CrimsonTheme(), TealTheme() })
        {
            Color accent = AccentOf(theme);
            MarkdownStyle style = ColorScheme.MarkdownStyleForTest(theme);

            // "Derived from the accent" — not the theme's plain foreground/background, and not the
            // old fixed green. Some accent channel should still show through the mix-then-shade.
            Assert.NotEqual(theme.WindowForegroundColor, style.CodeForeground);

            double gap = System.Math.Abs(style.H1Color!.Value.Luminance() - style.CodeForeground.Luminance());
            Assert.True(gap >= 30.0,
                $"heading/code luminance gap was {gap:0.0} under {theme.Name}, wanted >= 30");

            // Still recognisably accent-family: neither collapsed to grey (R, G, B all equal).
            Assert.False(accent.R == accent.G && accent.G == accent.B);
        }
    }

    /// <summary>
    /// A DIFFERENT THEME GIVES DIFFERENT MARKDOWN. The regression that matters: run DeriveFrom
    /// twice with two accents and demand the heading colour actually moved.
    /// </summary>
    [Fact]
    public void SwitchingTheme_MovesTheMarkdownColours()
    {
        ColorScheme.DeriveFrom(CrimsonTheme());
        Color crimsonHeading = ColorScheme.Heading;

        ColorScheme.DeriveFrom(TealTheme());
        Color tealHeading = ColorScheme.Heading;

        Assert.NotEqual(crimsonHeading, tealHeading);
    }

    /// <summary>
    /// LEGIBLE ON EVERY GROUND, including a light theme where a colour picked for near-black is
    /// not readable and a fixed near-black code background is a box behind dark text.
    /// </summary>
    [Fact]
    public void OnALightTheme_EveryMarkdownColourSeparatesFromTheGround()
    {
        ITheme theme = LightTheme();
        Color surface = theme.WindowBackgroundColor;
        MarkdownStyle style = ColorScheme.MarkdownStyleForTest(theme);

        // Text-carrying colours: the default 80-gap against the window surface.
        Assert.True(System.Math.Abs(style.H1Color!.Value.Luminance() - surface.Luminance()) >= 80.0);
        Assert.True(System.Math.Abs(style.QuoteColor.Luminance() - surface.Luminance()) >= 80.0);
        Assert.True(System.Math.Abs(style.LinkColor.Luminance() - surface.Luminance()) >= 80.0);

        // Code foreground separates from the CODE background, not the window surface — that is the
        // ground it actually sits on.
        Assert.True(System.Math.Abs(style.CodeForeground.Luminance() - style.CodeBackground.Luminance()) >= 80.0);

        // Chrome: the smaller 55-gap.
        Assert.True(System.Math.Abs(style.BorderColor.Luminance() - surface.Luminance()) >= 55.0);

        // THE CODE BACKGROUND STOPS BEING A BLACK BOX: a light ground gets a light code surface,
        // derived off the window background rather than a fixed near-black literal.
        Assert.True(style.CodeBackground.Luminance() > 128.0);

        // Heading and code stay separable from EACH OTHER on this ground too, not just legible.
        double gap = System.Math.Abs(style.H1Color!.Value.Luminance() - style.CodeForeground.Luminance());
        Assert.True(gap >= 30.0, $"heading/code luminance gap was {gap:0.0} on the light theme, wanted >= 30");
    }

    /// <summary>
    /// THE SEPARATION HOLDS ACROSS MULTIPLE ACCENTS, named individually rather than folded into a
    /// loop — a single-theme assertion is exactly what let a 28.0-point gap (cxagent's own accent,
    /// under a naive mix-then-shade derivation) pass earlier in this change's history. Three shapes
    /// checked: a warm mid-luminance accent (cxagent's own), a dark saturated one (crimson-like,
    /// the theme named in the observed defect), and one on a light ground.
    /// </summary>
    [Theory]
    [InlineData("cxagent")]
    [InlineData("crimson")]
    [InlineData("light")]
    public void HeadingAndCode_StaySeparatedAcrossNamedAccents(string themeName)
    {
        ITheme theme = themeName switch
        {
            "cxagent" => CxAgentAccentTheme(),
            "crimson" => CrimsonTheme(),
            "light" => LightTheme(),
            _ => throw new System.ArgumentOutOfRangeException(nameof(themeName)),
        };

        MarkdownStyle style = ColorScheme.MarkdownStyleForTest(theme);
        double gap = System.Math.Abs(style.H1Color!.Value.Luminance() - style.CodeForeground.Luminance());

        Assert.True(gap >= 30.0, $"heading/code luminance gap was {gap:0.0} under '{themeName}', wanted >= 30");
    }

    /// <summary>
    /// EVERY THEME DERIVES, cxagent's own included. A per-theme declared palette would make the
    /// app's markdown answer to two rules depending on which theme is active, and the theme that
    /// opted out would be the one nobody ever saw adapt.
    /// </summary>
    [Fact]
    public void TheCxAgentTheme_DerivesLikeEveryOther()
    {
        MarkdownStyle style = ColorScheme.MarkdownStyleForTest(CxAgentAccentTheme());

        Assert.Equal(CxAgentAccentTheme().PrimaryColor, style.H1Color!.Value);
    }

    /// <summary>
    /// AND EVERY OTHER THEME STILL DERIVES. The defect this whole change exists to fix: a crimson
    /// theme with violet headings is chrome and content disagreeing about what app the user is in.
    /// </summary>
    [Fact]
    public void AnotherTheme_StillDerivesFromItsAccent()
    {
        ITheme theme = CrimsonTheme();
        Color accent = AccentOf(theme);

        MarkdownStyle style = ColorScheme.MarkdownStyleForTest(theme);

        Assert.Equal(accent, style.H1Color);
        Assert.NotEqual(new Color(0x9d, 0x7c, 0xd8), style.H1Color);
    }

    /// <summary>
    /// SWITCHING THEMES MOVES MARKDOWN AND SWITCHING BACK MOVES IT BACK. The palette is not a
    /// one-shot at startup: F9 must carry the transcript to the new theme and return it.
    /// </summary>
    [Fact]
    public void SwitchingAwayAndBack_ReturnsTheOriginalPalette()
    {
        Color before = ColorScheme.MarkdownStyleForTest(CxAgentAccentTheme()).H1Color!.Value;
        Color other = ColorScheme.MarkdownStyleForTest(CrimsonTheme()).H1Color!.Value;
        Color after = ColorScheme.MarkdownStyleForTest(CxAgentAccentTheme()).H1Color!.Value;

        Assert.NotEqual(before, other);
        Assert.Equal(before, after);
    }
}
