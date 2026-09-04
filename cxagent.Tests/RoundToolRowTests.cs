using SharpConsoleUI.Controls;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A round's tool calls on one row.
///
/// <para>THE THREADING IS THE DESIGN, so several of these pin it rather than the appearance: a call
/// arriving must set a flag and nothing else, and the render must happen once however many calls
/// arrived. An earlier build wrote the row from three threads and produced duplicate rows, prose out
/// of order, and a stalled main loop.</para>
/// </summary>
public class RoundToolRowTests
{
    private static ToolCallReport Call(string agentId, string tool, string? jobType = "file") =>
        new(Guid.NewGuid().ToString(), agentId, tool, jobType, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    // A CALL SETS A FLAG AND DRAWS NOTHING. The renderer is the tick, and only the tick — this is
    // what turns twenty calls into one render instead of twenty enqueued re-renders.
    [Fact]
    public void ACallDirtiesTheRoundWithoutDrawing()
    {
        var (sink, chat) = SinkFixture.Build();
        sink.OpenRoundForTest();
        var before = chat.MessageIds.Count;

        sink.RecordToolCall(Call("parent", "read_file"));

        Assert.True(sink.RoundIsDirtyForTest);
        Assert.Equal(before, chat.MessageIds.Count);
    }

    // AND MANY CALLS COST ONE RENDER. The flag is a bool, not a queue.
    [Fact]
    public void ManyCallsRenderOnce()
    {
        var (sink, chat) = SinkFixture.Build();
        sink.OpenRoundForTest();

        for (var i = 0; i < 20; i++) sink.RecordToolCall(Call("parent", "read_file"));
        sink.PaintRoundForTest();

        Assert.False(sink.RoundIsDirtyForTest);
        Assert.Single(chat.MessageIds);
    }

    // ONE ROW PER ROUND, NEVER TWO. The previous build created the message through `??=` on a field
    // written by two threads, and a paint racing a settle produced duplicates.
    [Fact]
    public void RepeatedPaintsReuseTheSameRow()
    {
        var (sink, chat) = SinkFixture.Build();
        sink.OpenRoundForTest();
        sink.RecordToolCall(Call("parent", "read_file"));

        sink.PaintRoundForTest();
        sink.RecordToolCall(Call("parent", "grep"));
        sink.PaintRoundForTest();
        sink.PaintRoundForTest();

        Assert.Single(chat.MessageIds);
    }

    // A ROUND THAT CALLED NOTHING ADDS NO ROW, which keeps a conversational round unchanged.
    [Fact]
    public void ARoundWithNoCallsAddsNoRow()
    {
        var (sink, chat) = SinkFixture.Build();
        sink.OpenRoundForTest();

        sink.PaintRoundForTest();

        Assert.Empty(chat.MessageIds);
    }

    // EACH ROUND COUNTS ITS OWN CALLS. A row spanning a whole turn would put calls made before a
    // paragraph and after it in one table.
    [Fact]
    public void EachRoundSeesOnlyItsOwnCalls()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();
        sink.RecordToolCall(Call("parent", "first"));

        sink.OpenRoundForTest();
        sink.RecordToolCall(Call("parent", "second"));

        var calls = sink.RoundCallsForTest();

        Assert.Single(calls);
        Assert.Equal("second", calls[0].ToolName);
    }

    // A WORKER'S CALLS ARE NOT THE ROUND'S. They arrive under the child's id and belong to its row.
    [Fact]
    public void AChildsCallsAreNotTheRounds()
    {
        var (sink, _) = SinkFixture.Build();
        sink.NoteChildAgentForTest("child");
        sink.OpenRoundForTest();

        sink.RecordToolCall(Call("parent", "read_file"));
        sink.RecordToolCall(Call("child", "grep"));
        sink.RecordToolCall(Call("child", "read_file"));

        var calls = sink.RoundCallsForTest();

        Assert.Single(calls);
        Assert.Equal("parent", calls[0].AgentId);
    }

    // A SPAWN HAS ITS OWN ROW, so counting it here shows one spawn twice.
    [Fact]
    public void ASpawnIsNotCountedInTheTable()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();

