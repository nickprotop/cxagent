using System.Text.Json;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class DiagnoseToolTests
{
    private static JsonElement Schema() =>
        DiagnoseTool.BuildDefinition(PluginRegistry.CreateWithBuiltins()).InputSchema;

    [Fact]
    public void Definition_IsNamedSuggestRecovery()
    {
        Assert.Equal("suggest_recovery", DiagnoseTool.Definition.Name);
        Assert.False(string.IsNullOrWhiteSpace(DiagnoseTool.Definition.Description));
    }

    [Fact]
    public void RequiredFields_AreCauseActionRationale()
    {
        var required = Schema().GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.Contains("cause", required);
        Assert.Contains("action", required);
        Assert.Contains("rationale", required);
    }

    /// <summary>
    /// The wire enum and the C# RecoveryAction enum must stay in lockstep: a value the model can emit
    /// but JobDiagnoser cannot map is a silent dead end at runtime.
    /// </summary>
    [Fact]
    public void ActionEnum_MatchesRecoveryActionMembers_Exactly()
    {
        var wire = Schema().GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToHashSet();

        Assert.Equal(
            new HashSet<string> { "retry", "modify_and_retry", "insert_before", "skip", "ask_user" },
            wire);
        // One wire value per RecoveryAction member — no more, no fewer.
        Assert.Equal(Enum.GetValues<RecoveryAction>().Length, wire.Count);
    }

    [Fact]
    public void JobsToInsert_ConstrainsTypeToRegisteredPlugins()
    {
        // Same lesson as CreatePlanTool: a job the model invents cannot be executed, so the type must
        // be an enum of what the registry actually has rather than a free string.
        var json = Schema().GetRawText();
        foreach (var t in new[] { "shell", "file", "http", "wait" })
            Assert.Contains($"\"{t}\"", json);
    }

    [Fact]
    public void ParameterChanges_AreOptional()
    {
        var required = Schema().GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();
        Assert.DoesNotContain("parameter_changes", required);
        Assert.DoesNotContain("jobs_to_insert", required);
    }
}
