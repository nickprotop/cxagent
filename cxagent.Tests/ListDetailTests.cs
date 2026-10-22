using CxAgent.UI;
using SharpConsoleUI.Controls;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The list and detail pieces Configuration and Plugins share.</summary>
public class ListDetailTests
{
    private static ListControl Rail(params string[] texts)
    {
        var rail = new ListControl();
        foreach (var t in texts) rail.AddItem(new ListItem(t) { Tag = t });
        return rail;
    }

    [Fact]
    public void TheSelectedRow_CarriesTheBar_OnEveryLine_AndTheOthersASpace()
    {
        var rail = Rail("one\nsub", "two");
        rail.SelectedIndex = 0;
        ListDetail.Mark(rail);
        var lines = rail.Items[0].Text.Split('\n');
        Assert.All(lines, l => Assert.Contains("▌", l));
        Assert.StartsWith(" ", rail.Items[1].Text);
        Assert.DoesNotContain("▌", rail.Items[1].Text);
    }

    [Fact]
    public void MovingTheSelection_MovesTheBar_WithoutStackingPrefixes()
    {
        var rail = Rail("one", "two");
        rail.SelectedIndex = 0; ListDetail.Mark(rail);
        rail.SelectedIndex = 1; ListDetail.Mark(rail);
        ListDetail.Mark(rail);
        Assert.Equal(" one", rail.Items[0].Text);
        Assert.Contains("▌", rail.Items[1].Text);
        Assert.EndsWith("two", rail.Items[1].Text);
        Assert.Equal(1, rail.Items[1].Text.Count(c => c == '▌'));
    }

    [Fact]
    public void ACallerRewritingARow_IsTheNewBase()
    {
        var rail = Rail("one");
        rail.SelectedIndex = 0; ListDetail.Mark(rail);
        rail.Items[0].Text = "renamed";
        ListDetail.Mark(rail);
        Assert.EndsWith("renamed", rail.Items[0].Text);
        Assert.DoesNotContain("one", rail.Items[0].Text);
    }

    [Fact]
    public void AHeadingRow_WithNoTag_NeverTakesTheBar()
    {
        var rail = new ListControl();
        rail.AddItem(new ListItem("INSTALLED · 1") { IsEnabled = false });
        rail.AddItem(new ListItem("calc") { Tag = "calc" });
        rail.SelectedIndex = 1; ListDetail.Mark(rail);
        Assert.DoesNotContain("▌", rail.Items[0].Text);
    }

    [Theory]
    [InlineData("/home/me/.config/cxagent/config.json", "/home/me", 80, "~/.config/cxagent/config.json")]
    [InlineData("/home/me/.config/cxagent/config.json", "/home/me", 12, "…config.json")]
    [InlineData("/etc/cxagent/config.json", "/home/me", 80, "/etc/cxagent/config.json")]
    [InlineData("/home/meow/config.json", "/home/me", 80, "/home/meow/config.json")]
    public void ElidePath_ShortensHome_AndCutsFromTheLeft(string path, string home, int width, string expected) =>
        Assert.Equal(expected, ListDetail.ElidePath(path, home, width));

    [Fact]
    public void ElidePath_NeverExceedsItsWidth()
    {
        for (var w = 1; w < 40; w++)
            Assert.True(ListDetail.ElidePath("/home/me/a/very/long/path/to/config.json", "/home/me", w).Length <= w);
    }
}
