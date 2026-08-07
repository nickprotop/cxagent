using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class ConsultToolTests
{
    /// <summary>Builds a ToolCall from an anonymous object, the way a real provider reply would
    /// arrive. LlmResponse.WithToolCall is the shortest route to a JsonElement Arguments payload.</summary>
    private static ToolCall Call(object args) =>
        LlmResponse.WithToolCall("consult", args).ToolCalls[0];

    [Fact]
    public void BuildDefinition_DocumentsAllThreeActions()
    {
        var json = JsonSerializer.Serialize(
            ConsultTool.BuildDefinition(PluginRegistry.CreateWithBuiltins()).InputSchema);

        foreach (var action in new[] { "continue", "modify", "finish_goal" })
            Assert.Contains(action, json);
    }

    [Fact]
    public void BuildDefinition_ShowsAWorkedExampleForModify()
    {
        // Three times in this project the model invented syntax because only a RULE was given
        // (plugin types, the role enum, {{...}} references). A worked example is not optional.
        var json = JsonSerializer.Serialize(
            ConsultTool.BuildDefinition(PluginRegistry.CreateWithBuiltins()).InputSchema);

        Assert.Contains("\\u0022action\\u0022: \\u0022modify\\u0022", json);
        Assert.Contains("jobs_to_add", json);
    }

    [Fact]
    public void BuildDefinition_ExampleUsesRealPluginParamNames()
    {
        // A worked example that names a param no plugin accepts is WORSE than prose — the model
        // follows it faithfully and every job it plans then fails validation. This happened once
        // already ("operation" vs FileJobPlugin's "action"), so cross-check against the live schema.
        var registry = PluginRegistry.CreateWithBuiltins();
        var json = JsonSerializer.Serialize(ConsultTool.BuildDefinition(registry).InputSchema);

        foreach (var plugin in registry.All)
            foreach (var p in plugin.GetSchema().Params.Where(p => p.Required))
                if (json.Contains($"\\u0022{plugin.TypeName}\\u0022"))
                    Assert.Contains($"\\u0022{p.Name}\\u0022", json);
    }

    [Fact]
    public void Parse_ContinueNeedsNothingElse()
    {
        // The cheap path. If `continue` required a rationale or a summary the model would pay to say
        // nothing, which is exactly what this design is trying to avoid.
        var d = ConsultTool.Parse(Call(new { action = "continue" }));
        Assert.NotNull(d);
        Assert.Equal(ConsultAction.Continue, d!.Action);
        Assert.Null(d.Modification);
    }

    [Fact]
    public void Parse_FinishGoalCarriesItsSummary()
    {
        var d = ConsultTool.Parse(Call(new { action = "finish_goal", summary = "All three reviews agree." }));
        Assert.Equal(ConsultAction.FinishGoal, d!.Action);
        Assert.Equal("All three reviews agree.", d.Summary);
    }

    [Fact]
    public void Parse_UnknownAction_ReturnsNull_RatherThanGuessing()
    {
        // Never infer intent from a malformed decision: guessing "modify" from a half-parsed payload
        // could add jobs the user never authorised. The caller reports and continues.
        Assert.Null(ConsultTool.Parse(Call(new { action = "wat" })));
        Assert.Null(ConsultTool.Parse(Call(new { })));
    }

    [Fact]
    public void Parse_CancelIdsAreCarried()
    {
        var d = ConsultTool.Parse(Call(new { action = "modify", cancel_job_ids = new[] { "r3" } }));
        Assert.Equal(new[] { "r3" }, d!.CancelJobIds);
    }
}
