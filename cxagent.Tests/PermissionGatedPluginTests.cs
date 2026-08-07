using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Task 2: the twin-path closure. Both execution paths — JobExecutor.cs:84 (planned jobs) and
/// WorkerToolset.cs:147 (worker tool calls) — dispatch through the SAME registry, and this
/// registry wraps shell/file/http in <see cref="PermissionGatedPlugin"/> before either caller
/// ever sees them. Neither caller changes; that is the property under test.
/// </summary>
public class PermissionGatedPluginTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-perm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JobParameters Params(params (string k, object? v)[] kv) =>
        new(kv.ToDictionary(x => x.k, x => x.v));

    private static Job J(string id, string type, JobParameters p) => new()
    {
        Id = id,
        GoalId = "g",
        PluginType = type,
        DisplayName = id,
        Parameters = p,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static JobDag DagWithOneShellJob(string command)
    {
        var dag = new JobDag();
        dag.AddJob(J("j1", "shell", Params(("command", command))));
        return dag;
    }

    private static ToolCall ToolCall(string name, string jsonArgs) => new()
    {
        Name = name,
        Arguments = JsonSerializer.Deserialize<JsonElement>(jsonArgs),
        Id = "call-1",
    };

    private static Job FailedJobWithRetryHeadroom() => new()
    {
        Id = "j", GoalId = "g", PluginType = "shell", DisplayName = "j",
        State = JobState.Failed, RetryCount = 0, MaxRetries = 3,
    };

    [Fact]
    public async Task PlannedJobPath_ADeniedShellCommand_NeverExecutes()
    {
        // Chokepoint 1 of 2 (JobExecutor.cs:84). The marker file is the proof: a gate that runs
        // the command and then reports failure would still have created it.
        var marker = Path.Combine(TempDir(), "ran.txt");
        var dag = DagWithOneShellJob($"touch {marker}");
        var registry = PluginRegistry.CreateWithBuiltins(null, PermissionGate.DenyAll);
        var executor = new JobExecutor(registry, dag);

        var result = await executor.RunJobAsync(dag.AllJobs[0], CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.False(File.Exists(marker));
        Assert.Contains("denied by the user", result.ErrorMessage);
    }

    [Fact]
    public async Task WorkerToolPath_ADeniedWrite_ComesBackAsARefusalTheModelCanRead()
    {
        // Chokepoint 2 of 2 (WorkerToolset.cs:147) — the path D7/D8/D11/D12 each forgot once.
        // Same registry, ZERO WorkerToolset changes: the wrapper sits below both callers.
        var target = Path.Combine(TempDir(), "out.txt");
        var registry = PluginRegistry.CreateWithBuiltins(null, PermissionGate.DenyAll);
        var call = ToolCall("write_file", $$"""{"path": "{{target}}", "content": "x"}""");

        var text = await WorkerToolset.InvokeAsync(call,
            new[] { WorkerTool.WriteFile }, registry, new TestJobContext(), CancellationToken.None);

        Assert.False(File.Exists(target));
        Assert.Contains("denied by the user", text);     // routable refusal, not an opaque crash
        Assert.Contains("Do not retry", text);
    }

    [Fact]
    public async Task AnAllowedRequest_ExecutesExactlyAsBefore()
    {
        var file = Path.Combine(TempDir(), "in.txt");
        File.WriteAllText(file, "hello");
        var registry = PluginRegistry.CreateWithBuiltins(null, PermissionGate.AllowAll);
        registry.TryGet("file", out var plugin);

        var result = await plugin!.ExecuteAsync(
            Params(("action", "read"), ("path", file)), new TestJobContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Output["content"]);
    }

    [Fact]
    public void OnlyTheThreeRiskyPlugins_AreWrapped()
    {
        // llm_agent gated would double-prompt (its TOOLS are already gated one level down);
        // wait gated would be noise. The wrapper set is exactly shell|file|http.
        var registry = PluginRegistry.CreateWithBuiltins(null, PermissionGate.DenyAll);
        foreach (var name in new[] { "shell", "file", "http" })
        {
            registry.TryGet(name, out var p);
            Assert.IsType<PermissionGatedPlugin>(p);
        }
        registry.TryGet("wait", out var wait);
        Assert.IsNotType<PermissionGatedPlugin>(wait);
    }

    [Fact]
    public void ShouldAutoDiagnose_SkipsAUserDenial()
    {
        // Diagnosis exists to repair broken jobs. "The user said no" is not broken, and a paid
        // diagnosis round cannot change their mind.
        var job = FailedJobWithRetryHeadroom();
        job.Result = new JobResult { Success = false, PermissionDenied = true };
        Assert.False(CxAgent.UI.GoalRunner.ShouldAutoDiagnose(job));
    }
}