        sink.RecordToolCall(Call("parent", "read_file"));
        sink.RecordToolCall(Call("parent", "agent", "llm_agent"));

        Assert.Single(sink.RoundCallsForTest());
    }

    // A TURN WHOSE FIRST ACT IS A SPAWN MUST NOT ADOPT THE CHILD — the bug that read "5 calls"
    // beside a panel reading "1 tool call".
    [Fact]
    public void ATurnThatSpawnsFirstDoesNotAdoptTheChild()
    {
        var (sink, _) = SinkFixture.Build();
        sink.NoteChildAgentForTest("child");
        sink.OpenRoundForTest();

        sink.RecordToolCall(Call("parent", "agent", "llm_agent"));
        sink.RecordToolCall(Call("child", "read_file"));
        sink.RecordToolCall(Call("child", "grep"));

        Assert.Empty(sink.RoundCallsForTest());
    }

    // THE WORKING FOLDS; THE ANSWER DOES NOT.
    [Theory]
    [InlineData("file", false)]
    [InlineData("shell", false)]
    [InlineData("llm_agent", true)]
    [InlineData("todo", true)]
    public void OnlyWorkingRowsAreFolded(string jobType, bool keepsItsOwnRow)
        => Assert.Equal(keepsItsOwnRow, InlineJobSink.ShouldShowForTest(new Job
        {
            Id = "j", AgentId = "a", JobType = jobType, DisplayName = jobType,
            State = JobState.Succeeded, CreatedAt = DateTimeOffset.UtcNow,
        }));

    // A DENIAL KEEPS ITS ROW: the reader answered it a moment ago.
    [Fact]
    public void ADeniedCallKeepsItsOwnRow()
        => Assert.True(InlineJobSink.ShouldShowForTest(new Job
        {
            Id = "j", AgentId = "a", JobType = "shell", DisplayName = "run_shell",
            State = JobState.Succeeded, CreatedAt = DateTimeOffset.UtcNow,
            Result = new JobResult { Success = false, PermissionDenied = true },
        }));

    [Fact]
    public void AFailedCallKeepsItsOwnRow()
        => Assert.True(InlineJobSink.ShouldShowForTest(new Job
        {
            Id = "j", AgentId = "a", JobType = "shell", DisplayName = "run_shell",
            State = JobState.Failed, CreatedAt = DateTimeOffset.UtcNow,
        }));
}

