using CxAgent.Core.Agent;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What <c>/mode</c> decides. Pure over (argument, current mode, is a turn running), which is why it
/// is a type of its own rather than a branch inside AppBootstrap — the same reasoning that separated
/// EscapeRouting and PromptQueue.
/// </summary>
public class ModeCommandTests
{
    /// <summary>Asking what mode you are in must never change it.</summary>
    [Fact]
    public void BareMode_Reports_AndChangesNothing()
    {
        var result = ModeCommand.Decide("", AgentMode.FanOut, turnRunning: false);

        Assert.Null(result.NewMode);
        Assert.Contains("fan-out", result.Reply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fan-out")]
    [InlineData("fanout")]     // the spelling people actually type
    [InlineData("FAN-OUT")]
    [InlineData(" fan-out ")]
    public void SwitchingToFanOut_IsAccepted(string argument)
    {
        var result = ModeCommand.Decide(argument, AgentMode.Single, turnRunning: false);

        Assert.Equal(AgentMode.FanOut, result.NewMode);
    }

    [Fact]
    public void SwitchingToSingle_IsAccepted()
    {
        var result = ModeCommand.Decide("single", AgentMode.FanOut, turnRunning: false);

        Assert.Equal(AgentMode.Single, result.NewMode);
    }

    /// <summary>
    /// THE REPLY SAYS WHAT CHANGED, because a mode is otherwise invisible: the user cannot inspect a
    /// tool list, so "switched to fan-out" alone does not tell them what they now have.
    /// </summary>
    [Fact]
    public void TheReply_SaysWhatTheModeActuallyDoes()
    {
        Assert.Contains("spawn sub-agents",
            ModeCommand.Decide("fan-out", AgentMode.Single, turnRunning: false).Reply,
            StringComparison.Ordinal);

        Assert.Contains("works alone",
            ModeCommand.Decide("single", AgentMode.FanOut, turnRunning: false).Reply,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A BAD VALUE NAMES THE GOOD ONES. "unknown mode" alone leaves someone guessing at a spelling,
    /// and a value that silently defaulted is how a user concludes sub-agents are broken when they
    /// merely mistyped.
    /// </summary>
    [Fact]
    public void AnUnknownMode_IsRefused_AndListsTheValidOnes()
    {
        var result = ModeCommand.Decide("sideways", AgentMode.Single, turnRunning: false);

        Assert.Null(result.NewMode);
        Assert.Contains("single", result.Reply, StringComparison.Ordinal);
        Assert.Contains("fan-out", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>Reporting "switched" when nothing switched is a small lie the user will act on.</summary>
    [Fact]
    public void SettingTheModeItIsAlreadyIn_ChangesNothing_AndSaysSo()
    {
        var result = ModeCommand.Decide("single", AgentMode.Single, turnRunning: false);

        Assert.Null(result.NewMode);
        Assert.Contains("already", result.Reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DECLINED MID-TURN, and not out of caution. The tool list is fixed once a request begins —
    /// deliberately, so a tool cannot appear or vanish between two turns of one request and leave the
    /// model chasing something that is no longer there. Changing mode under a running turn is exactly
    /// that, and it uses the same predicate /compress and Escape share.
    /// </summary>
    [Fact]
    public void MidTurn_IsDeclined_WithTheWayToProceed()
    {
        var result = ModeCommand.Decide("fan-out", AgentMode.Single, turnRunning: true);

        Assert.Null(result.NewMode);
        Assert.Contains("Escape", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reporting still works mid-turn: it reads nothing and changes nothing, and refusing it would
    /// mean a user cannot check what mode they are in while the thing they are watching runs.
    /// </summary>
    [Fact]
    public void ReportingMidTurn_IsAllowed()
    {
        var result = ModeCommand.Decide("", AgentMode.Single, turnRunning: true);

        Assert.Null(result.NewMode);
        Assert.Contains("single", result.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain("Escape", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE AXIS IS NAMED, which is what lets one command grow. File editing and a build/plan mode
    /// are coming; each would otherwise have wanted a command of its own, and there would be no
    /// single place that shows the whole picture.
    /// </summary>
    [Theory]
    [InlineData("agent fan-out", AgentMode.FanOut)]
    [InlineData("agent single", AgentMode.Single)]
    [InlineData("agents fan-out", AgentMode.FanOut)]
    public void TheAxisCanBeNamed(string argument, AgentMode expected)
    {
        var from = expected == AgentMode.FanOut ? AgentMode.Single : AgentMode.FanOut;

        Assert.Equal(expected, ModeCommand.Decide(argument, from, turnRunning: false).NewMode);
    }

    /// <summary>
    /// AND THE BARE VALUE STILL WORKS. Agent is the only axis today, so naming it is ceremony for
    /// the one thing anyone is switching — the day a value collides across axes is the day the
    /// unqualified form for THAT value stops being unambiguous, which is a reason to name the axis
    /// then rather than to demand it now.
    /// </summary>
    [Fact]
    public void TheValueAloneStillWorks() =>
        Assert.Equal(AgentMode.FanOut,
            ModeCommand.Decide("fan-out", AgentMode.Single, turnRunning: false).NewMode);

    /// <summary>
    /// AN AXIS THAT IS NOT SETTABLE YET SAYS SO. Without this, `/mode files read-only` reports
    /// "unknown mode 'files read-only'" — which reads as though the VALUE were misspelled and sends
    /// the user hunting for the right spelling of a value that was never the problem.
    /// </summary>
    [Theory]
    [InlineData("files read-only")]
    [InlineData("work plan")]
    public void AnAxisThatDoesNotExistYet_SaysWhichAxesDo(string argument)
    {
        var result = ModeCommand.Decide(argument, AgentMode.Single, turnRunning: false);

        Assert.Null(result.NewMode);
        Assert.Contains("not settable yet", result.Reply, StringComparison.Ordinal);
        Assert.Contains("agent", result.Reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BARE /mode IS A STATUS BLOCK, not a sentence — because "what mode am I in" is about to have
    /// more than one answer, and a line that assumed one axis would need rewriting rather than
    /// extending.
    /// </summary>
    [Fact]
    public void ABareModeReportsEveryAxis()
    {
        var result = ModeCommand.Decide("", AgentMode.FanOut, turnRunning: false);

        Assert.Null(result.NewMode);                       // reporting never changes anything
        Assert.Contains("agent", result.Reply, StringComparison.Ordinal);
        Assert.Contains("fan-out", result.Reply, StringComparison.Ordinal);
        Assert.Contains("spawn", result.Reply, StringComparison.Ordinal);   // what it MEANS
        Assert.Contains("\n", result.Reply, StringComparison.Ordinal);      // a block, not a line
    }
}
