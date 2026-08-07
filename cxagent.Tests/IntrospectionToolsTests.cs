using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class IntrospectionToolsTests
{
    private static Job MakeJob(string planLocalId, string id, string name, JobState state,
        List<string>? dependsOn = null, double? progress = null, string? progressMessage = null,
        JobResult? result = null, DateTimeOffset? startedAt = null) => new()
    {
        Id = id,
        PlanLocalId = planLocalId,
        GoalId = "g",
        PluginType = "shell",
        DisplayName = name,
        State = state,
        DependsOn = dependsOn ?? new(),
        Progress = progress,
        ProgressMessage = progressMessage,
        Result = result,
        StartedAt = startedAt,
    };

    private static JobDag ThreeJobDag()
    {
        var dag = new JobDag();
        dag.AddJob(MakeJob("a", "01A", "Job A", JobState.Succeeded,
            result: new JobResult { Success = true, Output = new() { ["content"] = "ok" } }));
        dag.AddJob(MakeJob("b", "01B", "Job B", JobState.Running, dependsOn: new() { "01A" }));
        dag.AddJob(MakeJob("c", "01C", "Job C", JobState.Pending, dependsOn: new() { "01B" }));
        return dag;
    }

    private static JobDag DagWithOutput(string content)
    {
        var dag = new JobDag();
        dag.AddJob(MakeJob("r1", "01R", "Job R", JobState.Succeeded,
            result: new JobResult { Success = true, Output = new() { ["content"] = content } }));
        return dag;
    }

    private static JobDag DagWithRunningJob(double progress, string message)
    {
        var dag = new JobDag();
        dag.AddJob(MakeJob("r1", "01R", "Job R", JobState.Running,
            progress: progress, progressMessage: message, startedAt: DateTimeOffset.UtcNow));
        return dag;
    }

    [Fact]
    public void ListJobs_ReportsEveryJobAndItsState()
    {
        var text = IntrospectionTools.ListJobs(ThreeJobDag());
        foreach (var name in new[] { "Job A", "Job B", "Job C" }) Assert.Contains(name, text);
        Assert.Contains("Running", text);
        Assert.Contains("Pending", text);
    }

    [Fact]
    public void GetJobOutput_ReturnsAWindow_AndReportsTheTotalSize()
    {
        // Large results live in FILES; the orchestrator pages rather than being handed megabytes.
        var text = IntrospectionTools.GetJobOutput(DagWithOutput(new string('x', 10_000)), "r1", offset: 0, limit: 100);
        Assert.True(text.Length < 500);
        Assert.Contains("10,000", text);   // total size still visible
    }

    [Fact]
    public void GetJobStatus_WorksOnARUNNINGJob()
    {
        // The whole point: "what is this job doing right now". A status that only works post-mortem
        // answers nothing.
        var text = IntrospectionTools.GetJobStatus(DagWithRunningJob(progress: 0.42, message: "step 3/7"), "r1");
        Assert.Contains("Running", text);
        Assert.Contains("42", text);
        Assert.Contains("step 3/7", text);
    }

    [Fact]
    public void GetJobOutput_UnknownJobId_SaysSoRatherThanThrowing()
    {
        var text = IntrospectionTools.GetJobOutput(ThreeJobDag(), "nope", 0, 100);
        Assert.Contains("nope", text);
        Assert.Contains("not", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetJobStatus_UnknownJobId_SaysSoRatherThanThrowing()
    {
        var text = IntrospectionTools.GetJobStatus(ThreeJobDag(), "nope");
        Assert.Contains("nope", text);
        Assert.Contains("not", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetJobStatus_OnAFinishedJob_ReusesJobDigestRendering()
    {
        var dag = DagWithOutput("all done");
        var digest = JobDigest.From(dag.TryGet("01R")!).Render();
        var status = IntrospectionTools.GetJobStatus(dag, "r1");
        Assert.Equal(digest, status);
    }

    [Fact]
    public void GetJobOutput_NoOutputYet_OnARunningJob_SaysSoRatherThanEmptyString()
    {
        var text = IntrospectionTools.GetJobOutput(DagWithRunningJob(0.1, "starting"), "r1", 0, 100);
        Assert.Contains("running", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListJobs_ShowsDependenciesByPlanLocalId()
    {
        var text = IntrospectionTools.ListJobs(ThreeJobDag());
        Assert.Contains("depends on: a", text);
        Assert.Contains("depends on: b", text);
    }
}
