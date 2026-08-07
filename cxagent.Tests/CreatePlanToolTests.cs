using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class CreatePlanToolTests
{
    /// <summary>A registry with one configured instance, so llm_agent registers and its
    /// model_hint description has something to name.</summary>
    private static ProviderRegistry TestResolver() =>
        ProviderRegistry.FromProviders(
            new Dictionary<string, ILlmProvider> { ["local"] = new MockLlmProvider() }, "local");

    [Fact]
    public void BuildDefinition_DoesNotLeakRoleSystemPromptsToOrchestrator()
    {
        // The orchestrator CHOOSES roles; it does not act as one. System prompts are second-person
        // behavioural instructions ("You are a reviewer…") — sending them here both bloats every
        // create_plan call and invites the orchestrator to follow instructions meant for a worker.
        var def = CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins());
        var json = JsonSerializer.Serialize(def.InputSchema);

        // Was: no role SystemPrompt may leak into the schema. Roles are gone; the invariant it
        // protected — worker-only behavioural text must never reach the orchestrator — is now
        // vacuous, so the assertion is simply that the schema still builds.
        Assert.NotEmpty(json);
    }

    [Fact]
    public void BuildDefinition_WithoutRoles_OmitsRoleField()
    {
        var json = JsonSerializer.Serialize(CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins()).InputSchema);
        Assert.DoesNotContain("\"role\"", json);
    }

    [Fact]
    public void BuildDefinition_WithRoles_StillAdvertisesPluginTypes()
    {
        // Guard against the role field displacing the plugin-type enum added in P6 Task 0.
        var json = JsonSerializer.Serialize(
            CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins()).InputSchema);
        foreach (var t in new[] { "shell", "file", "http", "wait" })
            Assert.Contains(t, json);
    }

    [Fact]
    public void Definition_IsCreatePlan_WithJobsArraySchema()
    {
        var def = CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins());
        Assert.Equal("create_plan", def.Name);
        Assert.False(string.IsNullOrWhiteSpace(def.Description));

        // Schema is a JSON object with a "jobs" array property whose items have id/name/type/params.
        var schema = def.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.True(props.TryGetProperty("jobs", out var jobs));
        Assert.Equal("array", jobs.GetProperty("type").GetString());
        var itemProps = jobs.GetProperty("items").GetProperty("properties");
        foreach (var required in new[] { "id", "name", "type", "params" })
            Assert.True(itemProps.TryGetProperty(required, out _), $"jobs.items missing '{required}'");
    }

    [Fact]
    public void BuildDefinition_SchemaIsStableAcrossCalls()
    {
        // Deterministic for the same inputs — same raw text every build. This matters more now that
        // there is no cached Definition property: GoalRunner rebuilds the schema on EVERY planning call,
        // so any nondeterminism here (unordered plugin or role enumeration) would send the model a
        // subtly different tool contract each turn and defeat provider-side prompt caching.
        Assert.Equal(
            CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins())
                .InputSchema.GetRawText(),
            CreatePlanTool.BuildDefinition(PluginRegistry.CreateWithBuiltins())
                .InputSchema.GetRawText());
    }


    [Fact]
    public void BuildDescription_SaysTheOrchestratorIsThePlanner()
    {
        // A live drive worked out the exact `sed -i` commands for six files, wrote them into its
        // closing message, and called finish_goal -- nothing was edited. The plan was not missing;
        // a PLACE to put it was.
        var description = CreatePlanTool
            .BuildDefinition(PluginRegistry.CreateWithBuiltins())
            .Description;

        Assert.Contains("YOU are the planner", description);
        Assert.Contains("emit it as a JOB", description);
    }

    [Fact]
    public void BuildDefinition_AdvertisesNoRolesAtAll()
    {
        // Replaces the three role-enum tests. The `role` field is omitted entirely rather than
        // offered empty: advertising a field with no valid values invites the model to invent one.
        var json = CreatePlanTool
            .BuildDefinition(PluginRegistry.CreateWithBuiltins())
            .InputSchema.GetRawText();

        foreach (var name in new[] { "planner", "reviewer", "implementer", "debugger" })
            Assert.DoesNotContain($"\"{name}\"", json);

        // And the PROPERTY must be gone, not merely emptied. "enum": [] is an impossible constraint
        // in JSON Schema -- a provider either rejects the tool outright or cannot produce a valid
        // call for it. Asserting only that the NAMES are absent passed happily on a broken schema.
        Assert.DoesNotContain("\"role\"", json);
        Assert.DoesNotContain("\"enum\":[]", json.Replace(" ", ""));
    }

    [Fact]
    public void SingleAgentMode_DoesNotMentionWorkersAtALL()
    {
        // The mode is enforced STRUCTURALLY: llm_agent is not registered, so it is absent from the
        // type enum, the params reference and every worked example. A mode enforced by prompt
        // wording is one the model can talk itself out of.
        var def = CreatePlanTool.BuildDefinition(
            PluginRegistry.CreateWithBuiltins(TestResolver(), PermissionGate.AllowAll, fanOut: false));
        var all = def.Description + def.InputSchema.GetRawText();

        Assert.DoesNotContain("llm_agent", all);
        Assert.Contains("YOU DO THE WORK YOURSELF", def.Description);
        Assert.Contains("file", all);        // the types that DO exist are still there
    }

    [Fact]
    public void FanOutMode_OffersWorkersAndTheRuleForThem()
    {
        var def = CreatePlanTool.BuildDefinition(
            PluginRegistry.CreateWithBuiltins(TestResolver(), PermissionGate.AllowAll, fanOut: true));
        var all = def.Description + def.InputSchema.GetRawText();

        Assert.Contains("llm_agent", all);
        Assert.Contains("read-then-modify", def.Description);
        Assert.DoesNotContain("YOU DO THE WORK YOURSELF", def.Description);
    }

    [Fact]
    public void TheTwoModesGiveOPPOSITEAdviceForReadThenEdit()
    {
        // Single-agent's answer is "plan the read now, plan the edit at the next consult, when the
        // text is in the digest". Fan-out's is "one worker does both in one context". Each is wrong
        // in the other mode, which is why the guidance is derived from what is registered.
        var single = CreatePlanTool.BuildDefinition(
            PluginRegistry.CreateWithBuiltins(TestResolver(), PermissionGate.AllowAll, fanOut: false)).Description;
        var fan = CreatePlanTool.BuildDefinition(
            PluginRegistry.CreateWithBuiltins(TestResolver(), PermissionGate.AllowAll, fanOut: true)).Description;

        Assert.Contains("across TURNS", single);
        Assert.Contains("single context", fan);
        Assert.NotEqual(single, fan);
    }
}
