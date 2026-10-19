using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A running call's progress and streamed text, from the call's context to its row: the percent and
/// message on the round's header and on the table's live row, the streamed text behind the expand.
/// </summary>
public class ToolReportDisplayTests
{
    private static Job Running(string tool = "count_slowly", string args = "{\"n\":20}") => new()
    {
        Id = Guid.NewGuid().ToString(), AgentId = "parent", JobType = "plugin",
        PlanLocalId = tool, DisplayName = $"{tool} {args}",
        State = JobState.Running, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ToolCallReport Finished(string tool) =>
        new(Guid.NewGuid().ToString(), "parent", tool, "file", "succeeded", 5, 10, DateTimeOffset.UtcNow);

    // ---- the context raises what it is told ----------------------------------------------------

    [Fact]
    public void ReportProgress_IsRaised_ForWhoeverDrawsTheCall()
    {
        var ctx = new JobContext("a", "j", new Dictionary<string, JobResult>(), null);
        (double Percent, string? Message)? seen = null;
        ctx.ProgressReported += (percent, message) => seen = (percent, message);

        ctx.ReportProgress(45, "counted 9 of 20");

        Assert.Equal((45d, "counted 9 of 20"), seen);
    }

    // ---- the feed turns reports into the job's fields ------------------------------------------

    [Fact]
    public void Progress_IsThePercentThenTheMessage()
    {
        var job = Running();
        var feed = new JobReportFeed(job);

        feed.Progress(45, "counted 9 of 20");

        Assert.Equal(45, job.Progress);
        Assert.Equal("45% · counted 9 of 20", job.ProgressMessage);
    }

    [Fact]
    public void Progress_WithNoMessage_IsThePercentAlone()
    {
        var job = Running();

        new JobReportFeed(job).Progress(45, null);

        Assert.Equal("45%", job.ProgressMessage);
    }

    [Fact]
    public void Progress_OutOfRange_IsClamped()
    {
        var job = Running();
        var feed = new JobReportFeed(job);

        feed.Progress(140, null);
        Assert.Equal("100%", job.ProgressMessage);
        feed.Progress(-5, null);
        Assert.Equal("0%", job.ProgressMessage);
    }

    [Fact]
    public void StreamedText_Accumulates()
    {
        var job = Running();
        var feed = new JobReportFeed(job);

        feed.Text("1 ");
        feed.Text("2 ");

        Assert.Equal("1 2 ", job.ProgressBody);
    }

    /// <summary>The last 200 lines, and one line saying how many went before them.</summary>
    [Fact]
    public void StreamedText_KeepsTheLast200Lines_AndSaysHowManyWereDropped()
    {
        var job = Running();
        var feed = new JobReportFeed(job);

        for (var i = 1; i <= 250; i++) feed.Text($"line {i}\n");

        var lines = job.ProgressBody!.TrimEnd('\n').Split('\n');
        Assert.Equal("… 50 earlier lines not shown", lines[0]);
        Assert.Equal("line 51", lines[1]);
        Assert.Equal("line 250", lines[^1]);
        Assert.Equal(201, lines.Length);
    }

    /// <summary>A line arriving in pieces is one line, not several.</summary>
    [Fact]
    public void StreamedText_ALineInPieces_CountsOnce()
    {
        var job = Running();
        var feed = new JobReportFeed(job);

        for (var i = 1; i <= 201; i++) { feed.Text($"line {i}"); feed.Text("\n"); }

        var lines = job.ProgressBody!.TrimEnd('\n').Split('\n');
        Assert.Equal("… 1 earlier line not shown", lines[0]);
        Assert.Equal("line 2", lines[1]);
    }

    // ---- the round row draws them --------------------------------------------------------------

    /// <summary>
    /// THE ROUND'S FIRST CALL, WHILE IT RUNS. Nothing has finished yet, so nothing has been recorded,
    /// and a round opened only by a recorded call has no row to draw the running one in: the
    /// reader saw nothing for the whole length of the call.
    /// </summary>
    [Fact]
    public void TheFirstCallOfARound_GetsItsRow_WhileItRuns()
    {
        var (sink, chat) = SinkFixture.Build();
        var job = Running();
        new JobReportFeed(job).Progress(45, "counted 9 of 20");

        sink.ToolsChangedNow([job]);
        sink.PaintRoundForTest();

        Assert.Single(chat.MessageIds);
        Assert.Equal("count_slowly {\"n\":20} · 45% · counted 9 of 20", sink.RoundHeaderForTest());
    }

    /// <summary>A call that has already finished opens nothing: the recorded call does that, as before.</summary>
    [Fact]
    public void AFinishedJob_DoesNotOpenARound()
    {
        var (sink, chat) = SinkFixture.Build();
        var done = Running() with { State = JobState.Succeeded };

        sink.ToolsChangedNow([done]);
        sink.PaintRoundForTest();

        Assert.Empty(chat.MessageIds);
    }

    [Fact]
    public void TheRoundHeader_ShowsTheRunningCallsProgress()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();
        var job = Running();
        new JobReportFeed(job).Progress(45, "counted 9 of 20");
        sink.ToolsChangedNow([job]);

        Assert.EndsWith("count_slowly {\"n\":20} · 45% · counted 9 of 20", sink.RoundHeaderForTest());
    }

    [Fact]
    public void TheRoundHeader_WithoutProgress_IsUnchanged()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();
        sink.ToolsChangedNow([Running()]);

