using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The startup wordmark. Small surface, but two things about it are easy to break silently: the
/// width fallback (only visible on a narrow terminal, which is not where anyone develops) and the
/// promise NOT to repeat what the composer and mode line already say.
/// </summary>
public class BannerTests
{
    [Fact]
    public void AWideTerminalGetsTheBlockWordmark()
    {
        var art = Banner.Render(200, "single agent · mock");

        // Per-CHARACTER markup wraps every glyph in its own colour tag, so the block form's cells
        // never appear as a contiguous "██" run — assert on the glyph itself, which the light
        // wordmark does not contain at all.
        Assert.Contains("█", art, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ANarrowTerminalFallsBackToTheLightWordmark()
    {
        // The block form is 60 columns; below the threshold it wraps into nonsense. Nobody develops
        // at 60 columns, so this is exactly the kind of thing that ships broken.
        var art = Banner.Render(Banner.MinBlockWidth - 1, "single agent · mock");

        Assert.DoesNotContain("█", art, System.StringComparison.Ordinal);
        Assert.Contains("╭", art, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordmarkFadesFromTheAccentToTheHeading()
    {
        // Horizontal, from the app's OWN two colours — a decorative pair invented for the banner
        // would be one more thing to keep in step with the palette.
        var art = Banner.Render(200, "single agent · mock");

        var accent = ColorScheme.AccentRgb;
        var heading = ColorScheme.Heading;

        Assert.Contains($"#{accent.R:x2}{accent.G:x2}{accent.B:x2}", art, System.StringComparison.Ordinal);
        Assert.Contains($"#{heading.R:x2}{heading.G:x2}{heading.B:x2}", art, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheSubtitleIsCarried()
    {
        Assert.Contains("single agent · mock", Banner.Render(200, "single agent · mock"),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheBannerDoesNotRepeatTheKeybindingHint()
    {
        // It used to: "Type a goal and press Enter (Shift+Enter for a new line)". The composer's own
        // PlaceholderText says the same thing in the control the user is about to type into, so a
        // second copy here is a third place to keep in sync — and a banner that explains keybindings
        // is not a banner.
        var art = Banner.Render(200, "single agent · mock");

        Assert.DoesNotContain("Shift+Enter", art, System.StringComparison.Ordinal);
        Assert.DoesNotContain("press Enter", art, System.StringComparison.Ordinal);
    }
}
