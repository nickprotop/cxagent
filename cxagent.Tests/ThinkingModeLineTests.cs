using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The thinking level on the composer's mode line.</summary>
public class ThinkingModeLineTests
{
    [Fact]
    public void ModeLine_ShowsTheLevel_AfterTheModel()
    {
        var line = MainWindow.ModeLineTextForTest(WorkingMode.Default, "anthropic:opus",
            thinking: "◆ medium (F8)");
        Assert.True(line.IndexOf("anthropic:opus", StringComparison.Ordinal)
                    < line.IndexOf("◆ medium (F8)", StringComparison.Ordinal));
    }

    [Fact]
    public void ModeLine_WithoutALevel_IsUnchanged() =>
        Assert.Equal(
            MainWindow.ModeLineTextForTest(WorkingMode.Default, "local:qwen"),
            MainWindow.ModeLineTextForTest(WorkingMode.Default, "local:qwen", thinking: null));

    [Fact]
    public void Indicator_ALevel() =>
        Assert.Equal("◆ high (F8)", MainWindow.ThinkingIndicator("high", null, new(["low", "high"])));

    [Fact]
    public void Indicator_Off_IsHollow() =>
        Assert.Equal("◇ off (F8)", MainWindow.ThinkingIndicator("off", null, new(["off", "low"])));

    [Fact]
    public void Indicator_Pending_ShowsBoth() =>
        Assert.Equal("◆ medium → high (next message) (F8)",
            MainWindow.ThinkingIndicator("medium", "high", new(["medium", "high"])));

    [Fact]
    public void Indicator_ModelDefault_SaysDefault() =>
        Assert.Equal("◆ default (F8)", MainWindow.ThinkingIndicator(null, null, new(["low", "high"])));

    [Fact]
    public void Indicator_NoLevels_IsAbsent() =>
        Assert.Null(MainWindow.ThinkingIndicator(null, null, ThinkingOptions.None("x")));

    [Fact]
    public void Indicator_NotYetKnown_IsAbsent() =>
        Assert.Null(MainWindow.ThinkingIndicator(null, null, null));
}
