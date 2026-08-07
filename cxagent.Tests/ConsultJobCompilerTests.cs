using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class ConsultJobCompilerTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A ConsultModification carrying only jobs_to_add (the compile-hard half).</summary>
    private static ConsultModification Add(string jobsJson) =>
        new(Json(jobsJson), new Dictionary<string, JsonElement>());

    private static Job Succeeded(string id, string planLocalId) => new()
    {
        Id = id,
        PlanLocalId = planLocalId,
        GoalId = "g1",
        PluginType = "wait",
        DisplayName = planLocalId,
        CreatedAt = DateTimeOffset.UtcNow,
        State = JobState.Succeeded,
    };

    private static JobDag DagWith(params Job[] jobs)
    {
        var dag = new JobDag();
        foreach (var j in jobs) dag.AddJob(j);
        return dag;
    }

    private static JobDag EmptyDag() => new();

    [Fact]
    public void TryCompile_ResolvesADependencyOnAJobThatALREADYRAN()
    {
        // THE point of this task. PlanCompiler cannot do it: it resolves depends_on only within its own
        // payload. A consult-added job almost always depends on the job whose output prompted the edit.
        var live = DagWith(Succeeded("U1", planLocalId: "review"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1", Add(
            "[ { \"id\": \"save\", \"name\": \"Save it\", \"type\": \"file\", " +
            "    \"depends_on\": [\"review\"], " +
            "    \"params\": { \"action\": \"write\", \"path\": \"/tmp/o.md\" } } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        var save = Assert.Single(result.JobsToAdd);
        Assert.Equal("save", save.PlanLocalId);            // so {{save.x}} resolves later
        Assert.Equal("g1", save.GoalId);                   // NOT "" — JobDiagnoser's bug
        Assert.NotEqual("save", save.Id);                  // a real ULID, not the local id
        Assert.Equal("U1", Assert.Single(save.DependsOn)); // wired to the EXISTING job's ULID
    }

    [Fact]
    public void TryCompile_ResolvesADependencyOnAnotherNEWJobInTheSameBatch()
    {
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"type\": \"wait\", \"params\": { \"seconds\": 0 } }, " +
            "  { \"id\": \"b\", \"name\": \"B\", \"type\": \"wait\", \"params\": { \"seconds\": 0 }, \"depends_on\": [\"a\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        var a = result.JobsToAdd.Single(j => j.PlanLocalId == "a");
        var b = result.JobsToAdd.Single(j => j.PlanLocalId == "b");
        Assert.Equal(a.Id, Assert.Single(b.DependsOn));
    }

    [Fact]
    public void TryCompile_UnknownDependency_FailsWithAnActionableError()
    {
        // Must NOT throw — the loop reports and continues rather than dying mid-goal.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"b\", \"name\": \"B\", \"type\": \"wait\", \"params\": { \"seconds\": 0 }, \"depends_on\": [\"ghost\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("ghost", error!);
    }

    [Fact]
    public void TryCompile_InvalidParams_FailBeforeAnythingIsAddedToTheDag()
    {
        // Validate through the plugin, as JobDiagnoser does. A half-added batch would leave the live dag
        // inconsistent with what the orchestrator was told.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"type\": \"file\", \"params\": { \"path\": \"/tmp/x\" } } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(result.JobsToAdd);
    }

    [Fact]
    public void TryCompile_APlanLocalIdThatCOLLIDESWithAnExistingOne_IsRejected()
    {
        // Two jobs sharing a PlanLocalId makes {{that_id.content}} ambiguous, and JobExecutor's
        // jobRefToId map would silently keep only one of them.
        var live = DagWith(Succeeded("U1", planLocalId: "review"));
        var ok = ConsultJobCompiler.TryCompile(live, "g1", Add(
            "[ { \"id\": \"review\", \"name\": \"Again\", \"type\": \"wait\", \"params\": { \"seconds\": 0 } } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("review", error!);
    }

    // --- Beyond the brief: the batch's own duplicates, the unknown-type case, and the
    //     ParameterChanges half of the translation the addendum handed us. ---

    [Fact]
    public void TryCompile_TwoNEWJobsSharingALocalId_IsRejected()
    {
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"type\": \"wait\", \"params\": { \"seconds\": 0 } }, " +
            "  { \"id\": \"a\", \"name\": \"A again\", \"type\": \"wait\", \"params\": { \"seconds\": 0 } } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("a", error!);
    }

    [Fact]
    public void TryCompile_UnknownPluginType_FailsRatherThanThrows()
    {
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"type\": \"quantum\", \"params\": {} } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("quantum", error!);
    }

    [Fact]
    public void TryCompile_MalformedJob_MissingType_FailsRatherThanThrows()
    {
        // The model omitted a required field. GetProperty would throw KeyNotFoundException, which
        // would kill the goal — the one thing this compiler must never do.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"params\": {} } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCompile_JobsToAddThatIsNotAnArray_FailsRatherThanThrows()
    {
        var mod = new ConsultModification(Json("{ \"oops\": true }"), new Dictionary<string, JsonElement>());
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", mod,
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryCompile_NoJobsToAdd_SucceedsWithAnEmptyModification()
    {
        var mod = new ConsultModification(null, new Dictionary<string, JsonElement>());
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", mod,
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Empty(result.JobsToAdd);
        Assert.Empty(result.ParameterChanges);
        Assert.Empty(result.JobIdsToRemove);
    }

    [Fact]
    public void TryCompile_ParameterChanges_KeepValuesAsJsonElement_NotBlindCast()
    {
        // JobParameters' contract: untyped values stay JsonElement and Get<T> converts. Storing a
        // pre-cast object here would diverge from every other construction path in the codebase.
        var live = DagWith(Succeeded("U1", planLocalId: "review"));
        var mod = new ConsultModification(null, new Dictionary<string, JsonElement>
        {
            ["U1"] = Json("{ \"seconds\": 5, \"note\": \"hi\" }"),
        });

        var ok = ConsultJobCompiler.TryCompile(live, "g1", mod,
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        var changed = result.ParameterChanges["U1"];
        Assert.IsType<JsonElement>(changed.Values["seconds"]);
        Assert.Equal(5, changed.Get<int>("seconds"));
        Assert.Equal("hi", changed.Get<string>("note"));
    }

    [Fact]
    public void TryCompile_ParameterChangesForAnUnknownJob_IsRejected()
    {
        var mod = new ConsultModification(null, new Dictionary<string, JsonElement>
        {
            ["nope"] = Json("{ \"seconds\": 5 }"),
        });

        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", mod,
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("nope", error!);
    }

    [Fact]
    public void TryCompile_ADependencyNamedByRawUlid_AlsoResolves()
    {
        // The digest shows the orchestrator real job ids; it may name one directly rather than by
        // plan-local id. Falling back to a raw Job.Id match costs nothing and avoids a confusing failure.
        var live = DagWith(Succeeded("U1", planLocalId: "review"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1", Add(
            "[ { \"id\": \"save\", \"name\": \"S\", \"type\": \"wait\", \"params\": { \"seconds\": 0 }, " +
            "    \"depends_on\": [\"U1\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal("U1", Assert.Single(Assert.Single(result.JobsToAdd).DependsOn));
    }

    [Fact]
    public void TryCompile_JobLevelRole_IsFoldedIntoParams_AsPlanCompilerDoes()
    {
        // Same trap PlanCompiler documents: `role` is advertised job-level but read from params.
        // A consult-added llm_agent job would silently lose its role otherwise.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"a\", \"name\": \"A\", \"type\": \"wait\", \"role\": \"reviewer\", " +
            "    \"params\": { \"seconds\": 0 } } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal("reviewer", Assert.Single(result.JobsToAdd).Parameters.Get<string>("role"));
    }

    [Fact]
    public void TryCompile_DoesNotMutateTheLiveDag()
    {
        // The compiler compiles; DagModifier applies. If TryCompile added jobs itself, a later
        // DagModifier failure could not roll them back.
        var live = DagWith(Succeeded("U1", planLocalId: "review"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1", Add(
            "[ { \"id\": \"save\", \"name\": \"S\", \"type\": \"wait\", \"params\": { \"seconds\": 0 }, " +
            "    \"depends_on\": [\"review\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Single(live.AllJobs);
        Assert.Single(result.JobsToAdd);
    }
    [Fact]
    public void TryCompile_TemplateSyntaxNamingNoJob_IsLeftAlone()
    {
        // The counterweight, and the reason the check is narrow. P7b's C1 fix exists because
        // {{ is the delimiter of Go templates, Jinja, Handlebars and Vue — docker inspect -f
        // '{{.State.Running}}' and helm charts must keep working. None of these name a job, so the
        // new check must not fire.
        var live = DagWith(Succeeded("U1", "review1"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1",
            new ConsultModification(Json(
                "[ { \"id\": \"inspect\", \"name\": \"Inspect\", \"type\": \"shell\", " +
                "    \"params\": { \"command\": \"docker inspect -f '{{.State.Running}}' c1\" } } ]"),
                new Dictionary<string, JsonElement>()),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Single(result.JobsToAdd);
    }

    [Fact]
    public void TryCompile_ReferenceWITHTheDependency_IsAccepted()
    {
        var live = DagWith(Succeeded("U1", "review1"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1",
            new ConsultModification(Json(
                "[ { \"id\": \"save\", \"name\": \"Save\", \"type\": \"file\", " +
                "    \"depends_on\": [\"review1\"], " +
                "    \"params\": { \"action\": \"write\", \"path\": \"/tmp/o.md\" } } ]"),
                new Dictionary<string, JsonElement>()),
            PluginRegistry.CreateWithBuiltins(), out var result, out var error);

        Assert.True(ok, error);
        Assert.Equal("U1", Assert.Single(Assert.Single(result.JobsToAdd).DependsOn));
    }

    [Fact]
    public void TryCompile_AllowsAReviewerToWriteANEWFile()
    {
        // The user's explicit carve-out: "sometimes a reviewer must write a test file. or create
        // something in /tmp." Writing something the plan never read is not editing the work
        // under review, and must keep working.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"read\", \"name\": \"Read\", \"type\": \"file\", " +
            "    \"params\": { \"action\": \"read\", \"path\": \"/tmp/buggy.cs\" } }, " +
            "  { \"id\": \"review\", \"name\": \"Review\", \"type\": \"wait\", \"role\": \"reviewer\", " +
            "    \"params\": { \"seconds\": 0 }, \"depends_on\": [\"read\"] }, " +
            "  { \"id\": \"notes\", \"name\": \"Notes\", \"type\": \"file\", " +
            "    \"params\": { \"action\": \"write\", \"path\": \"/tmp/review-notes.md\" }, " +
            "    \"depends_on\": [\"review\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.True(ok, error);
    }

    [Fact]
    public void TryCompile_AllowsAWriteWhenNoRoleIsInvolvedAtAll()
    {
        // A plain unroled goal ("read A, transform it, write it back") must be unaffected. The
        // rule triggers on a NON-WRITING ROLE being present, not on read-then-write by itself.
        var ok = ConsultJobCompiler.TryCompile(EmptyDag(), "g1", Add(
            "[ { \"id\": \"read\", \"name\": \"Read\", \"type\": \"file\", " +
            "    \"params\": { \"action\": \"read\", \"path\": \"/tmp/x.txt\" } }, " +
            "  { \"id\": \"write\", \"name\": \"Write\", \"type\": \"file\", " +
            "    \"params\": { \"action\": \"write\", \"path\": \"/tmp/x.txt\", \"content\": \"hello\" }, " +
            "    \"depends_on\": [\"read\"] } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.True(ok, error);
    }

    [Fact]
    public void TryCompile_ACollidingIdIsToldBothWaysToFixIt()
    {
        // Hit repeatedly on live fan-outs ('read_hex', 'find_files'): the orchestrator proposes a new
        // job reusing the id of one that ALREADY RAN. Two opposite intentions land here -- wanting a
        // genuinely new job, or wanting to build on the existing one -- so the error has to offer
        // both corrections. A bare "choose a different id" pushes the second case toward a renamed
        // duplicate of work that is already done.
        var live = DagWith(Succeeded("U1", planLocalId: "read_hex"));

        var ok = ConsultJobCompiler.TryCompile(live, "g1", Add(
            "[ { \"id\": \"read_hex\", \"name\": \"Read it again\", \"type\": \"wait\", " +
            "    \"params\": { \"seconds\": 0 } } ]"),
            PluginRegistry.CreateWithBuiltins(), out _, out var error);

        Assert.False(ok);
        Assert.Contains("read_hex_2", error);        // the "I meant a new job" correction
        Assert.Contains("depends_on", error);        // the "I meant to use its result" correction
    }
}
