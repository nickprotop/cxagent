using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which calls belong to the turn on screen. The list underneath is the session's, append-only, so
/// a turn is a position in it rather than a copy of it.
/// </summary>
public class TurnToolTableTests
{
    private static ToolCallReport Call(string agentId, string tool) =>
        new(Guid.NewGuid().ToString(), agentId, tool, null, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    [Fact]
    public void BeforeAnyTurn_ThereAreNoTurnCalls()
    {
        var (sink, _) = SinkFixture.Build();

        sink.RecordToolCall(Call("parent", "read_file"));

        Assert.Empty(sink.TurnCallsForTest());
    }

    [Fact]
    public void ATurnSeesOnlyTheCallsMadeAfterItBegan()
    {
        var (sink, _) = SinkFixture.Build();
        sink.RecordToolCall(Call("parent", "before"));

        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "during"));

        var calls = sink.TurnCallsForTest();

        Assert.Single(calls);
        Assert.Equal("during", calls[0].ToolName);
    }

    // A CHILD'S CALLS ARRIVE UNDER THE CHILD'S OWN ID, so they are not this turn's by construction —
    // no filtering, and this pins the assumption the whole design rests on.
    [Fact]
    public void AChildsCallsAreNotTheParentsTurn()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");

        sink.RecordToolCall(Call("child", "read_file"));

        Assert.Empty(sink.TurnCallsForTest());
    }

    [Fact]
    public void ASecondTurnStartsFromWhereTheFirstEnded()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "first"));
        sink.TurnEnded();

        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "second"));

        var calls = sink.TurnCallsForTest();

        Assert.Single(calls);
        Assert.Equal("second", calls[0].ToolName);
    }

    // A TURN THAT CALLED NOTHING HAS NOTHING, which is what keeps a conversational turn unchanged.
    [Fact]
    public void ATurnWithNoCallsIsEmpty()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");

        Assert.Empty(sink.TurnCallsForTest());
    }
}

/// <summary>The row itself: one message per turn, carrying the same table a worker's row shows.</summary>
public class TurnRowTests
{
    private static ToolCallReport Call(string agentId, string tool) =>
        new(Guid.NewGuid().ToString(), agentId, tool, null, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    // THE SAME TABLE THE WORKER ROW USES, not a second one that could drift from it — minus the
    // summary line, which is the header. Printing it in both places makes a reader check whether the
    // two agree.
    [Fact]
    public void TheBodyIsTheTimetablesTableWithoutItsSummary()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "read_file"));
        sink.RecordToolCall(Call("parent", "grep"));

        var whole = InlineJobSink.TimetableForTest(sink.TurnCallsForTest());
        var body = sink.TurnRowBodyForTest();

        Assert.EndsWith(body!.TrimEnd(), whole.TrimEnd());
        Assert.DoesNotContain("2 calls", body);
        Assert.Contains("read_file", body);
        Assert.Contains("grep", body);
    }

    // AND THE HEADER IS ITS SUMMARY LINE — one source, so the two cannot disagree.
    [Fact]
    public void TheHeaderCountsTheCallsAndTheDistinctTools()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "read_file"));
        sink.RecordToolCall(Call("parent", "read_file"));
        sink.RecordToolCall(Call("parent", "grep"));

        var header = sink.TurnRowHeaderForTest();

        Assert.Contains("3 calls", header);
        Assert.Contains("across 2 tools", header);
    }

    [Fact]
    public void NoCallsMeansNoRow()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");

        Assert.Null(sink.TurnRowHeaderForTest());
    }
}

/// <summary>What the header says while the turn is still working.</summary>
public class TurnRowInFlightTests
{
    private static ToolCallReport Call(string agentId, string tool) =>
        new(Guid.NewGuid().ToString(), agentId, tool, null, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    private static Job RunningJob(string tool, string target) => new()
    {
        Id = Guid.NewGuid().ToString(), AgentId = "parent", JobType = "file",
        DisplayName = $"{tool} {target}", PlanLocalId = tool,
        State = JobState.Running,
        CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
    };

    // THE HEADER NAMES WHAT IS RUNNING. Six calls in, "6 calls" alone says nothing about which file
    // it is reading — which is the one thing the per-call rows did well.
    [Fact]
    public void TheHeaderNamesTheCallInFlight()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "read_file"));
        sink.ToolsChangedNow([RunningJob("read_file", "Agent.cs")]);

        var header = sink.TurnRowHeaderForTest();

        Assert.Contains("read_file", header);
        Assert.Contains("Agent.cs", header);
    }

    // AND DROPS IT WHEN THE TURN IS OVER — a finished row that still claims to be doing something is
    // worse than one that says nothing.
    [Fact]
    public void TheFinishedHeaderNamesNothingInFlight()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "read_file"));
        sink.ToolsChangedNow([RunningJob("read_file", "Agent.cs")]);

        sink.TurnEnded();

        Assert.Null(sink.TurnRowHeaderForTest());
    }
}

