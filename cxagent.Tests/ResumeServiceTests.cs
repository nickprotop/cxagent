using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class ResumeServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppPaths _paths;
    private readonly SqliteGoalStore _store;

    public ResumeServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cxagent-resume-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dir);
        _paths.EnsureCreated();
        _store = new SqliteGoalStore(_paths);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Goal G(string id, GoalState s) => new() { Id = id, Description = "g", State = s, CreatedAt = DateTimeOffset.UtcNow, ProviderId = "mock" };
    private Job J(string id, string gid, JobState s) => new() { Id = id, GoalId = gid, PluginType = "shell", DisplayName = id, State = s, CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Resume_ReconcilesJobStates_PerSpec()
    {
        await _store.SaveGoalAsync(G("g1", GoalState.Active));
        await _store.SaveJobAsync(J("running", "g1", JobState.Running));
        await _store.SaveJobAsync(J("paused", "g1", JobState.Paused));
        await _store.SaveJobAsync(J("queued", "g1", JobState.Queued));
        await _store.SaveJobAsync(J("done", "g1", JobState.Succeeded));

        var resumed = await new ResumeService(_store).ResumeAsync();

        var rg = Assert.Single(resumed);
        Assert.Equal("g1", rg.Goal.Id);
        Assert.Equal(JobState.Failed, rg.Dag.TryGet("running")!.State);          // Running -> Failed
        Assert.Equal("interrupted by app shutdown", rg.Dag.TryGet("running")!.Result!.ErrorMessage);
        Assert.Equal(JobState.Paused, rg.Dag.TryGet("paused")!.State);           // Paused stays
        Assert.Equal(JobState.Queued, rg.Dag.TryGet("queued")!.State);           // Queued stays
        Assert.Contains("queued", rg.QueuedJobIdsToReprime);                     // flagged for re-prime
        Assert.Equal(JobState.Succeeded, rg.Dag.TryGet("done")!.State);          // unchanged
    }

    [Fact]
    public async Task Resume_RePersistsReconciledStates()
    {
        await _store.SaveGoalAsync(G("g1", GoalState.Active));
        await _store.SaveJobAsync(J("running", "g1", JobState.Running));

        await new ResumeService(_store).ResumeAsync();

        // Load again from a fresh store — the Running->Failed reconciliation must be durable.
        var jobs = await new SqliteGoalStore(_paths).GetJobsForGoalAsync("g1");
        Assert.Equal(JobState.Failed, jobs.Single().State);
    }

    [Fact]
    public async Task Resume_IgnoresTerminalGoals()
    {
        await _store.SaveGoalAsync(G("done", GoalState.Completed));
        await _store.SaveGoalAsync(G("active", GoalState.Active));

        var resumed = await new ResumeService(_store).ResumeAsync();
        Assert.Equal(new[] { "active" }, resumed.Select(r => r.Goal.Id));   // only Active/Draft
    }

    [Fact]
    public async Task Resume_RestoresDraftGoal_WithoutReconcilingJobs()
    {
        await _store.SaveGoalAsync(G("draft", GoalState.Draft));
        await _store.SaveJobAsync(J("p", "draft", JobState.Pending));

        var resumed = await new ResumeService(_store).ResumeAsync();
        var rg = Assert.Single(resumed);
        Assert.Equal(GoalState.Draft, rg.Goal.State);
        Assert.Equal(JobState.Pending, rg.Dag.TryGet("p")!.State);          // untouched (scheduler inert in Draft)
        Assert.Empty(rg.QueuedJobIdsToReprime);
    }
}
