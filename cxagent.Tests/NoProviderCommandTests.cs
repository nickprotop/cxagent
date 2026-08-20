using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which commands a session can run with NO model configured.
///
/// <para>THE BUG THIS PINS: the composer's submit handler opened with
/// `if (!session.HasAgent || ...) return;`, which swallowed the keystroke before anything looked at
/// what was typed. A session that opened without a working provider could not run /exit, /help,
/// /stats, /sessions — or /model, the one command that FIXES having no provider. The window was
/// unusable except by killing it.</para>
///
/// <para>THE CLASSIFICATION ALREADY EXISTED and the guard was not asking. CommandOutcome's own
/// documentation says every command except NeedsProvider and NeedsTurn "answers from state the app
/// already holds, costing no tokens and no time". These tests hold that claim to its word, so a
/// command added later with the wrong outcome fails here rather than in someone's broken session.</para>
/// </summary>
public class NoProviderCommandTests
{
    /// <summary>What the submit handler decides, expressed as the predicate it uses.</summary>
    private static bool NeedsAModel(string input)
    {
        var outcome = SessionCommands.Match(input)?.Outcome ?? CommandOutcome.NotACommand;
        return outcome is CommandOutcome.NotACommand
            or CommandOutcome.NeedsProvider or CommandOutcome.NeedsTurn;
    }

    [Theory]
    [InlineData("/exit")]        // leaving must never require a model
    [InlineData("/help")]
    [InlineData("/stats")]       // reads the usage archive, not the model
    [InlineData("/sessions")]
    [InlineData("/model")]       // THE ONE THAT FIXES IT — blocking this was the worst of the bug
    [InlineData("/mode")]
    [InlineData("/mcp")]
    [InlineData("/skills")]
    [InlineData("/agents")]
    [InlineData("/diff")]        // git, not the model
    [InlineData("/clear")]
    public void TheseRunWithNoModel(string input)
        => Assert.False(NeedsAModel(input), $"{input} must work with no provider configured");

    [Theory]
    [InlineData("/compress")]    // summarises THROUGH the model, exactly as auto-compression does
    [InlineData("/init")]        // becomes a turn: the agent goes and reads the project
    public void TheseGenuinelyNeedOne(string input)
        => Assert.True(NeedsAModel(input), $"{input} cannot work without a provider");

    [Fact]
    public void OrdinaryTextStillNeedsAModel()
    {
        // The guard must still stop a GOAL. Removing it wholesale would send prose at a session with
        // nothing to send it to.
        Assert.True(NeedsAModel("summarise this folder"));
        Assert.True(NeedsAModel("what does Program.cs do?"));
    }

    [Fact]
    public void EveryShippedCommandIsClassified()
    {
        // A THIRTEENTH COMMAND MUST NOT DEFAULT TO BLOCKED. Match returns null for anything it does
        // not know, which reads as NotACommand — so a command added to the table but forgotten here
        // silently becomes unavailable without a model. This fails when the counts diverge.
        var commands = SessionCommands.All;

        Assert.Equal(13, commands.Count);
        Assert.All(commands, c => Assert.NotNull(SessionCommands.Match(c.Name)));
    }
}
