using CxAgent.UI;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Themes;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Thinking takes its colour from the theme it is shown in.
///
/// <para>It used to be the Caution role, which five of the eight themes resolve to the same
/// #ffc107 — one loud yellow whatever the theme.</para>
/// </summary>
public class ThinkingColourTests
{
    private static IReadOnlyList<string> ThemeNames()
    {
        var probe = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24));
        CxAgentTheme.Install(probe, null);
        return probe.ThemeRegistryService.GetAvailableThemes().Select(t => t.Name).ToList();
    }

    public static TheoryData<string> Themes() => new(ThemeNames());

    private static ITheme Theme(string name)
    {
        var system = new ConsoleWindowSystem(new HeadlessConsoleDriver(80, 24));
        CxAgentTheme.Install(system, name);
        return system.ThemeStateService.CurrentTheme;
    }

    private static Color ThinkingIn(ITheme theme) =>
        ColorScheme.ThinkingFor(theme.WindowForegroundColor,
            ColorRoleResolver.Resolve(ColorScheme.Accent, theme).Text, theme.WindowBackgroundColor);

    [Theory]
    [MemberData(nameof(Themes))]
    public void ThinkingIsReadable_AndQuieterThanTheAnswer(string name)
    {
        var theme = Theme(name);
        var surface = theme.WindowBackgroundColor;
        var thinking = ThinkingIn(theme);

        static double Gap(Color a, Color b) => Math.Abs(a.Luminance() - b.Luminance());

        Assert.True(Gap(thinking, surface) >= 70, $"{name}: thinking {thinking} on {surface}");
        Assert.True(Gap(thinking, surface) < Gap(theme.WindowForegroundColor, surface),
            $"{name}: thinking {thinking} is as loud as the answer's text {theme.WindowForegroundColor}");
    }

    [Fact]
    public void ThinkingFollowsTheTheme_NotOneColourForAll()
    {
        var colours = ThemeNames().Select(name => ThinkingIn(Theme(name))).ToList();

        Assert.Equal(colours.Count, colours.Distinct().Count());
        Assert.DoesNotContain(new Color(0xff, 0xc1, 0x07), colours);
    }
}
