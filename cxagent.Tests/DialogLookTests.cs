using CxAgent.UI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Themes;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The shared dialog chrome: sizes that fit the terminal, one footer layout, one rule colour.</summary>
[Collection("palette")]
public class DialogLookTests
{
    [Theory]
    [InlineData(DialogSize.S, 200, 60)]
    [InlineData(DialogSize.M, 200, 86)]
    [InlineData(DialogSize.L, 200, 150)]
    [InlineData(DialogSize.L, 120, 108)]
    public void Width_IsTheSizesOwn_OnAWideTerminal(DialogSize size, int terminal, int expected) =>
        Assert.Equal(expected, DialogLook.Width(size, terminal));

    [Theory]
    [InlineData(DialogSize.S)]
    [InlineData(DialogSize.M)]
    [InlineData(DialogSize.L)]
    public void Width_NeverExceedsTheTerminal(DialogSize size)
    {
        foreach (var terminal in new[] { 40, 60, 80, 90 })
            Assert.True(DialogLook.Width(size, terminal) <= terminal - 4, $"{size} at {terminal}");
    }

    [Theory]
    [InlineData(30, 27)]
    [InlineData(60, 40)]
    public void LargeHeight_Is90Percent_AtMost40(int terminal, int expected) =>
        Assert.Equal(expected, DialogLook.LargeHeight(terminal));

    [Fact]
    public void Footer_PutsTheHintsFirst_AndTheButtonsInTheOrderGiven()
    {
        var a = Controls.Button(" Revert ").Build();
        var b = Controls.Button(" Save ").Build();
        var footer = DialogLook.Footer("Esc closes", a, b);
        Assert.Equal([a, b], footer.Buttons.Items);
        Assert.Equal(SharpConsoleUI.Controls.StickyPosition.Bottom, footer.Row.StickyPosition);
        Assert.Equal(SharpConsoleUI.Controls.StickyPosition.Bottom, footer.Rule.StickyPosition);
        Assert.Equal(DialogLook.Palette.Rule, footer.Rule.Color);
    }

    [Fact]
    public void AFooterWithNoButtons_IsTheHintAlone()
    {
        var footer = DialogLook.Footer("Enter chooses · Esc cancels");
        Assert.Empty(footer.Buttons.Items);
    }

    [Fact]
    public void Main_TogglesThePrimaryRole()
    {
        var save = Controls.Button(" Save ").Build();
        Assert.Equal(ColorRole.Primary, DialogLook.MainButton(save).ColorRole);
        Assert.Equal(ColorRole.Default, DialogLook.MainButton(save, main: false).ColorRole);
    }

    [Fact]
    public void ALabelledRule_CarriesItsLabel_InTheRuleColour()
    {
        var rule = DialogLook.Rule("Format");
        // The label is wrapped in the muted colour's markup; the rule renders markup in its title.
        Assert.Contains("Format", rule.Title);
        Assert.Equal(DialogLook.Palette.Rule, rule.Color);
    }
}
