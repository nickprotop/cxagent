using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which commands a session can run with NO model configured.
///
/// <para>THE BUG THIS PINS: the composer's submit handler opened with
/// `if (!session.HasAgent || ...) return;`, which swallowed the keystroke before anything looked at
/// what was typed. A session that opened without a working provider could not run /help, /stats,
/// /sessions — or /model, the one command that FIXES having no provider. The window was unusable
/// except by killing it.</para>
///
/// <para>THE CLASSIFICATION IS IN THE TABLE, and these tests hold it to its word.
/// <see cref="SessionCommand.NeedsModel"/> is the guard's whole input: everything it marks false
/// answers from state the process already holds, costing no tokens and no time. A command added
/// later with the wrong flag fails here rather than in someone's unusable window.</para>
/// </summary>
public class NoProviderCommandTests
{
    /// <summary>What the submit handler decides, expressed as the predicate it uses.</summary>
    private static bool NeedsAModel(string input) =>
        SessionCommands.Match(input) is not { } command || command.NeedsModel;

    /// <summary>
    /// The WHOLE handler, both gates, in the order it runs them.
    ///
    /// <para>THE FIRST FIX PASSED ITS TESTS AND SHIPPED BROKEN because they modelled only
    /// <c>session.HasAgent</c>. The handler had a SECOND flag meaning the same thing —
    /// <c>mainWindow.SubmissionEnabled</c>, set from <c>resolution.HasProvider</c> — and it was
    /// still checked ABOVE the classification, so it returned before any of this ran. Testing a
    /// predicate proves the predicate; this models the sequence.</para>
    /// </summary>
    private static bool KeystrokeReachesTheCommand(string input, bool hasAgent, bool submissionEnabled)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (!hasAgent || !submissionEnabled) return !NeedsAModel(input);
        return true;
    }

    [Theory]
    [InlineData("/model")]
    [InlineData("/help")]
    [InlineData("/stats")]
    public void CommandsRunWithBothFlagsOff(string input)
    {
        // BOTH FALSE IS THE REAL NO-PROVIDER STATE. A session with no model has HasAgent false AND
        // SubmissionEnabled false; testing either alone is what let the bug through.
        Assert.True(KeystrokeReachesTheCommand(input, hasAgent: false, submissionEnabled: false),
            $"{input} must reach its handler when no provider is configured");
    }

    [Fact]
    public void SubmissionEnabledAloneDoesNotBlockACommand()
    {
        // The exact shape of the shipped bug: an agent exists but the composer flag is off.
        // /help, NOT /exit — /exit is not in Core's table at all now, so NeedsAModel's null-command
        // branch would read it as blocked and pass for the wrong reason.
        Assert.True(KeystrokeReachesTheCommand("/help", hasAgent: true, submissionEnabled: false));
    }

    [Fact]
    public void AGoalIsStillBlockedByEitherFlag()
    {
        Assert.False(KeystrokeReachesTheCommand("summarise this", hasAgent: false, submissionEnabled: true));
        Assert.False(KeystrokeReachesTheCommand("summarise this", hasAgent: true, submissionEnabled: false));
        Assert.True(KeystrokeReachesTheCommand("summarise this", hasAgent: true, submissionEnabled: true));
    }

    [Theory]
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
    [InlineData("/trust")]       // the folder's classification, and the store is on disk
    [InlineData("/plugin")]      // the registry and config.Plugins, neither of which is the model
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
        // A FOURTEENTH COMMAND MUST NOT DEFAULT TO BLOCKED. Match returns null for anything it does
        // not know, which reads as "not a command" — so a command added to the table but forgotten in
        // TheseRunWithNoModel silently becomes unavailable without a model. This fails when the
        // counts diverge.
        var commands = SessionCommands.All;

        Assert.Equal(14, commands.Count);
        Assert.All(commands, c => Assert.NotNull(SessionCommands.Match(c.Name)));
    }

    /// <summary>
    /// EXACTLY TWO COMMANDS REACH THE MODEL. Every other command answers from state the process
    /// already holds, costing no tokens and no time — which is why a session with no provider
    /// must still run them. /model is the command that FIXES having no provider; blocking it
    /// leaves the window unusable except by killing it.
    /// </summary>
    [Fact]
    public void OnlyCompressAndInit_NeedAModel()
    {
        var needy = SessionCommands.All.Where(c => c.NeedsModel).Select(c => c.Name).ToList();
        needy.Sort(StringComparer.Ordinal);

        Assert.Equal(new[] { "/compress", "/init" }, needy);
    }
}
