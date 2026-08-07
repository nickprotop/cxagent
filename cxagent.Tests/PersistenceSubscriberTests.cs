using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class PersistenceSubscriberTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private readonly SqliteGoalStore _store;

    public PersistenceSubscriberTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-sub-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _store = new SqliteGoalStore(_paths);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Attach_PersistsJobsAndGoal_AsAnOrchestrationRuns()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            summary = "demo",
            jobs = new object[]
            {
                new { id = "a", name = "A", type = "shell", @params = new { } },
                new { id = "b", name = "B", type = "shell", @params = new { }, depends_on = new[] { "a" } },
            }
        }));

        // Pre-create the goal row so the subscriber can update its state (see Task 6 note).
        var goal = new Goal { Id = "g1", Description = "demo", State = GoalState.Active, CreatedAt = DateTimeOffset.UtcNow, ProviderId = "mock" };
        await _store.SaveGoalAsync(goal);

        var orch = new Orchestrator(mock, runJob: (job, ct) => Task.FromResult(new JobResult { Success = true, ExitCode = 0 }));

        var subscriber = new PersistenceSubscriber(_store);
        subscriber.Attach(orch, goal);

        await orch.StartGoalAsync("demo", CancellationToken.None);
        await subscriber.DrainAsync();

        // A fresh store loads the persisted run: both jobs Succeeded.
        var store2 = new SqliteGoalStore(_paths);
        var jobs = await store2.GetJobsForGoalAsync(goal.Id);
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
    }

    [Fact]
    public async Task Attach_UpdatesGoalState_OnGoalStateChanged()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(LlmResponse.WithToolCall("create_plan", new
        {
            summary = "s",
            jobs = new object[] { new { id = "a", name = "A", type = "shell", @params = new { } } }
        }));
        var goal = new Goal { Id = "g1", Description = "s", State = GoalState.Active, CreatedAt = DateTimeOffset.UtcNow, ProviderId = "mock" };
        await _store.SaveGoalAsync(goal);

        var orch = new Orchestrator(mock, runJob: (j, ct) => Task.FromResult(new JobResult { Success = true }));
        var subscriber = new PersistenceSubscriber(_store);
        subscriber.Attach(orch, goal);
        await orch.StartGoalAsync("s", CancellationToken.None);
        await subscriber.DrainAsync();

        var loaded = await new SqliteGoalStore(_paths).GetGoalAsync("g1");
        Assert.Equal(GoalState.Completed, loaded!.State);   // goal state was persisted on change
    }

    [Fact]
    public async Task DisposeAsync_CompletesTheWorker_WithoutHanging()
    {
        var subscriber = new PersistenceSubscriber(_store);
        // Dispose with no writes pending — must return promptly, not hang.
        await subscriber.DisposeAsync();
        // A second drain after completion must also not hang.
        await subscriber.DrainAsync();   // guarded: returns immediately when writer is completed
    }
}