        Assert.EndsWith("count_slowly {\"n\":20}", sink.RoundHeaderForTest());
    }

    [Fact]
    public void TheRoundBody_ShowsTheStreamedText_WhileTheCallRuns()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();
        var job = Running();
        new JobReportFeed(job).Text("1 2 3 ");
        sink.ToolsChangedNow([job]);

        Assert.EndsWith("1 2 3 ", sink.RoundBodyForTest());
    }

    /// <summary>
    /// Earlier calls in the round keep their table; the running call's text follows it, and the
    /// table's live row carries the call's progress.
    /// </summary>
    [Fact]
    public void AfterEarlierCalls_TheTableStays_ItsLiveRowCarriesProgress_AndTheTextFollows()
    {
        var (sink, _) = SinkFixture.Build();
        sink.OpenRoundForTest();
        sink.RecordToolCall(Finished("read_file"));
        var job = Running();
        var feed = new JobReportFeed(job);
        feed.Progress(45, "counted 9 of 20");
        feed.Text("1 2 3 ");
        sink.ToolsChangedNow([job]);

        var body = sink.RoundBodyForTest();
        var liveRow = body.Split('\n').Last(line => line.Contains("count_slowly", StringComparison.Ordinal));

        Assert.Contains("read_file", body, StringComparison.Ordinal);
        Assert.Contains("45% · counted 9 of 20", liveRow, StringComparison.Ordinal);
        Assert.EndsWith("1 2 3 ", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// The agent joins a call's context to the call's row: what the tool reports while it runs is on
/// the job while it runs, and gone once it has finished — a finished row says how it ended, not
/// how far along it was.
/// </summary>
public class AgentToolReportTests : IDisposable
{
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "toolreport-state-" + Guid.NewGuid().ToString("N"));
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "toolreport-work-" + Guid.NewGuid().ToString("N"));

    public AgentToolReportTests()
    {
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>Reports, then waits for the test to look before it returns.</summary>
    private sealed class ReportingTool : CxAgent.Core.Jobs.IAgentTool
    {
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Looked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CxAgent.Core.Jobs.IJobContext? Context { get; private set; }

        public ToolDefinition Definition { get; } = new(
            "counter", "counts", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }));

        public CxAgent.Core.Permissions.PermissionRequest? Gate(JobParameters call) => null;

        public async Task<JobResult> ExecuteAsync(JobParameters call, CxAgent.Core.Jobs.IJobContext context, CancellationToken ct)
        {
            Context = context;
            context.ReportProgress(50, "half");
            context.ReportTextDelta("one ");
            Reported.SetResult();
            await Looked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new JobResult { Success = true, Output = { ["content"] = "counted" } };
        }
    }

    private sealed class Watching : IToolObserver
    {
        private readonly object _lock = new();
        private readonly List<Job> _seen = [];
        public Job? Find(string name) { lock (_lock) return _seen.LastOrDefault(j => j.DisplayName.StartsWith(name, StringComparison.Ordinal)); }
        public void ToolsChanged(IReadOnlyList<Job> jobs) { lock (_lock) _seen.AddRange(jobs); }
        public void ToolUpdated(Job job) { lock (_lock) _seen.Add(job); }
        public void ToolProgressed(Job job) { }
        public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
        public void ToolOutputAppended(string jobId, string delta) { }
    }

    private sealed class Allow : CxAgent.Core.Permissions.IPermissionGate
    {
        public Task<CxAgent.Core.Permissions.PermissionOutcome> RequestAsync(
            CxAgent.Core.Permissions.PermissionRequest request, CancellationToken ct) =>
            Task.FromResult(CxAgent.Core.Permissions.PermissionOutcome.Allow);
    }

    [Fact]
    public async Task WhatACallReports_IsOnItsJobWhileItRuns_AndClearedWhenItEnds()
    {
        var provider = new CxAgent.Core.Llm.MockLlmProvider();
        provider.EnqueueResponse(CxAgent.Core.Llm.LlmResponse.WithToolCall("counter", new { }));
        provider.EnqueueResponse(new CxAgent.Core.Llm.LlmResponse { Text = "done", StopReason = "end_turn" });
        using var manager = SessionManager.Create(new ProcessSetup
        {
            Paths = new CxAgent.Core.Storage.AppPaths(_state),
            Config = ResolvedConfig.ForTesting(provider),
            BuildGate = _ => new Allow(),
        });
        var tool = new ReportingTool();
        var jobs = new Watching();
        var session = manager.Open(_work, new SessionPorts
        {
            Observer = new BufferedChatSink(),
            ToolObserver = jobs,
            Tools = [tool],
            Policy = new CxAgent.Core.Permissions.PermissionPolicy(_work, manager.Rules!, EditMode.Auto),
        });

        var turn = session.SendAndWait("count");
        await tool.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var running = jobs.Find("counter")!;
        var midFlight = (running.ProgressMessage, running.ProgressBody);
        tool.Looked.SetResult();
        await turn;

        // A REPORT AFTER THE CALL HAS ENDED goes nowhere: the row has settled.
        tool.Context!.ReportProgress(99, "late");

        Assert.Equal(("50% · half", "one "), midFlight);
        Assert.Null(running.ProgressMessage);
        Assert.Null(running.ProgressBody);
        Assert.Null(running.Progress);
    }
}