/// <summary>Where the row sits relative to the prose it describes.</summary>
public class RoundRowOrderingTests
{
    private static ToolCallReport Call(string tool) =>
        new(Guid.NewGuid().ToString(), "parent", tool, "file", "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    // USER-REPORTED: "The ordering (when running isn't nice)" — the prose landed above the row
    // holding the calls that produced it. A transcript row is appended at the END, so a row first
    // created when the tick fires sits below whatever arrived in the meantime. Claiming the message
    // when the call is recorded fixes the order; the tick still fills it.
    [Fact]
    public void TheRowTakesItsPlaceBeforeTheProseArrives()
    {
        var (sink, chat) = SinkFixture.Build();

        sink.RecordToolCall(Call("run_shell"));
        sink.ClaimRoundRowForTest();          // what the enqueue does in the app

        var rowId = Assert.Single(chat.MessageIds);

        // The model then says something about it.
        var prose = chat.AddMessage(ChatRole.Assistant, "The first command printed: one");

        Assert.Equal(rowId, chat.MessageIds[0]);
        Assert.Equal(prose, chat.MessageIds[1]);
    }

    // AND CLAIMING TWICE IS ONE ROW. Every call enqueues a claim, so the second must find the first.
    [Fact]
    public void ManyCallsClaimOneRow()
    {
        var (sink, chat) = SinkFixture.Build();

        for (var i = 0; i < 5; i++)
        {
            sink.RecordToolCall(Call("read_file"));
            sink.ClaimRoundRowForTest();
        }

        Assert.Single(chat.MessageIds);
    }
}

/// <summary>Rounds that have nothing to show must not claim a row.</summary>
public class EmptyRoundRowTests
{
    private static ToolCallReport Call(string tool, string? jobType) =>
        new(Guid.NewGuid().ToString(), "parent", tool, jobType, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    // USER-REPORTED: "what is the empty tools row??" — a round whose only act was spawning a worker
    // claimed a message and never filled it, because a spawn is excluded from the table (the worker
    // keeps its own row). The claim has to wait for a call that will actually render.
    [Fact]
    public void ASpawnOnlyRoundClaimsNoRow()
    {
        var (sink, chat) = SinkFixture.Build();

        sink.RecordToolCall(Call("agent", "llm_agent"));
        sink.ClaimRoundRowForTest();

        Assert.Empty(chat.MessageIds);
    }

    // AND A ROUND THAT ALSO DID REAL WORK STILL GETS ONE.
    [Fact]
    public void ARoundThatSpawnsAndAlsoWorksClaimsARow()
    {
        var (sink, chat) = SinkFixture.Build();

        sink.RecordToolCall(Call("agent", "llm_agent"));
        sink.ClaimRoundRowForTest();
        sink.RecordToolCall(Call("read_file", "file"));
        sink.ClaimRoundRowForTest();

        Assert.Single(chat.MessageIds);
    }
}

/// <summary>A round that made one call names it, and shows what it returned.</summary>
public class SingleCallRowTests
{
    private static ToolCallReport Call(string tool, string? target, string? output = null) =>
        new(Guid.NewGuid().ToString(), "parent", tool, "file", "succeeded", 5,
            output?.Length ?? 0, DateTimeOffset.UtcNow) { Target = target, Output = output };

    // ONE CALL IS NAMED, NOT COUNTED. "1 call · across 1 tool" says three times over what the reader
    // can see once, and drops the only fact that tells this round from any other.
    [Fact]
    public void OneCallIsNamedInsteadOfCounted()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("read_file", "read_file · UI/FileProbe.cs"));
        sink.ClaimRoundRowForTest();
        sink.PaintRoundForTest();

        var header = sink.RoundHeaderForTest();

        Assert.Contains("UI/FileProbe.cs", header);
        Assert.DoesNotContain("1 call", header);
        Assert.DoesNotContain("across 1 tool", header);
    }

    // THE TIMING SURVIVES: only the two counts at the front are dropped.
    [Fact]
    public void OneCallKeepsTheRestOfTheSummary()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("run_shell", "run_shell · dotnet build"));
        sink.ClaimRoundRowForTest();
        sink.PaintRoundForTest();

        Assert.Contains("in tools", sink.RoundHeaderForTest());
    }

    // TWO CALLS STILL COUNT. The count earns its place where naming them all is not possible.
    [Fact]
    public void TwoCallsAreStillCounted()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("read_file", "read_file · a.cs"));
        sink.RecordToolCall(Call("grep", "grep · \"x\""));
        sink.ClaimRoundRowForTest();
        sink.PaintRoundForTest();

        Assert.Contains("2 calls", sink.RoundHeaderForTest());
    }

    // ONE ROW IS NOT A TABLE. Expanding a single-call round should show what it returned, not
    // scaffolding around one line of cells — and the output is the fact the table never showed.
    [Fact]
    public void OneCallShowsItsOutput()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("read_file", "read_file · a.cs", "the file's contents"));
        sink.ClaimRoundRowForTest();
        sink.PaintRoundForTest();

        Assert.Contains("the file's contents", sink.RoundBodyForTest());
        Assert.DoesNotContain("| tool |", sink.RoundBodyForTest());
    }

    // A CALL THAT RETURNED NOTHING KEEPS ITS TABLE. An empty body under an expand is worse than the
    // scaffolding it replaced.
    [Fact]
    public void OneCallWithNoOutputKeepsTheTable()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("read_file", "read_file · a.cs"));
        sink.ClaimRoundRowForTest();
        sink.PaintRoundForTest();

        Assert.Contains("| tool |", sink.RoundBodyForTest());
    }
}
