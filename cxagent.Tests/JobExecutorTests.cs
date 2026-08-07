using System.Text.Json;
using CxAgent.Core.Execution;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class JobExecutorTests
{
    private static Job J(string id, string type, JobParameters p, params string[] deps) => new()
    {
        Id = id,
        GoalId = "g",
        PluginType = type,
        DisplayName = id,
        Parameters = p,
        DependsOn = new List<string>(deps),
        CreatedAt = DateTimeOffset.UtcNow
    };
    private static JobParameters P(params (string k, object? v)[] kv) => new(kv.ToDictionary(x => x.k, x => x.v));

    [Fact]
    public async Task RunJobAsync_ResolvesPluginByType_AndExecutes()
    {
        var dag = new JobDag();
        var job = J("j1", "wait", P(("seconds", 0.01)));
        dag.AddJob(job);
        var exec = new JobExecutor(PluginRegistry.CreateWithBuiltins(), dag);

        var result = await exec.RunJobAsync(job, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task RunJobAsync_UnknownPluginType_FailsImmediately()
    {
        var dag = new JobDag();
        var job = J("j1", "nonexistent", P());
        dag.AddJob(job);
        var exec = new JobExecutor(PluginRegistry.CreateWithBuiltins(), dag);

        var result = await exec.RunJobAsync(job, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("No plugin", result.ErrorMessage!);
    }

    [Fact]
    public async Task RunJobAsync_InvalidParams_FailsBeforeExecute()
    {
        var dag = new JobDag();
        var job = J("j1", "shell", P(("command", "")));   // empty command → invalid
        dag.AddJob(job);
        var exec = new JobExecutor(PluginRegistry.CreateWithBuiltins(), dag);

        var result = await exec.RunJobAsync(job, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("command", result.ErrorMessage!);
    }

    [Fact]
    public async Task RunJobAsync_ExposesDependencyResults_InCompletedJobOutputs()
    {
        // A plugin that records what it saw in CompletedJobOutputs, to prove chaining.
        var dag = new JobDag();
        var depA = J("A", "wait", P(("seconds", 0.0)));
        depA.State = JobState.Succeeded;
        depA.Result = new JobResult { Success = true, Output = new Dictionary<string, object?> { ["marker"] = "from-A" } };
        var jobB = J("B", "chain-probe", P(), "A");
        dag.AddJob(depA); dag.AddJob(jobB);

        var probe = new ChainProbePlugin();
        var reg = new PluginRegistry();
        reg.Register(probe);
        var exec = new JobExecutor(reg, dag);

        var result = await exec.RunJobAsync(jobB, CancellationToken.None);
        Assert.True(result.Success);
        Assert.True(probe.SawDependencyA, "B's context must contain A's result keyed by A's Id");
    }

    /// <summary>
    /// Task 11 — closes the resource wiring gap carried forward from Task 10: ProcessRunner raises
    /// ctx.ReportResources and JobPanelControl.UpdateResources can render it, but nothing in between
    /// joined them because JobExecutor built a bare JobContext with no sink. This pins that JobExecutor
    /// now accepts a resource callback and the JobContext it builds actually reaches it.
    /// </summary>
    [Fact]
    public async Task RunJobAsync_ReportedResources_ReachTheConfiguredSink()
    {
        var dag = new JobDag();
        var job = J("j1", "resource-probe", P());
        dag.AddJob(job);

        var seen = new List<(string JobId, ResourceSnapshot Snapshot)>();
        var exec = new JobExecutor(RegistryWith(new ResourceProbePlugin()), dag,
            onResource: (jobId, snapshot) => seen.Add((jobId, snapshot)));

        var result = await exec.RunJobAsync(job, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(seen);
        Assert.Equal("j1", seen[0].JobId);
        Assert.Equal(42.0, seen[0].Snapshot.CpuPercent);
    }

    /// <summary>
    /// P7b Task 3 — the defect this whole plan exists for. P7's live drive planned a file job whose
    /// content referenced an earlier llm_agent job; nothing substituted it, so the literal 10 bytes
    /// "{{review}}" were written to disk and the goal reported Succeeded.
    /// </summary>
    [Fact]
    public async Task RunJobAsync_TemplateBracesNamingNoDependency_ReachThePluginUnchanged()
    {
        var dag = new JobDag();
        var upstream = J("U1", "wait", P(("seconds", 0.0)));
        upstream = upstream with { PlanLocalId = "r1" };
        upstream.State = JobState.Succeeded;
        upstream.Result = new JobResult
        {
            Success = true,
            Output = new Dictionary<string, object?> { ["content"] = "REVIEW" }
        };

        const string Command = "docker inspect -f '{{.State.Running}}' c1 | awk '{{print $1}}'";
        const string Chart = "image: {{ .Values.image }}\ntag: {{ .Values.tag | default \"latest\" }}";
        // The job HAS a real dependency, so this is not the depends_on-is-empty shortcut.
        var job = J("D1", "capture", P(("text", Command), ("content", Chart)), "U1");
        dag.AddJob(upstream); dag.AddJob(job);

        var captured = new CapturingPlugin();
        var result = await new JobExecutor(RegistryWith(captured), dag)
            .RunJobAsync(job, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Command, captured.LastParameters!.Get<string>("text"));
        Assert.Equal(Chart, captured.LastParameters!.Get<string>("content"));
    }

    [Fact]
    public async Task RunJobAsync_BracesOnAJobWithNoDependencies_AreLiteralNotAFailure()
    {
        var dag = new JobDag();
        var job = J("D1", "capture", P(("text", "{{r1.content}}")));
        dag.AddJob(job);

        var captured = new CapturingPlugin();
        var result = await new JobExecutor(RegistryWith(captured), dag)
            .RunJobAsync(job, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("{{r1.content}}", captured.LastParameters!.Get<string>("text"));
    }

    [Fact]
    public async Task RunJobAsync_ParametersWithoutReferences_ArePassedThroughUnchanged()
    {
        var dag = new JobDag();
        var job = J("D1", "capture", P(("text", "no refs"), ("n", 42)));
        dag.AddJob(job);

        var captured = new CapturingPlugin();
        await new JobExecutor(RegistryWith(captured), dag).RunJobAsync(job, CancellationToken.None);

        Assert.Equal("no refs", captured.LastParameters!.Get<string>("text"));
        Assert.Equal(42, captured.LastParameters!.Get<int>("n"));   // non-string values must survive intact
    }

    /// <summary>
    /// Parameters round-trip through SQLite as JsonElement (see JobParameters' doc comment), so
    /// substitution must read string-valued JsonElements and must not flatten the rest.
    /// </summary>
    private sealed class CapturingPlugin : IJobPlugin
    {
        public JobParameters? LastParameters { get; private set; }
        public JobParameters? LastValidated { get; private set; }
        public string TypeName => "capture";
        public string DisplayName => "Capture";
        public JobSchema GetSchema() => new(TypeName, DisplayName, Array.Empty<JobParamSpec>());
        public JobValidation Validate(JobParameters p) { LastValidated = p; return JobValidation.Valid(); }
        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
        {
            LastParameters = p;
            return Task.FromResult(new JobResult { Success = true });
        }
    }

    private static PluginRegistry RegistryWith(IJobPlugin plugin)
    {
        var reg = new PluginRegistry();
        reg.Register(plugin);
        return reg;
    }

    private sealed class ResourceProbePlugin : IJobPlugin
    {
        public string TypeName => "resource-probe";
        public string DisplayName => "Resource Probe";
        public JobSchema GetSchema() => new(TypeName, DisplayName, Array.Empty<JobParamSpec>());
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
        {
            c.ReportResources(new ResourceSnapshot(42.0, 1024, DateTimeOffset.UtcNow));
            return Task.FromResult(new JobResult { Success = true });
        }
    }

    private sealed class ChainProbePlugin : IJobPlugin
    {
        public bool SawDependencyA { get; private set; }
        public string TypeName => "chain-probe";
        public string DisplayName => "Chain Probe";
        public JobSchema GetSchema() => new(TypeName, DisplayName, Array.Empty<JobParamSpec>());
        public JobValidation Validate(JobParameters p) => JobValidation.Valid();
        public Task<JobResult> ExecuteAsync(JobParameters p, IJobContext c, CancellationToken ct)
        {
            // A's result is keyed by A's ULID Id ("A" here).
            SawDependencyA = c.CompletedJobOutputs.TryGetValue("A", out var r)
                && r.Output.TryGetValue("marker", out var m) && (string?)m == "from-A";
            return Task.FromResult(new JobResult { Success = true });
        }
    }

    private static Job Produced(string id, string content, bool skipped = false)
    {
        var output = new Dictionary<string, object?> { ["content"] = content };
        if (skipped) output["skipped"] = true;
        var j = J(id, "capture", P(("text", "x")));
        j.State = skipped ? JobState.Skipped : JobState.Succeeded;
        j.Result = new JobResult { Success = true, Output = output };
        return j;
    }

    private static (JobExecutor Exec, string Path) WriteSetup(Job upstream, out Job write,
        bool withContent = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cx-{Guid.NewGuid():N}.md");
        var ps = withContent
            ? P(("action", "write"), ("path", path), ("content", "mine"))
            : P(("action", "write"), ("path", path));
        write = J("W1", "file", ps, upstream.Id);
        var dag = new JobDag();
        dag.AddJob(upstream); dag.AddJob(write);
        return (new JobExecutor(PluginRegistry.CreateWithBuiltins(), dag), path);
    }

    [Fact]
    public async Task AWriteJob_InheritsItsSingleDependencysContent()
    {
        // The replacement for {{compile.content}}: declaring the dependency IS the request for its
        // output, so there is no name for the model to get wrong.
        var (exec, path) = WriteSetup(Produced("U1", "the report body"), out var write);

        var result = await exec.RunJobAsync(write, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("the report body", File.ReadAllText(path));
        File.Delete(path);
    }

    [Fact]
    public async Task AWriteJob_WithExplicitContent_UsesItVerbatim()
    {
        // Injection fills a GAP; it never overrides what the plan actually authored.
        var (exec, path) = WriteSetup(Produced("U1", "IGNORED"), out var write, withContent: true);

        await exec.RunJobAsync(write, CancellationToken.None);

        Assert.Equal("mine", File.ReadAllText(path));
        File.Delete(path);
    }

    [Fact]
    public async Task AWriteJob_FAILSWhenItsDependencyWasSkipped()
    {
        // THE REGRESSION GUARD. A skipped job's content is "[not available — …]". Writing that to
        // disk re-creates the 18-byte AUDIT.md this change exists to prevent -- and quiet failure
        // here is precisely the trap CrewAI is documented to fall into.
        var (exec, path) = WriteSetup(
            Produced("U1", "[not available — this job was skipped because 'r' did not succeed]",
                     skipped: true), out var write);

        var result = await exec.RunJobAsync(write, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("skipped", result.ErrorMessage!);
        Assert.False(File.Exists(path));            // nothing reached disk
    }

    [Fact]
    public async Task AWriteJob_FAILSWhenItsDependencyProducedNothing()
    {
        var (exec, path) = WriteSetup(Produced("U1", ""), out var write);

        var result = await exec.RunJobAsync(write, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no content", result.ErrorMessage!);
        Assert.False(File.Exists(path));
    }
}
