using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using Xunit;

namespace CxAgent.Tests;

public class DagModifierTests
{
    private static Job J(string id, params string[] deps)
    {
        var j = new Job
        {
            Id = id, GoalId = "g", PluginType = "shell", DisplayName = id,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var d in deps) j.DependsOn.Add(d);
        return j;
    }

    private static JobDag Dag(params Job[] jobs)
    {
        var d = new JobDag();
        foreach (var j in jobs) d.AddJob(j);
        return d;
    }

    [Fact]
    public void ParameterChange_LandsOnTheLiveJob()
    {
        var a = J("a");
        a.Parameters.Values["command"] = "echo old";
        var dag = Dag(a);

        var newParams = new JobParameters();
        newParams.Values["command"] = "echo new";
        var mod = new DagModification(
            Array.Empty<Job>(), Array.Empty<string>(),
            new Dictionary<string, JobParameters> { ["a"] = newParams });

        Assert.True(DagModifier.TryApply(dag, mod, insertBeforeJobId: null, out var err));
        Assert.Null(err);
        Assert.Equal("echo new", dag.TryGet("a")!.Parameters.Get<string>("command"));
    }

    [Fact]
    public void InsertBefore_RewiresTheTargetToDependOnTheInsertedJob()
    {
        var a = J("a");
        var b = J("b", "a");            // b depends on a
        var dag = Dag(a, b);

        var setup = J("setup");
        var mod = new DagModification(new[] { setup }, Array.Empty<string>(),
            new Dictionary<string, JobParameters>());

        Assert.True(DagModifier.TryApply(dag, mod, insertBeforeJobId: "b", out var err));
        Assert.Null(err);

        // b must now wait for setup, and setup must be in the graph.
        Assert.NotNull(dag.TryGet("setup"));
        Assert.Contains("setup", dag.TryGet("b")!.DependsOn);
        // b's original dependency is preserved — inserting must not orphan the existing edge.
        Assert.Contains("a", dag.TryGet("b")!.DependsOn);
    }

    [Fact]
    public void ModificationIntroducingACycle_IsRejected_AndTheDagIsUnchanged()
    {
        var a = J("a");
        var b = J("b", "a");
        var dag = Dag(a, b);

        // Inserting a job that depends on b, before a, closes the loop a -> ... -> b -> a.
        var loop = J("loop", "b");
        var mod = new DagModification(new[] { loop }, Array.Empty<string>(),
            new Dictionary<string, JobParameters>());

        Assert.False(DagModifier.TryApply(dag, mod, insertBeforeJobId: "a", out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));

        // ROLLBACK: the rejected job must be gone and a's edges untouched.
        Assert.Null(dag.TryGet("loop"));
        Assert.DoesNotContain("loop", dag.TryGet("a")!.DependsOn);
        Assert.Equal(2, dag.AllJobs.Count);
    }

    [Fact]
    public void RemovingAJobOthersDependOn_IsRejected_AndTheDagIsUnchanged()
    {
        var a = J("a");
        var b = J("b", "a");
        var dag = Dag(a, b);

        var mod = new DagModification(Array.Empty<Job>(), new[] { "a" },
            new Dictionary<string, JobParameters>());

        Assert.False(DagModifier.TryApply(dag, mod, insertBeforeJobId: null, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
        Assert.NotNull(dag.TryGet("a"));               // still there
        Assert.Contains("a", dag.TryGet("b")!.DependsOn);
        Assert.Equal(2, dag.AllJobs.Count);
    }

    [Fact]
    public void EmptyModification_IsANoOp_AndSucceeds()
    {
        var dag = Dag(J("a"));
        Assert.True(DagModifier.TryApply(dag, DagModification.Empty, null, out var err));
        Assert.Null(err);
        Assert.Single(dag.AllJobs);
    }
}
