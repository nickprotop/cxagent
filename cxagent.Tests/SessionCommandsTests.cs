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
}
