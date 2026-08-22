using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
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
        var result = ModeCommand.Decide(new ModeQuery("", new WorkingMode(AgentMode.FanOut), true, "/repo"));

        Assert.Null(result.NewMode);
        Assert.Contains("fan-out", result.Reply.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fan-out")]
    [InlineData("fanout")]     // the spelling people actually type
    [InlineData("FAN-OUT")]
    [InlineData(" fan-out ")]
    public void SwitchingToFanOut_IsAccepted(string argument)
    {
        var result = ModeCommand.Decide(new ModeQuery(argument, new WorkingMode(AgentMode.Single), true, "/repo"));

        Assert.Equal(AgentMode.FanOut, result.NewMode);
    }

    [Fact]
    public void SwitchingToSingle_IsAccepted()
    {
        var result = ModeCommand.Decide(new ModeQuery("single", new WorkingMode(AgentMode.FanOut), true, "/repo"));

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
            ModeCommand.Decide(new ModeQuery("fan-out", new WorkingMode(AgentMode.Single), true, "/repo")).Reply.Text,
            StringComparison.Ordinal);

        Assert.Contains("works alone",
            ModeCommand.Decide(new ModeQuery("single", new WorkingMode(AgentMode.FanOut), true, "/repo")).Reply.Text,
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
        var result = ModeCommand.Decide(new ModeQuery("sideways", new WorkingMode(AgentMode.Single), true, "/repo"));

        Assert.Null(result.NewMode);
        Assert.Contains("single", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("fan-out", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>Reporting "switched" when nothing switched is a small lie the user will act on.</summary>
    [Fact]
    public void SettingTheModeItIsAlreadyIn_ChangesNothing_AndSaysSo()
    {
        var result = ModeCommand.Decide(new ModeQuery("single", new WorkingMode(AgentMode.Single), true, "/repo"));

        Assert.Null(result.NewMode);
        Assert.Contains("already", result.Reply.Text, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Reporting still works mid-turn: it reads nothing and changes nothing, and refusing it would
    /// mean a user cannot check what mode they are in while the thing they are watching runs.
    /// </summary>
    [Fact]
    public void ReportingMidTurn_IsAllowed()
    {
        var result = ModeCommand.Decide(new ModeQuery("", new WorkingMode(AgentMode.Single), true, "/repo"));

        Assert.Null(result.NewMode);
        Assert.Contains("single", result.Reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Escape", result.Reply.Text, StringComparison.Ordinal);
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

        Assert.Equal(expected, ModeCommand.Decide(new ModeQuery(argument, new WorkingMode(from), true, "/repo")).NewMode);
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
            ModeCommand.Decide(new ModeQuery("fan-out", new WorkingMode(AgentMode.Single), true, "/repo")).NewMode);

    /// <summary>
    /// AN AXIS THAT IS NOT SETTABLE YET SAYS SO. Without this, `/mode files read-only` reports
    /// "unknown mode 'files read-only'" — which reads as though the VALUE were misspelled and sends
    /// the user hunting for the right spelling of a value that was never the problem.
    /// </summary>
    /// <remarks>
    /// "files read-only" LEFT THIS THEORY when the edits axis became real. It is now a known axis
    /// with an unknown VALUE, which is a different message — and the right one, since the axis is no
    /// longer the thing that is wrong. Its behaviour is pinned by
    /// <see cref="ModeEdits_WithAnUnknownValue_SaysWhatIsValid"/>.
    /// </remarks>
    [Theory]
    [InlineData("work plan")]
    [InlineData("task something")]
    public void AnAxisThatDoesNotExistYet_SaysWhichAxesDo(string argument)
    {
        var result = ModeCommand.Decide(new ModeQuery(argument, new WorkingMode(AgentMode.Single), true, "/repo"));

        Assert.Null(result.NewMode);
        Assert.Contains("not settable yet", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("agent", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BARE /mode IS A STATUS BLOCK, not a sentence — because "what mode am I in" is about to have
    /// more than one answer, and a line that assumed one axis would need rewriting rather than
    /// extending.
    /// </summary>
    [Fact]
    public void ABareModeReportsEveryAxis()
    {
        var result = ModeCommand.Decide(new ModeQuery("", new WorkingMode(AgentMode.FanOut), true, "/repo"));

        Assert.Null(result.NewMode);                       // reporting never changes anything
        Assert.Contains("agent", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("fan-out", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("spawn", result.Reply.Text, StringComparison.Ordinal);   // what it MEANS
        Assert.Contains("\n", result.Reply.Text, StringComparison.Ordinal);      // a block, not a line
    }

    // ---- the edits axis -------------------------------------------------------------------------

    // NO turnRunning. Whether a change can happen now is the session's to decide and announce —
    // this command only decides WHAT was asked for.
    private static ModeQuery Query(string argument, WorkingMode current, bool trusted = true) =>
        new(argument, current, trusted, "/repo");

    /// <summary>Bare /mode lists BOTH axes. The command was already axis-shaped and its comment said
    /// the axis is what makes room for the next one — so this is a row, not a rewrite.</summary>
    [Fact]
    public void BareMode_ListsBothAxes()
    {
        var result = ModeCommand.Decide(Query("", WorkingMode.Default));

        Assert.Contains("agent", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("edits", result.Reply.Text, StringComparison.Ordinal);
        Assert.Contains("accept-edits", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE EFFECT LINE REPORTS WHAT IS IN FORCE, not what the mode name promises. An accept-edits
    /// session on an untrusted folder asks for everything, and this is where a user meets the trust
    /// rule — at the moment it is affecting them rather than in documentation.
    /// </summary>
    [Fact]
    public void OnAnUntrustedFolder_AcceptEdits_SaysItIsAskingAnyway()
    {
        var result = ModeCommand.Decide(Query("", WorkingMode.Default, trusted: false));

        Assert.Contains("not trusted", result.Reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModeEdits_SetsTheAxis_AndLeavesTheAgentAxisAlone()
    {
        var result = ModeCommand.Decide(Query("edits always-ask",
            new WorkingMode(AgentMode.FanOut, EditMode.AcceptEdits)));

        Assert.Equal(new WorkingMode(AgentMode.FanOut, EditMode.AlwaysAsk), result.NewMode);
    }

    /// <summary>The other axis is carried, not reset — `with` is why WorkingMode is a record.</summary>
    [Fact]
    public void ModeAgent_LeavesTheEditsAxisAlone()
    {
        var result = ModeCommand.Decide(Query("agent fan-out",
            new WorkingMode(AgentMode.Single, EditMode.AlwaysAsk)));

        Assert.Equal(new WorkingMode(AgentMode.FanOut, EditMode.AlwaysAsk), result.NewMode);
    }

    [Theory]
    [InlineData("edits nonsense")]
    [InlineData("edit sideways")]
    public void ModeEdits_WithAnUnknownValue_SaysWhatIsValid(string argument)
    {
        var result = ModeCommand.Decide(Query(argument, WorkingMode.Default));

        Assert.Null(result.NewMode);
        Assert.Contains("always-ask", result.Reply.Text, StringComparison.Ordinal);
    }

    /// <summary>AUTO IS NOT SELECTABLE while no classifier is configured — a mode that claims
    /// background review while nothing reviews is worse than no mode.</summary>
    [Fact]
    public void ModeEdits_Auto_IsRejectedWhileNoClassifierIsConfigured()
    {
        var result = ModeCommand.Decide(Query("edits auto", WorkingMode.Default));

        Assert.Null(result.NewMode);

        // The reply echoes the rejected value ("unknown edit mode 'auto'"), so what matters is that
        // auto is absent from the VALID list it offers — the user must not be pointed at a mode they
        // cannot reach.
        var valid = result.Reply.Text[result.Reply.Text.IndexOf("Valid:", StringComparison.Ordinal)..];
        Assert.DoesNotContain("auto", valid, StringComparison.Ordinal);
    }

    /// <summary>...and IS selectable once one is. The config key is the whole gate.</summary>
    [Fact]
    public void ModeEdits_Auto_IsAcceptedWhenAClassifierIsConfigured()
    {
        var result = ModeCommand.Decide(new ModeQuery("edits auto", WorkingMode.Default, true, "/repo", ClassifierConfigured: true));

        Assert.Equal(EditMode.Auto, result.NewMode!.Value.Edits);
    }

    /// <summary>The listing offers auto only when it is reachable, so a user is never told about a
    /// mode they cannot select.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BareMode_OffersAuto_OnlyWhenAClassifierIsConfigured(bool configured, bool expected)
    {
        var result = ModeCommand.Decide(new ModeQuery("", WorkingMode.Default, true, "/repo",
            ClassifierConfigured: configured));

        Assert.Equal(expected, result.Reply.Text.Contains("auto", StringComparison.Ordinal));
    }

    /// <summary>A no-op says so and changes nothing, on this axis as on the other. The value has to
    /// be whatever the default actually is, so this reads it rather than naming a mode.</summary>
    [Fact]
    public void ModeEdits_ToTheCurrentValue_IsANoOp()
    {
        var current = EditModes.Name(WorkingMode.Default.Edits);
        var result = ModeCommand.Decide(Query($"edits {current}", WorkingMode.Default));

        Assert.Null(result.NewMode);
        Assert.Contains("already", result.Reply.Text, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>An axis we do not know is not a value — `/mode work plan` should say that work is not
    /// an axis yet, rather than blaming the spelling of "plan".</summary>
    [Fact]
    public void AnUnbuiltAxis_SaysSo_RatherThanBlamingTheValue()
    {
        var result = ModeCommand.Decide(Query("work plan", WorkingMode.Default));

        Assert.Null(result.NewMode);
        Assert.Contains("not settable yet", result.Reply.Text, StringComparison.Ordinal);
    }
}
