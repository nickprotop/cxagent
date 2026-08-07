using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class PlanCompilerTests
{
    private static JsonElement Plan(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BuildDag_TwoJobs_WithDependency_MapsLocalIdsToUlids_AndWiresDeps()
    {
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"a", "name":"Step A", "type":"shell", "params":{ "command":"echo a" } },
          { "id":"b", "name":"Step B", "type":"shell", "params":{ "command":"echo b" }, "depends_on":["a"] }
        ]}
        """);
        var dag = PlanCompiler.BuildDag("goal-1", plan);

        var jobs = dag.AllJobs;   // JobDag.AllJobs is a PROPERTY (IReadOnlyList<Job>), not a method — verified
        Assert.Equal(2, jobs.Count);
        var a = jobs.Single(j => j.DisplayName == "Step A");
        var b = jobs.Single(j => j.DisplayName == "Step B");
        Assert.NotEqual("a", a.Id);          // local id 'a' mapped to a ULID
        Assert.Equal("goal-1", a.GoalId);
        Assert.Single(b.DependsOn);
        Assert.Equal(a.Id, b.DependsOn[0]);  // dependency wired to A's ULID, not the local id
    }

    [Fact]
    public void BuildDag_FoldsAJobLevelRoleIntoParams()
    {
        // P7's FINAL whole-branch review found this, after twelve per-task reviews all passed:
        // CreatePlanTool advertises `role` as a JOB-LEVEL field (beside id/name/type), but
        // LlmAgentJobPlugin reads it from JobParameters — from inside `params`. So a job-level role
        // was silently dropped and the headline feature was a coin flip on where the model put it.
        //
        // The live drive measured the cost: 3 of 4 llm_agent jobs ran with role='(none)', including
        // a goal that asked for the reviewer role by name. That was misdiagnosed as the local model
        // being weak at optional enum fields. It was this.
        var dag = PlanCompiler.BuildDag("goal-1", Plan("""
        { "summary":"x", "jobs":[
          { "id":"r1", "name":"Review", "type":"shell", "role":"reviewer",
            "params":{ "command":"echo hi" } }
        ]}
        """));

        Assert.Equal("reviewer", dag.AllJobs.Single().Parameters.Get<string>("role"));
    }

    [Fact]
    public void BuildDag_ANestedRoleWins_OverAJobLevelOne()
    {
        // Both forms are documented (the plugin's own schema describes the nested one), so an
        // explicit value inside params is the more specific instruction and must not be clobbered.
        var dag = PlanCompiler.BuildDag("goal-1", Plan("""
        { "summary":"x", "jobs":[
          { "id":"r1", "name":"Review", "type":"shell", "role":"planner",
            "params":{ "command":"echo hi", "role":"reviewer" } }
        ]}
        """));

        Assert.Equal("reviewer", dag.AllJobs.Single().Parameters.Get<string>("role"));
    }

    [Fact]
    public void BuildDag_NoRoleAnywhere_LeavesParamsUntouched()
    {
        var dag = PlanCompiler.BuildDag("goal-1", Plan("""
        { "summary":"x", "jobs":[
          { "id":"r1", "name":"Plain", "type":"shell", "params":{ "command":"echo hi" } }
        ]}
        """));

        Assert.False(dag.AllJobs.Single().Parameters.Values.ContainsKey("role"));
    }

    [Fact]
    public void Compile_PreservesThePlanLocalId()
    {
        // The orchestrator writes r1/r2; PlanCompiler rewrites them to ULIDs for DependsOn.
        // Without keeping the original, a "{{r1.content}}" reference has nothing to match —
        // which is why P7's drive wrote the literal string {{review}} to disk.
        var dag = PlanCompiler.BuildDag("goal-1", Plan("""
        {
          "summary": "two steps",
          "jobs": [
            { "id": "r1", "name": "Review it", "type": "wait", "params": { "seconds": 0 } },
            { "id": "w1", "name": "Write it",  "type": "wait", "params": { "seconds": 0 },
              "depends_on": ["r1"] }
          ]
        }
        """));

        var review = dag.AllJobs.Single(j => j.DisplayName == "Review it");
        var write  = dag.AllJobs.Single(j => j.DisplayName == "Write it");

        Assert.Equal("r1", review.PlanLocalId);
        Assert.Equal("w1", write.PlanLocalId);
        // DependsOn still holds the ULID, not the local id — the rewrite must not regress.
        Assert.Equal(review.Id, Assert.Single(write.DependsOn));
        Assert.NotEqual("r1", review.Id);
    }

    [Fact]
    public void BuildDag_DuplicateLocalId_Throws()
    {
        var plan = Plan("""
        { "jobs":[ { "id":"a","name":"A","type":"shell","params":{} },
                   { "id":"a","name":"A2","type":"shell","params":{} } ]}
        """);
        Assert.Throws<InvalidOperationException>(() => PlanCompiler.BuildDag("g", plan));
    }

    [Fact]
    public void BuildDag_DanglingDependency_Throws()
    {
        var plan = Plan("""
        { "jobs":[ { "id":"a","name":"A","type":"shell","params":{}, "depends_on":["ghost"] } ]}
        """);
        Assert.Throws<InvalidOperationException>(() => PlanCompiler.BuildDag("g", plan));
    }

    [Fact]
    public void BuildDag_LeavesGoTemplateTextAlone()
    {
        // THE constraint on this fix. `{{.State.Running}}` is docker inspect syntax, not a
        // reference: it parses to JobRef=".State" (leading dot -> not an identifier) and
        // Key="Running" (not one of our output keys). A goal that inspects a container must keep
        // working.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"check", "name":"Check", "type":"shell",
            "params":{ "command":"docker inspect -f '{{.State.Running}}' web" } }
        ]}
        """);
        var dag = PlanCompiler.BuildDag("g", plan);
        Assert.Single(dag.AllJobs);
    }

    [Fact]
    public void BuildDag_LeavesOrdinaryShellBracesAlone()
    {
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"count", "name":"Count", "type":"shell",
            "params":{ "command":"awk '{{print $1}}' /tmp/f | sed 's/{{x}}/y/'" } }
        ]}
        """);
        var dag = PlanCompiler.BuildDag("g", plan);
        Assert.Single(dag.AllJobs);
    }

    [Fact]
    public void BuildDag_AllowsAWriteWhenNoRoleIsInvolvedAtAll()
    {
        // A plain unroled goal ("read A, transform it, write it back") must be unaffected. The
        // rule triggers on a NON-WRITING ROLE being present, not on read-then-write by itself.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"read", "name":"Read", "type":"file", "params":{ "action":"read", "path":"/tmp/x.txt" } },
          { "id":"write", "name":"Write", "type":"file",
            "params":{ "action":"write", "path":"/tmp/x.txt", "content":"hello" },
            "depends_on":["read"] }
        ]}
        """);
        var dag = PlanCompiler.BuildDag("g", plan);
        Assert.Equal(2, dag.AllJobs.Count);
    }

    [Fact]
    public void BuildDag_WithPlugins_REJECTSAJobMissingARequiredParam()
    {
        // Seen live: the orchestrator planned an llm_agent job with no `prompt`. The plan compiled
        // clean, the job was dispatched, and it died with "'prompt' is required" -- a wasted
        // dispatch and a red job, where the model should simply have been asked to fix its plan.
        // ConsultJobCompiler has validated params since it was written; this compiler never did.
        // A `file` write with no `content` -- same shape of defect as the live llm_agent-without-
        // prompt, using a plugin that is present in a bare registry (llm_agent registers only when a
        // provider resolver is supplied).
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"a", "name":"Save it", "type":"file", "params":{ "action":"write", "path":"/tmp/o" } }
        ]}
        """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanCompiler.BuildDag("goal-1", plan, PluginRegistry.CreateWithBuiltins()));

        Assert.Contains("content", ex.Message);
        Assert.Contains("a", ex.Message);        // names the offending job
    }

    [Fact]
    public void BuildDag_WithPlugins_ACCEPTSAValidJob()
    {
        // The guard must not reject good plans -- it runs on EVERY initial plan.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"a", "name":"Save it", "type":"file",
            "params":{ "action":"write", "path":"/tmp/o", "content":"hello" } }
        ]}
        """);

        var dag = PlanCompiler.BuildDag("goal-1", plan, PluginRegistry.CreateWithBuiltins());

        Assert.Single(dag.AllJobs);
    }

    [Fact]
    public void BuildDag_WithPlugins_TOLERATESAPluginTheRegistryDoesNotHave()
    {
        // A registry legitimately lacks optional plugins: llm_agent registers only when a provider
        // resolver is supplied. Treating absence as a bad plan would reject every worker job
        // compiled against a bare registry -- which is exactly what a first version of this guard
        // did, breaking five existing tests.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"a", "name":"Analyze", "type":"llm_agent", "params":{ "prompt":"Review this." } }
        ]}
        """);

        var dag = PlanCompiler.BuildDag("goal-1", plan, PluginRegistry.CreateWithBuiltins());

        Assert.Single(dag.AllJobs);
    }

    [Fact]
    public void BuildDag_AWriteWithONEDependencyAndNoContent_COMPILES()
    {
        // The shape that replaces {{compile.content}}. FileJobPlugin requires `content` for a write,
        // so the compile-time plugin.Validate call has to skip this one job -- it is the only param
        // that is legally absent at plan time and supplied at run time (by JobExecutor's injection).
        // Miss that and every new-style plan is rejected before it can run.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"c", "name":"Compile", "type":"shell", "params":{ "command":"echo report" } },
          { "id":"w", "name":"Write", "type":"file", "depends_on":["c"],
            "params":{ "action":"write", "path":"/tmp/out.md" } } ]}
        """);

        var dag = PlanCompiler.BuildDag("goal-1", plan, PluginRegistry.CreateWithBuiltins());

        Assert.Equal(2, dag.AllJobs.Count);
    }

    [Fact]
    public void BuildDag_AWriteWithNODependencyAndNoContent_IsStillRejected()
    {
        // Nothing can supply the content. Rejected with a message that names the FIX -- the model
        // has just been told to omit `content` and take a dependency's output, so its mistake is the
        // missing depends_on, and the plugin's bare "'content' is required" teaches the opposite.
        var plan = Plan("""
        { "summary":"x", "jobs":[
          { "id":"w", "name":"Write", "type":"file",
            "params":{ "action":"write", "path":"/tmp/out.md" } } ]}
        """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PlanCompiler.BuildDag("goal-1", plan, PluginRegistry.CreateWithBuiltins()));

        Assert.Contains("depends_on", ex.Message);
    }
}
