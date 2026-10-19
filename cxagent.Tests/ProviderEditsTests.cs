using System.Text.Json.Nodes;
using CxAgent.Core.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Edits over the document, not a model of it: whatever cxagent does not own — at the root or inside an
/// instance — is still there afterwards.
/// </summary>
public class ProviderEditsTests
{
    private static JsonObject Doc() => JsonNode.Parse("""
        {
          "providers": {
            "local": { "kind": "ollama", "model": "m1", "handAdded": 7 },
            "cloud": { "kind": "anthropic", "model": "claude-opus-5-5", "apiKey": "k" }
          },
          "defaultProvider": "local",
          "classifier": "cloud",
          "llmAgent": { "allowedProviders": ["local", "cloud"],
                        "routing": { "plan": { "provider": "cloud", "model": "x" } } },
          "agents": { "planner": { "provider": "cloud", "briefing": "b" } },
          "theme": "cxagent",
          "futureKey": { "a": 1 }
        }
        """)!.AsObject();

    [Fact]
    public void Update_SetsOnlyTheNamedFields_AndKeepsUnknownKeys()
    {
        var root = Doc();
        ProviderEdits.Update(root, "local", new InstanceFields { Model = "m2", Thinking = "on" });

        var local = root["providers"]!["local"]!;
        Assert.Equal("m2", (string?)local["model"]);
        Assert.Equal("on", (string?)local["thinking"]);
        Assert.Equal("ollama", (string?)local["kind"]);
        Assert.Equal(7, (int?)local["handAdded"]);
        Assert.Equal(1, (int?)root["futureKey"]!["a"]);
    }

    [Fact]
    public void Update_Clear_RemovesTheNamedKeys()
    {
        var root = Doc();
        ProviderEdits.Update(root, "cloud", new InstanceFields { Clear = new HashSet<string> { "apiKey" } });
        Assert.Null(root["providers"]!["cloud"]!["apiKey"]);
    }

    [Fact]
    public void Add_TheFirstInstance_BecomesTheDefault()
    {
        var root = new JsonObject();
        ProviderEdits.Add(root, "only", new InstanceFields { Kind = "ollama", Model = "m" });
        Assert.Equal("only", (string?)root["defaultProvider"]);
    }

    [Fact]
    public void Add_AClash_IsRefused() =>
        Assert.Throws<ConfigEditException>(() =>
            ProviderEdits.Add(Doc(), "local", new InstanceFields { Kind = "ollama", Model = "m" }));

    // EVERY REFERENCE, ONE MATRIX: a rename that missed one would leave a name the loader refuses.
    [Fact]
    public void Rename_RewritesEveryReference()
    {
        var root = Doc();
        ProviderEdits.Rename(root, "cloud", "claude");

        Assert.Null(root["providers"]!["cloud"]);
        Assert.Equal("k", (string?)root["providers"]!["claude"]!["apiKey"]);
        Assert.Equal("claude", (string?)root["classifier"]);
        Assert.Equal("claude", (string?)root["llmAgent"]!["routing"]!["plan"]!["provider"]);
        Assert.Contains("claude", root["llmAgent"]!["allowedProviders"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain("cloud", root["llmAgent"]!["allowedProviders"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("claude", (string?)root["agents"]!["planner"]!["provider"]);
        Assert.Equal("b", (string?)root["agents"]!["planner"]!["briefing"]);
    }

    [Fact]
    public void Rename_TheDefault_MovesTheDefault()
    {
        var root = Doc();
        ProviderEdits.Rename(root, "local", "here");
        Assert.Equal("here", (string?)root["defaultProvider"]);
    }

    [Fact]
    public void Remove_HandlesEveryReference()
    {
        var root = Doc();
        ProviderEdits.Remove(root, "cloud");

        Assert.Null(root["providers"]!["cloud"]);
        Assert.Null(root["classifier"]);
        Assert.Null(root["llmAgent"]!["routing"]!["plan"]);
        Assert.DoesNotContain("cloud", root["llmAgent"]!["allowedProviders"]!.AsArray().Select(n => (string?)n));
        Assert.Null(root["agents"]!["planner"]!["provider"]);
        Assert.Equal("b", (string?)root["agents"]!["planner"]!["briefing"]);
    }

    [Fact]
    public void Remove_TheDefault_PassesItToTheFirstRemainingByName()
    {
        var root = Doc();
        ProviderEdits.Remove(root, "local");
        Assert.Equal("cloud", (string?)root["defaultProvider"]);
    }

    [Fact]
    public void SetReviewer_Null_ClearsIt()
    {
        var root = Doc();
        ProviderEdits.SetReviewer(root, null);
        Assert.Null(root["classifier"]);
    }

    [Fact]
    public void Rename_MovesEveryFormOfAReference()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""
        { "providers": { "local": {}, "fast": {} },
          "agents": { "a": { "provider": ["fast", "local:qwen"] }, "b": { "provider": "local" } } }
        """)!.AsObject();

        ProviderEdits.Rename(root, "local", "box");

        Assert.Equal("""["fast","box:qwen"]""", root["agents"]!["a"]!["provider"]!.ToJsonString());
        Assert.Equal("box", (string?)root["agents"]!["b"]!["provider"]);
    }

    [Fact]
    public void Remove_DropsTheEntry_AndTheKeyWhenTheListEmpties()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""
        { "providers": { "local": {}, "fast": {} }, "defaultProvider": "fast",
          "agents": { "a": { "provider": ["local", "fast"] }, "b": { "provider": ["local:qwen"] } } }
        """)!.AsObject();

        ProviderEdits.Remove(root, "local");

        Assert.Equal("""["fast"]""", root["agents"]!["a"]!["provider"]!.ToJsonString());
        Assert.False(root["agents"]!["b"]!.AsObject().ContainsKey("provider"));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("rename")]
    public void ANewProviderName_CannotHoldAColon(string how)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("""{ "providers": { "local": {} } }""")!.AsObject();

        var ex = Assert.Throws<ConfigEditException>(() =>
        {
            if (how == "add") ProviderEdits.Add(root, "my:box", new InstanceFields());
            else ProviderEdits.Rename(root, "local", "my:box");
        });
        Assert.Contains("':'", ex.Message, StringComparison.Ordinal);
    }
}
