using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class SqliteGoalStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;

    public SqliteGoalStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-store-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections; clear so the file can be deleted on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Goal MakeGoal(string id, GoalState state = GoalState.Active) => new()
    {
        Id = id,
        Description = "test goal",
        State = state,
        CreatedAt = DateTimeOffset.UtcNow,
        ProviderId = "mock"
    };

    private static Job MakeJob(string id, string goalId, JobState state = JobState.Pending) => new()
    {
        Id = id,
        GoalId = goalId,
        PluginType = "shell",
        DisplayName = "Step " + id,
        // Set on the SHARED factory, not only in the test that names it: a hand-built Job that omits
        // a field never exercises the production shape, which is how plan_local_id reached main
        // unpersisted while the suite stayed green.
        PlanLocalId = "r_" + id,
        Parameters = new JobParameters(new() { ["command"] = "echo hi", ["count"] = 42 }),
        DependsOn = new List<string> { "dep1" },
        State = state,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SaveAndLoad_Goal_RoundTripsAllFields()
    {
        var store = new SqliteGoalStore(_paths);
        var goal = MakeGoal("g1", GoalState.Completed);
        goal.CompletedAt = DateTimeOffset.UtcNow;
        await store.SaveGoalAsync(goal);

        // Fresh store instance on the same file — proves durability, not in-memory state.
        var store2 = new SqliteGoalStore(_paths);
        var loaded = await store2.GetGoalAsync("g1");

        Assert.NotNull(loaded);
        Assert.Equal("g1", loaded!.Id);
        Assert.Equal("test goal", loaded.Description);
        Assert.Equal(GoalState.Completed, loaded.State);
        Assert.Equal("mock", loaded.ProviderId);
        Assert.NotNull(loaded.CompletedAt);
    }

    [Fact]
    public async Task SaveAndLoad_Job_RoundTripsParams_AsConvertibleJsonElements()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        await store.SaveJobAsync(MakeJob("j1", "g1", JobState.Succeeded));

        var store2 = new SqliteGoalStore(_paths);
        var jobs = await store2.GetJobsForGoalAsync("g1");

        var j = Assert.Single(jobs);
        Assert.Equal("j1", j.Id);
        Assert.Equal(JobState.Succeeded, j.State);
        Assert.Equal(new List<string> { "dep1" }, j.DependsOn);
        Assert.Equal("r_j1", j.PlanLocalId);
        // The load-bearing reuse: params come back as JsonElement, Get<T> converts them.
        Assert.Equal("echo hi", j.Parameters.Get<string>("command"));
        Assert.Equal(42, j.Parameters.Get<int>("count"));
    }

    /// <summary>
    /// PlanLocalId is the id the orchestrator's own plan used ("r1") and the only name a
    /// {{r1.content}} reference can be resolved by — DisplayName and the raw ULID are not what the
    /// model writes. ResumeService rebuilds interrupted goals from exactly these rows, so a column
    /// that is written but never read (or never written) breaks every reference across a restart
    /// while the whole suite stays green. Assert the survival directly, and assert it survives an
    /// UPDATE too: the upsert branch is a separate column list from the INSERT.
    /// </summary>
    [Fact]
    public async Task SaveAndLoad_Job_PlanLocalIdSurvivesInsertAndUpdate()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        var job = MakeJob("j1", "g1", JobState.Queued);
        job = job with { PlanLocalId = "r1" };
        await store.SaveJobAsync(job);

        var afterInsert = (await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1")).Single();
        Assert.Equal("r1", afterInsert.PlanLocalId);

        job.State = JobState.Succeeded;
        await store.SaveJobAsync(job);      // second save takes the ON CONFLICT DO UPDATE branch

        var afterUpdate = (await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1")).Single();
        Assert.Equal("r1", afterUpdate.PlanLocalId);
    }

    [Fact]
    public async Task SaveAndLoad_Job_WithoutAPlanLocalId_LoadsAsNullNotEmpty()
    {
        // JobExecutor keys on `is { Length: > 0 }`, and a recovery-inserted job has no plan-local id
        // at all. "" and null must not be confused on the way back out.
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        await store.SaveJobAsync(MakeJob("j1", "g1") with { PlanLocalId = null });

        var loaded = (await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1")).Single();
        Assert.Null(loaded.PlanLocalId);
    }

    /// <summary>
    /// The C2 defect end to end: a goal is planned, persisted, and reloaded as if the process had
    /// restarted, and a downstream job still receives its dependency's output. The unit assertions
    /// above pin the column; this pins the behaviour that column exists for.
    ///
    /// <para>Was asserted through a {{r1.content}} reference until that syntax was removed. The
    /// column is still load-bearing — dependency wiring survives a reload by plan-local id — so the
    /// test is re-expressed through injection rather than deleted.</para>
    /// </summary>
    [Fact]
    public async Task ReloadedJobs_StillReceiveTheirDependencysOutput()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));

        var upstream = MakeJob("U1", "g1", JobState.Succeeded) with
        {
            PlanLocalId = "r1",
            DependsOn = new List<string>(),
        };
        upstream.Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?> { ["content"] = "REVIEW BODY" },
        };
        var downstream = MakeJob("D1", "g1", JobState.Queued) with
        {
            PlanLocalId = "w1",
            DependsOn = new List<string> { "U1" },
            PluginType = "file",
            Parameters = new JobParameters(new()
                { ["action"] = "write", ["path"] = Path.Combine(Path.GetTempPath(), $"cx-reload-{Guid.NewGuid():N}.txt") }),
        };

        await store.SaveJobAsync(upstream);
        await store.SaveJobAsync(downstream);

        // Fresh store, fresh DAG — exactly what ResumeService builds at startup.
        var reloaded = await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1");
        var dag = new CxAgent.Core.Orchestrator.JobDag();
        foreach (var j in reloaded) dag.AddJob(j);

        var registry = new CxAgent.Core.Plugins.PluginRegistry();
        var capturing = new PlanLocalIdCapturingPlugin();
        registry.Register(capturing);
        var executor = new CxAgent.Core.Execution.JobExecutor(registry, dag);

        var result = await executor.RunJobAsync(reloaded.Single(j => j.Id == "D1"), CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("REVIEW BODY", capturing.LastParameters!.Get<string>("content"));
    }

    private sealed class PlanLocalIdCapturingPlugin : CxAgent.Core.Plugins.IJobPlugin
    {
        public JobParameters? LastParameters { get; private set; }
        public string TypeName => "file";
        public string DisplayName => "Capture";
        public CxAgent.Core.Plugins.JobSchema GetSchema() =>
            new(TypeName, DisplayName, Array.Empty<CxAgent.Core.Plugins.JobParamSpec>());
        public CxAgent.Core.Plugins.JobValidation Validate(JobParameters parameters) =>
            CxAgent.Core.Plugins.JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters parameters,
            CxAgent.Core.Plugins.IJobContext context, CancellationToken ct)
        {
            LastParameters = parameters;
            return Task.FromResult(new JobResult { Success = true, ExitCode = 0 });
        }
    }

    [Fact]
    public async Task SaveJob_WithResult_RoundTripsResultOutput()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        var job = MakeJob("j1", "g1", JobState.Succeeded);
        job.Result = new JobResult { Success = true, ExitCode = 0, Output = new() { ["image_tag"] = "v2.1" } };
        await store.SaveJobAsync(job);

        var loaded = (await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1")).Single();
        Assert.NotNull(loaded.Result);
        Assert.True(loaded.Result!.Success);
        // Output values also round-trip as JsonElement.
        Assert.Equal("v2.1", ((JsonElement)loaded.Result.Output["image_tag"]!).GetString());
    }

    [Fact]
    public async Task SaveJob_Twice_IsIdempotentUpsert_OneRowLatestState()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        var job = MakeJob("j1", "g1", JobState.Queued);
        await store.SaveJobAsync(job);
        job.State = JobState.Running; await store.SaveJobAsync(job);
        job.State = JobState.Succeeded; await store.SaveJobAsync(job);

        var jobs = await store.GetJobsForGoalAsync("g1");
        var j = Assert.Single(jobs);           // one row, not three
        Assert.Equal(JobState.Succeeded, j.State);
    }

    [Fact]
    public async Task DeleteGoal_CascadesToJobs_ProvingForeignKeysOn()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        await store.SaveJobAsync(MakeJob("j1", "g1"));

        await store.DeleteGoalAsync("g1");

        Assert.Null(await store.GetGoalAsync("g1"));
        Assert.Empty(await store.GetJobsForGoalAsync("g1")); // cascade removed the job
    }

    [Fact]
    public async Task ListGoalsByState_FiltersToRequestedStates()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1", GoalState.Active));
        await store.SaveGoalAsync(MakeGoal("g2", GoalState.Completed));
        await store.SaveGoalAsync(MakeGoal("g3", GoalState.Draft));

        var resumable = await store.ListGoalsByStateAsync(GoalState.Active, GoalState.Draft);
        Assert.Equal(new[] { "g1", "g3" }, resumable.Select(g => g.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task SaveAndLoad_Conversation_PreservesOrderAndFields()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        await store.SaveChatMessageAsync("g1", new ChatMessage { Role = "user", Content = "hi", Timestamp = DateTimeOffset.UtcNow });
        await store.SaveChatMessageAsync("g1", new ChatMessage { Role = "assistant", Content = "hello", Timestamp = DateTimeOffset.UtcNow });

        var convo = await new SqliteGoalStore(_paths).GetConversationAsync("g1");
        Assert.Equal(2, convo.Count);
        Assert.Equal("user", convo[0].Role);
        Assert.Equal("hi", convo[0].Content);
        Assert.Equal("assistant", convo[1].Role);
    }

    [Fact]
    public async Task GetConversation_DropsDanglingToolResult_WhenToolUseMissing()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        // A tool-result whose matching tool-use ("call-1") was never persisted.
        await store.SaveChatMessageAsync("g1", new ChatMessage { Role = "user", Content = "start", Timestamp = DateTimeOffset.UtcNow });
        await store.SaveChatMessageAsync("g1", new ChatMessage { Role = "tool", Content = "result data", ToolCallId = "call-1", Timestamp = DateTimeOffset.UtcNow });

        var convo = await store.GetConversationAsync("g1");
        Assert.Single(convo);                    // the dangling tool-result was dropped
        Assert.Equal("user", convo[0].Role);
    }

    [Fact]
    public async Task GetConversation_KeepsToolResult_WhenMatchingToolUsePresent()
    {
        var store = new SqliteGoalStore(_paths);
        await store.SaveGoalAsync(MakeGoal("g1"));
        var toolUse = new ChatMessage
        {
            Role = "assistant",
            Content = "calling",
            Timestamp = DateTimeOffset.UtcNow,
            ToolCalls = new List<ToolCall> { new() { Name = "create_plan", Id = "call-1", Arguments = JsonSerializer.SerializeToElement(new { }) } }
        };
        await store.SaveChatMessageAsync("g1", toolUse);
        await store.SaveChatMessageAsync("g1", new ChatMessage { Role = "tool", Content = "ok", ToolCallId = "call-1", Timestamp = DateTimeOffset.UtcNow });

        var convo = await store.GetConversationAsync("g1");
        Assert.Equal(2, convo.Count);            // both kept — the tool-use is present
    }

    [Fact]
    public async Task SaveJob_RoundTripsOrchestratorEditCount()
    {
        // THE THIRD TIME this project has had a field silently dropped by persistence (PlanLocalId was
        // the second). A cap that resets on restart is not a cap — an interrupted goal would resume
        // with a fresh edit budget for every job.
        var store = new SqliteGoalStore(_paths);
        var goalId = "g1";
        await store.SaveGoalAsync(MakeGoal(goalId));
        var job = MakeJob("j1", goalId);
        job.OrchestratorEditCount = 2;   // settable, like RetryCount — the loop mutates the live instance
        await store.SaveJobAsync(job);

        var reloaded = (await store.GetJobsForGoalAsync(job.GoalId)).Single(j => j.Id == job.Id);
        Assert.Equal(2, reloaded.OrchestratorEditCount);
    }
}
