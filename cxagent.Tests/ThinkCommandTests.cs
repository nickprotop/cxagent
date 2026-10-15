using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>/think: what it shows, what it sets, and what it refuses.</summary>
public class ThinkCommandTests
{
    private static readonly ThinkingOptions Opus = new(["low", "medium", "high", "xhigh", "max"]);

    [Fact]
    public void Bare_ListsTheModelsLevels_MarkingTheCurrent()
    {
        var r = ThinkCommand.Decide("", Opus, "medium");
        Assert.Null(r.SetTo);
        Assert.False(r.Clear);
        Assert.Contains("▸ medium", r.Reply.Text);
        Assert.Contains("xhigh", r.Reply.Text);
    }

    [Fact]
    public void Bare_AtTheDefault_MarksDefault() =>
        Assert.Contains("▸ default", ThinkCommand.Decide("", Opus, null).Reply.Text);

    [Fact]
    public void ALevel_SetsIt() => Assert.Equal("high", ThinkCommand.Decide("high", Opus, null).SetTo);

    [Fact]
    public void Save_IsRecognised_InEitherPosition()
    {
        Assert.True(ThinkCommand.Decide("low --save", Opus, null).Save);
        Assert.True(ThinkCommand.Decide("--save low", Opus, null).Save);
        Assert.Equal("low", ThinkCommand.Decide("--save low", Opus, null).SetTo);
    }

    [Fact]
    public void Default_ClearsTheLevel()
    {
        var r = ThinkCommand.Decide("default", Opus, "high");
        Assert.True(r.Clear);
        Assert.Null(r.SetTo);
    }

    [Fact]
    public void AnUnofferedLevel_IsRefused_NamingTheLevels()
    {
        var r = ThinkCommand.Decide("off", Opus, null);
        Assert.Null(r.SetTo);
        Assert.False(r.Clear);
        Assert.Contains("low, medium, high, xhigh, max", r.Reply.Text);
    }

    [Fact]
    public void NoLevels_SaysWhy() =>
        Assert.Contains("adaptive",
            ThinkCommand.Decide("", ThinkingOptions.None("this model does not accept adaptive thinking"), null).Reply.Text);
}
