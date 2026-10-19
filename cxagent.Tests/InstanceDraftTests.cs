using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// An instance being edited: only what changed is written, the key is never read back, and the draft
/// knows which running sessions its save will move.
/// </summary>
public class InstanceDraftTests
{
    private static readonly ProviderInstanceConfig Local =
        new("ollama", "qwen", null, "http://localhost:11434", null, Thinking: "on");

    [Fact]
    public void AnUntouchedDraft_IsClean_AndWritesNothing()
    {
        var fields = new InstanceDraft("local", Local).ToFields();
        Assert.False(new InstanceDraft("local", Local).IsDirty);
        Assert.Null(fields.Model);
        Assert.Null(fields.BaseUrl);
        Assert.Null(fields.Thinking);
        Assert.Empty(fields.Clear);
    }

    [Fact]
    public void ChangingTheModel_IsTheOnlyFieldWritten()
    {
        var draft = new InstanceDraft("local", Local) { Model = "qwen2" };
        Assert.True(draft.IsDirty);
        var fields = draft.ToFields();
        Assert.Equal("qwen2", fields.Model);
        Assert.Null(fields.BaseUrl);
        Assert.Null(fields.Thinking);
    }

    [Fact]
    public void SettingAModelBackToWhatItWas_IsClean()
    {
        var draft = new InstanceDraft("local", Local) { Model = "qwen2" };
        draft.Model = "qwen";
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void TheKey_IsNeverReadBack()
    {
        var draft = new InstanceDraft("claude", new ProviderInstanceConfig("anthropic", "m", "sk-secret", null, null));
        Assert.Null(draft.NewApiKey);
        Assert.Equal("set · …cret", SecretField.Describe("sk-secret"));
        Assert.Equal("none", SecretField.Describe(null));
    }

    [Fact]
    public void ClearingTheKey_WritesAClear()
    {
        var draft = new InstanceDraft("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)) { ClearApiKey = true };
        Assert.Contains("apiKey", draft.ToFields().Clear);
    }

    [Fact]
    public void ThinkingBackToTheModelsDefault_WritesAClear()
    {
        var draft = new InstanceDraft("local", Local) { Thinking = null };
        Assert.Contains("thinking", draft.ToFields().Clear);
    }

    [Fact]
    public void AnOrdinaryEdit_MovesOnlyThatInstancesTabs()
    {
        var change = new InstanceDraft("local", Local) { Model = "x" }.ChangeFor(Settings("other"));
        Assert.False(change.Everyone);
        Assert.Contains("local", change.Instances);
    }

    [Fact]
    public void TheReviewer_MovesEveryone() =>
        Assert.True(new InstanceDraft("local", Local) { Model = "x" }
            .ChangeFor(Settings("other") with { Classifier = "local" }).Everyone);

    [Fact]
    public void TheDefaultsConcurrencyLimit_MovesEveryone() =>
        Assert.True(new InstanceDraft("local", Local) { MaxConcurrentAgents = 3 }.ChangeFor(Settings("local")).Everyone);

    [Fact]
    public void AnInstanceAnAgentTypeNames_MovesEveryone() =>
        Assert.True(new InstanceDraft("local", Local) { Model = "x" }.ChangeFor(Settings("other") with
        {
            AgentTypes = new Dictionary<string, AgentTypeConfig> { ["planner"] = new(Provider: "local") },
        }).Everyone);

    // WHAT A TAB'S CACHE DOES NOT SURVIVE is known by the edit: a new model or server, not a new key.
    [Fact]
    public void ANewModelOrEndpoint_BreaksTheCache_ANewKeyDoesNot()
    {
        Assert.Contains("local", new InstanceDraft("local", Local) { Model = "x" }.ChangeFor(Settings("other")).ModelChanged);
        Assert.Contains("local", new InstanceDraft("local", Local) { BaseUrl = "http://elsewhere:1/v1" }.ChangeFor(Settings("other")).ModelChanged);
        Assert.Empty(new InstanceDraft("local", Local) { NewApiKey = "k2" }.ChangeFor(Settings("other")).ModelChanged);
    }

    private static ProviderSettings Settings(string defaultName) => new(
        new Dictionary<string, ProviderInstanceConfig> { ["local"] = Local, ["other"] = Local },
        defaultName, [], new Dictionary<string, RoutingTarget>());

    /// <summary>A type's catalog holds each listed provider resolved: a second entry's new model or key must
    /// reach every session, or a child that picks it keeps calling the old one.</summary>
    [Fact]
    public void EditingATypesSecondProvider_ReachesEveryone() =>
        Assert.True(new InstanceDraft("other", Local) { Model = "x" }.ChangeFor(Settings("local") with
        {
            AgentTypes = new Dictionary<string, AgentTypeConfig>
            {
                ["planner"] = new(Provider: "local") { Providers = ["local", "other"], Written = ["local", "other"] },
            },
        }).Everyone);
}
