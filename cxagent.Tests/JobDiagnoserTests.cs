using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class JobDiagnoserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-diag-" + Guid.NewGuid().ToString("N"));
    private readonly LogFileManager _logs;

    public JobDiagnoserTests()
    {
        Directory.CreateDirectory(_dir);
        var paths = new AppPaths(_dir); paths.EnsureCreated();
        _logs = new LogFileManager(paths);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static Job FailedJob(string id = "j1") => new()
    {
        Id = id, GoalId = "g1", PluginType = "shell", DisplayName = "Run tests",
        State = JobState.Failed, CreatedAt = DateTimeOffset.UtcNow,
        Result = new JobResult { Success = false, ExitCode = 1, ErrorMessage = "exit 1" },
    };

    private static JobDag DagWith(params Job[] jobs)
    {
        var dag = new JobDag();
        foreach (var j in jobs) dag.AddJob(j);
        return dag;
    }

    private static LlmResponse Suggestion(string action, string cause = "tests failed", object? extra = null) =>
        LlmResponse.WithToolCall("suggest_recovery", extra ?? new
        {
            cause,
            action,
            rationale = "the failure looks transient",
        });

    private JobDiagnoser Make(MockLlmProvider mock) =>
        new(mock, PluginRegistry.CreateWithBuiltins(), _logs);

    [Fact]
    public async Task ValidSuggestion_MapsToAiDiagnosis()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Suggestion("retry"));
        var job = FailedJob();

        var d = await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(RecoveryAction.Retry, d!.Action);
        Assert.Equal("tests failed", d.Cause);
        Assert.False(string.IsNullOrWhiteSpace(d.Rationale));
    }

    [Fact]
    public async Task UnknownPluginType_TriggersOneCorrectionRound_ThenSucceeds()
    {
        var mock = new MockLlmProvider();
        // Round 1: insert_before naming a plugin that does not exist.
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "apt", @params = new { } } },
        }));
        // Round 2: corrected to a real plugin.
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "shell", @params = new { command = "apt install -y dep" } } },
        }));
        var job = FailedJob();

        var d = await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(RecoveryAction.InsertBefore, d!.Action);
        Assert.Single(d.Modification!.JobsToAdd);
        Assert.Equal("shell", d.Modification.JobsToAdd[0].PluginType);
    }

    [Fact]
    public async Task TwoBadRounds_ReturnsNull_RatherThanLoopingForever()
    {
        var mock = new MockLlmProvider();
        for (int i = 0; i < 3; i++)   // more than the cap, to prove it stops at 2
            mock.EnqueueResponse(Suggestion("insert_before", extra: new
            {
                cause = "c", action = "insert_before", rationale = "r",
                jobs_to_insert = new[] { new { id = "x", name = "n", type = "nope", @params = new { } } },
            }));
        var job = FailedJob();

        var d = await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.Null(d);
    }

    [Fact]
    public async Task InvalidParams_TriggersOneCorrectionRound_ThenSucceeds()
    {
        var mock = new MockLlmProvider();
        // Round 1: a real plugin type ("shell") but missing its required 'command' param.
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "shell", @params = new { } } },
        }));
        // Round 2: corrected with a valid 'command'.
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "shell", @params = new { command = "apt install -y dep" } } },
        }));
        var job = FailedJob();

        var d = await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        // Must have succeeded via the correction path (not the never-throws path, which also returns null).
        Assert.NotNull(d);
        Assert.Equal(RecoveryAction.InsertBefore, d!.Action);
        Assert.Single(d.Modification!.JobsToAdd);
        Assert.Equal("apt install -y dep", d.Modification.JobsToAdd[0].Parameters.Get<string>("command"));
    }

    [Fact]
    public async Task ThreeRoundsOfInvalidParams_ReturnsNull_RatherThanLoopingForever()
    {
        var mock = new MockLlmProvider();
        for (int i = 0; i < 3; i++)   // more than the cap, to prove the widened check stays bounded too
            mock.EnqueueResponse(Suggestion("insert_before", extra: new
            {
                cause = "c", action = "insert_before", rationale = "r",
                jobs_to_insert = new[] { new { id = "x", name = "n", type = "shell", @params = new { } } },
            }));
        var job = FailedJob();

        var d = await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.Null(d);
    }

    [Fact]
    public async Task ProviderThrows_ReturnsNull_NeverPropagates()
    {
        var job = FailedJob();
        // MockLlmProvider with an EMPTY queue throws InvalidOperationException("Queue empty.") on Dequeue.
        var d = await Make(new MockLlmProvider()).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);
        Assert.Null(d);
    }

    [Fact]
    public async Task Diagnosing_DoesNotConsumeRetryHeadroom()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Suggestion("retry"));
        var job = FailedJob();
        var before = job.RetryCount;

        await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        // Spec: only EXECUTING a retry decrements headroom; analysing a failure must not.
        Assert.Equal(before, job.RetryCount);
    }

    /// <summary>
    /// Task 11 review I3: diagnosis rounds spend real tokens against the provider but were recorded
    /// nowhere, making them invisible to both the status-bar readout and the goal token budget.
    /// onUsage is the opt-in hook AppBootstrap uses to route diagnosis spend into the same
    /// TokenLedger a goal's own planning call uses.
    /// </summary>
    [Fact]
    public async Task DiagnoseJobAsync_ReportsUsage_ToTheOptionalCallback()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Suggestion("retry") with { Usage = new LlmUsage { InputTokens = 50, OutputTokens = 20 } });
        var job = FailedJob();

        var seen = new List<LlmUsage>();
        var diagnoser = new JobDiagnoser(mock, PluginRegistry.CreateWithBuiltins(), _logs,
            onUsage: usage => seen.Add(usage));

        await diagnoser.DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.Single(seen);
        Assert.Equal(70, seen[0].InputTokens + seen[0].OutputTokens);
    }

    /// <summary>Each correction round is a separate provider call and separate spend — both rounds
    /// must be reported, not just the first or the last.</summary>
    [Fact]
    public async Task DiagnoseJobAsync_ReportsUsage_ForEveryCorrectionRound()
    {
        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "apt", @params = new { } } },
        }) with { Usage = new LlmUsage { InputTokens = 10, OutputTokens = 5 } });
        mock.EnqueueResponse(Suggestion("insert_before", extra: new
        {
            cause = "missing dep", action = "insert_before", rationale = "install it first",
            jobs_to_insert = new[] { new { id = "x", name = "Install", type = "shell", @params = new { command = "x" } } },
        }) with { Usage = new LlmUsage { InputTokens = 11, OutputTokens = 6 } });
        var job = FailedJob();

        var seen = new List<LlmUsage>();
        var diagnoser = new JobDiagnoser(mock, PluginRegistry.CreateWithBuiltins(), _logs,
            onUsage: usage => seen.Add(usage));

        var d = await diagnoser.DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        Assert.NotNull(d);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task Prompt_IncludesTheJobsRecentLogOutput()
    {
        var job = FailedJob();
        await _logs.AppendAsync(job.GoalId, job.Id, "log", "line-one\nSENTINEL-FAILURE-TEXT\nline-three\n");

        var mock = new MockLlmProvider();
        mock.EnqueueResponse(Suggestion("retry"));

        await Make(mock).DiagnoseJobAsync(job, DagWith(job), CancellationToken.None);

        // The diagnoser must actually feed the job's output to the model — without it the LLM is
        // guessing from a job name alone.
        var sent = mock.LastMessages!;
        Assert.Contains(sent, m => m.Content.Contains("SENTINEL-FAILURE-TEXT"));
    }
}
