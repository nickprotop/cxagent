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
    public void TasksIsACommandWithStopAndOutputSubcommands()
    {
        // "stop x" is the argument, not more of the name — Match only needs to recognise "/tasks"; the
        // verb is dispatched by CommandRegistry, not by this table.
        var command = SessionCommands.Match("/tasks stop find-thing");
        Assert.NotNull(command);
        Assert.Equal("/tasks", command!.Value.Name);
        Assert.Contains(command.Value.Args, a => a.Name == "stop <handle|pid>");
        Assert.Contains(command.Value.Args, a => a.Name == "output <handle|pid>");
    }

    [Fact]
    public void TheTaskArgumentsCompleteFromWhatIsRunning()
    {
        // A handle is a slug the user may never have typed, a pid is a number they just read off a row:
        // both are filled from what is actually there.
        foreach (var arg in SessionCommands.Match("/tasks")!.Value.Args)
            Assert.Equal(ValueSources.Tasks, arg.Values);
    }

    [Fact]
    public void TasksIsNotAdvertisedToTheModel()
    {
        // TellTheModel is false: the model has task_list/task_stop already, and that flag is for a
        // dead end the model walks into — /tasks is not one, since the model has its own tools.
        Assert.False(SessionCommands.Match("/tasks")!.Value.TellTheModel);
    }

    /// <summary>/JOBS IS GONE: /tasks lists commands and agents together, as the model's task_list does.</summary>
    [Fact]
    public void JobsIsNoLongerACommand() => Assert.Null(SessionCommands.Match("/jobs"));
}
