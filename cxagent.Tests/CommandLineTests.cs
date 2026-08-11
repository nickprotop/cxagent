using CxAgent.Core.Agent;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Reading the command line. A pure function over <c>string[]</c>, which is the point of extracting
/// it: <c>AppBootstrap.Run</c> builds a console driver and takes over the terminal, so nothing about
/// argument handling could be tested while it lived there.
/// </summary>
public class CommandLineTests
{
    /// <summary>No arguments is a plain single-agent session — delegation is a choice a user makes,
    /// not one they discover.</summary>
    [Fact]
    public void NoArguments_IsSingleMode_WithNoMock()
    {
        var options = CommandLine.Parse([]);

        Assert.Equal(AgentMode.Single, options.Mode);
        Assert.False(options.UseMock);
        Assert.Null(options.Error);
    }

    [Theory]
    [InlineData("fan-out")]
    [InlineData("fanout")]
    public void ModeFanOut_StartsInFanOut(string value)
    {
        Assert.Equal(AgentMode.FanOut, CommandLine.Parse(["--mode", value]).Mode);
    }

    /// <summary>`--mode=x` is what scripts and shell completions generate; rejecting it would be a
    /// puzzle rather than a lesson.</summary>
    [Fact]
    public void TheEqualsForm_IsAccepted()
    {
        Assert.Equal(AgentMode.FanOut, CommandLine.Parse(["--mode=fan-out"]).Mode);
    }

    /// <summary>
    /// --mock IS A PROVIDER CHOICE, NOT A MODE, and the two are orthogonal on purpose: a mock session
    /// is exactly where someone drives the UI, and it must be able to do that in either mode.
    /// </summary>
    [Fact]
    public void MockAndMode_AreIndependent()
    {
        var options = CommandLine.Parse(["--mock", "--mode", "fan-out"]);

        Assert.True(options.UseMock);
        Assert.Equal(AgentMode.FanOut, options.Mode);
        Assert.Null(options.Error);
    }

    /// <summary>
    /// AN UNKNOWN MODE STOPS THE APP rather than defaulting. A user who typed a near-miss and
    /// silently got single mode concludes sub-agents are broken — the error must say what to type.
    /// </summary>
    [Fact]
    public void AnUnknownMode_IsAnError_ThatNamesTheValidValues()
    {
        var options = CommandLine.Parse(["--mode", "sideways"]);

        Assert.NotNull(options.Error);
        Assert.Contains("single", options.Error!, StringComparison.Ordinal);
        Assert.Contains("fan-out", options.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ModeWithNoValue_IsAnError()
    {
        Assert.NotNull(CommandLine.Parse(["--mode"]).Error);
    }

    /// <summary>An argument nobody understood is a typo, and starting anyway runs a session the user
    /// did not ask for.</summary>
    [Fact]
    public void AnUnknownArgument_IsAnError()
    {
        var options = CommandLine.Parse(["--fanout"]);

        Assert.NotNull(options.Error);
        Assert.Contains("--fanout", options.Error!, StringComparison.Ordinal);
    }

    /// <summary>The existing flag keeps working exactly as it did — this change adds an argument, it
    /// does not reshape the one that was there.</summary>
    [Fact]
    public void MockAlone_StillWorks()
    {
        var options = CommandLine.Parse(["--mock"]);

        Assert.True(options.UseMock);
        Assert.Equal(AgentMode.Single, options.Mode);
        Assert.Null(options.Error);
    }
}
