using System.Linq;
using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

public class SessionCommandsTests
{
    [Fact]
    public void Clear_Matches_AndNeedsNoModel()
    {
        // WHAT /clear ACTUALLY CLEARS is the agent's context, and the caller does that — this type
        // holds no session state. AgentTests covers the context, which is the real thing.
        var clear = SessionCommands.Match("/clear");

        Assert.NotNull(clear);
        Assert.False(clear.Value.NeedsModel);
    }

    [Fact]
    public void Compress_Matches_AndNeedsAModel()
    {
        // /compress means COMPRESS, not truncate: summarising needs a provider call, which is why
        // it is the one command besides /init that NeedsModel.
        Assert.True(SessionCommands.Match("/compress")?.NeedsModel);
    }

    [Fact]
    public void CompressDoesNotMatchAnOrdinaryGoal()
    {
        // Same false-positive rule as the other commands: "compress the log files" is a GOAL.
        Assert.Null(SessionCommands.Match("compress the log files"));
        Assert.Null(SessionCommands.Match("what does /compress do?"));
    }

    [Fact]
    public void AnOrdinaryGoalIsNotACommand()
    {
        // "/clear" is a command; "clear the build output" is a GOAL. Only an exact leading-slash token
        // counts, or a user loses work to a false positive.
        Assert.Null(SessionCommands.Match("clear the build output"));
        Assert.Null(SessionCommands.Match("what does /clear do?"));
    }

    [Fact]
    public void AnUnknownSlashCommandDoesNotMatch()
    {
        // A typo'd command must not resolve to anything — the caller is what decides a typo is a
        // command attempt rather than a goal.
        Assert.Null(SessionCommands.Match("/claer"));
    }

    [Fact]
    public void JobsIsACommandWithAKillSubcommand()
    {
        // "kill 2243233" is the argument, not more of the name — Match only needs to recognise
        // "/jobs" itself; the verb is dispatched by CommandRegistry, not by this table.
        var command = SessionCommands.Match("/jobs kill 2243233");
        Assert.NotNull(command);
        Assert.Equal("/jobs", command!.Value.Name);
    }

    [Fact]
    public void TheKillArgumentCompletesFromTheLiveJobs()
    {
        // The <pid> argument declares Values: ValueSources.BackgroundJobs, so the palette can fill
        // it from whatever is actually running rather than leaving the user to retype a pid they
        // just read off a /jobs row.
        var kill = SessionCommands.Match("/jobs")!.Value.Args.Single(a => a.Name == "kill <pid>");
        Assert.Equal(ValueSources.BackgroundJobs, kill.Values);
    }

    [Fact]
    public void JobsIsNotAdvertisedToTheModel()
    {
        // TellTheModel is false: the model has job_list/job_kill already, and that flag is for a
        // dead end the model walks into — /jobs is not one, since the model has its own tools.
        Assert.False(SessionCommands.Match("/jobs")!.Value.TellTheModel);
    }
}
