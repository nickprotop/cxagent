using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

public class DagTests
{
    private static Job J(string id, params string[] deps) => new()
    {
        Id = id,
        GoalId = "g",
        PluginType = "shell",
        DisplayName = id,
        DependsOn = new List<string>(deps)
    };

    [Fact]
    public void GetReadyJobs_ReturnsOnlyPendingJobsWithAllDepsSatisfied()
    {
        var dag = new JobDag();
        var a = J("a"); var b = J("b", "a");
        dag.AddJob(a); dag.AddJob(b);

        // Initially only 'a' (no deps) is ready.
        Assert.Equal(new[] { "a" }, dag.GetReadyJobs().Select(j => j.Id));

        // Once 'a' succeeds, 'b' becomes ready.
        a.State = JobState.Succeeded;
        Assert.Equal(new[] { "b" }, dag.GetReadyJobs().Select(j => j.Id));
    }

    [Fact]
    public void GetReadyJobs_TreatsSkippedDependencyAsSatisfied()
    {
        var dag = new JobDag();
        var a = J("a"); var b = J("b", "a");
        dag.AddJob(a); dag.AddJob(b);
        a.State = JobState.Skipped;                 // skip propagates like success
        Assert.Equal(new[] { "b" }, dag.GetReadyJobs().Select(j => j.Id));
    }

    [Fact]
    public void GetDependents_ReturnsJobsThatDependOnGiven()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a")); dag.AddJob(J("c", "a"));
        Assert.Equal(new[] { "b", "c" }, dag.GetDependents("a").Select(j => j.Id).OrderBy(x => x));
    }

    [Fact]
    public void GetAncestors_ReturnsTransitiveDependencies()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a")); dag.AddJob(J("c", "b"));
        Assert.Equal(new[] { "a", "b" }, dag.GetAncestors("c").Select(j => j.Id).OrderBy(x => x));
    }

    [Fact]
    public void GetTopologicalOrder_OrdersDependenciesBeforeDependents()
    {
        var dag = new JobDag();
        dag.AddJob(J("c", "b")); dag.AddJob(J("a")); dag.AddJob(J("b", "a"));
        var order = dag.GetTopologicalOrder().Select(j => j.Id).ToList();
        Assert.True(order.IndexOf("a") < order.IndexOf("b"));
        Assert.True(order.IndexOf("b") < order.IndexOf("c"));
    }

    [Fact]
    public void Validate_DetectsCycle()
    {
        var dag = new JobDag();
        dag.AddJob(J("a", "b")); dag.AddJob(J("b", "a"));
        Assert.False(dag.Validate(out var error));
        Assert.Contains("cycle", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DetectsDanglingDependency()
    {
        var dag = new JobDag();
        dag.AddJob(J("a", "missing"));
        Assert.False(dag.Validate(out var error));
        Assert.Contains("missing", error);
    }

    [Fact]
    public void Validate_PassesForAcyclicGraph()
    {
        var dag = new JobDag();
        dag.AddJob(J("a")); dag.AddJob(J("b", "a"));
        Assert.True(dag.Validate(out var error));
        Assert.Null(error);
    }
}
