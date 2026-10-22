using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The Plugins rail: a mark says each installed plugin's state before its name.</summary>
public class PluginRailTextTests
{
    [Theory]
    [InlineData(PluginRowSection.Installed, true, "●")]
    [InlineData(PluginRowSection.Installed, false, "○")]
    [InlineData(PluginRowSection.Updates, false, "↑")]
    [InlineData(PluginRowSection.NeedsAttention, false, "!")]
    public void EachState_HasItsMark(PluginRowSection section, bool active, string glyph) =>
        Assert.Contains(glyph, PluginRailText.Mark(section, active));

    [Fact]
    public void AnAvailablePlugin_HasNoMark_ButKeepsTheColumn() =>
        Assert.Equal(" ", PluginRailText.Mark(PluginRowSection.Available, false));

    [Fact]
    public void AShortState_StaysOnTheNamesLine()
    {
        var text = PluginRailText.Row(new PluginRow("calc", PluginRowSection.Installed, "not active"), active: false, cells: 40);
        Assert.DoesNotContain("\n", text);
        Assert.Contains("calc", text);
        Assert.Contains("not active", text);
    }

    [Fact]
    public void ALongState_GoesUnderTheName()
    {
        var text = PluginRailText.Row(new PluginRow("calculator", PluginRowSection.NeedsAttention, "rename Acme/x to acme/x to reach it"), false, 24);
        Assert.Contains("\n", text);
    }

    [Fact]
    public void AHeading_CarriesItsCount() =>
        Assert.Contains("INSTALLED · 2", PluginRailText.Heading("INSTALLED", 2));
}