/// <summary>Which rows fold into the turn's row, and which keep their own line.</summary>
public class FoldedRowTests
{
    private static Job JobOfType(string jobType, JobState state = JobState.Succeeded,
                                 bool denied = false) => new()
    {
        Id = Guid.NewGuid().ToString(), AgentId = "parent", JobType = jobType,
        DisplayName = jobType, State = state,
        CreatedAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
        Result = denied
            ? new JobResult { Success = false, PermissionDenied = true }
            : null,
    };

    // THE WORKING FOLDS; THE ANSWER DOES NOT. A worker's report and a todo list ARE the point of
    // their rows — folding one into a count deletes a feature rather than summarising it.
    [Theory]
    [InlineData("file", false)]
    [InlineData("shell", false)]
    [InlineData("llm_agent", true)]
    [InlineData("todo", true)]
    public void OnlyWorkingRowsAreFolded(string jobType, bool keepsItsOwnRow)
        => Assert.Equal(keepsItsOwnRow, InlineJobSink.ShouldShowForTest(JobOfType(jobType)));

    // A DENIAL KEEPS ITS ROW. The reader answered it a moment ago; making their own decision harder
    // to find than it was is the one regression this feature must not ship.
    [Fact]
    public void ADeniedCallStillGetsItsOwnRow()
        => Assert.True(InlineJobSink.ShouldShowForTest(JobOfType("shell", denied: true)));

    // AND SO DOES A FAILURE — the row nobody wants folded is exactly the row that went wrong.
    [Fact]
    public void AFailedCallStillGetsItsOwnRow()
        => Assert.True(InlineJobSink.ShouldShowForTest(JobOfType("shell", JobState.Failed)));
}

/// <summary>A row per answer, not per turn.</summary>
public class RoundScopedRowTests
{
    private static ToolCallReport Call(string agentId, string tool) =>
        new(Guid.NewGuid().ToString(), agentId, tool, null, "succeeded", 5, 10,
            DateTimeOffset.UtcNow);

    // A TURN IS OFTEN SEVERAL ROUNDS — work, speak, work again. One row spanning all of them would
    // put calls made before a paragraph and after it in the same table, below prose the reader has
    // already gone past.
    [Fact]
    public void EachRoundGetsItsOwnCalls()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "first"));

        sink.RoundEnded();
        sink.RecordToolCall(Call("parent", "second"));

        var calls = sink.TurnCallsForTest();

        Assert.Single(calls);
        Assert.Equal("second", calls[0].ToolName);
    }

    // AND THE SCOPE REOPENS, so the next round's first call has one waiting rather than falling
    // outside every scope — which is what happens when a boundary only ever closes.
    [Fact]
    public void ARoundEndingLeavesAScopeOpen()
    {
        var (sink, _) = SinkFixture.Build();
        sink.TurnBegan("parent");
        sink.RecordToolCall(Call("parent", "first"));

        sink.RoundEnded();

        // The new scope is empty, so there is no row yet …
        Assert.Null(sink.TurnRowHeaderForTest());

        // … and the next call lands in it rather than outside every scope.
        sink.RecordToolCall(Call("parent", "second"));
        Assert.Contains("1 call", sink.TurnRowHeaderForTest());
    }

    // A ROUND THAT ENDS OUTSIDE A TURN CHANGES NOTHING — the boundaries fire on every model round,
    // including ones with no user turn behind them.
    [Fact]
    public void ARoundEndingWithNoTurnIsANoOp()
    {
        var (sink, _) = SinkFixture.Build();

        sink.RoundEnded();

        Assert.Empty(sink.TurnCallsForTest());
    }
}
