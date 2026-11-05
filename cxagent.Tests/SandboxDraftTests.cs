using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>F8 › General's sandbox section: what it reads from config.json and what a save writes back.</summary>
public class SandboxDraftTests
{
    private static readonly Dictionary<string, string> NoEnv = new();
    private const string Providers = """ "providers": { "local": { "kind": "ollama", "model": "m" } }, "defaultProvider": "local" """;

    private static GeneralDraft Draft(string extra = "") =>
        GeneralDraft.From(ProviderConfigLoader.Parse($$"""{ {{Providers}} {{extra}} }""", NoEnv));

    private static JsonObject Saved(GeneralDraft draft, string extra = "")
    {
        var root = JsonNode.Parse($$"""{ {{Providers}} {{extra}} }""")!.AsObject();
        draft.Apply(root);
        return root;
    }

    [Fact]
    public void An_untouched_draft_is_clean_and_writes_nothing()
    {
        var draft = Draft();
        Assert.False(draft.IsDirty);
        Assert.Null(Saved(draft)["sandbox"]);
    }

    [Fact]
    public void Turning_it_off_writes_enabled_false()
    {
        var draft = Draft();
        draft.SandboxEnabled = false;
        Assert.True(draft.IsDirty);
        Assert.False(Saved(draft)["sandbox"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Back_to_the_defaults_removes_the_block()
    {
        var draft = Draft(""", "sandbox": { "enabled": false } """);
        draft.SandboxEnabled = true;
        Assert.Null(Saved(draft, """, "sandbox": { "enabled": false } """)["sandbox"]);
    }

    [Fact]
    public void Draft_round_trips_every_field()
    {
        var draft = Draft();
        draft.SandboxNetwork = false;
        // ONE PER LINE, so a comma in a path is part of the path.
        draft.WritableText = "~/out\n/data/a,b";
        draft.HiddenText = "~/.secret";
        draft.HiddenEnvText = "MY_*";
        draft.PassEnvText = "NPM_TOKEN";
        var root = Saved(draft);

        var back = GeneralDraft.From(ProviderConfigLoader.Parse(root.ToJsonString(), NoEnv));
        Assert.False(back.SandboxNetwork);
        Assert.Equal("~/out\n/data/a,b", back.WritableText);
        Assert.Equal("~/.secret", back.HiddenText);
        Assert.Equal("MY_*", back.HiddenEnvText);
        Assert.Equal("NPM_TOKEN", back.PassEnvText);
        Assert.False(back.IsDirty);
    }

    [Fact]
    public void Draft_keeps_unknown_keys()
    {
        var draft = Draft(""", "sandbox": { "future": 1 } """);
        draft.SandboxNetwork = false;
        var sandbox = Saved(draft, """, "sandbox": { "future": 1 } """)["sandbox"]!;
        Assert.Equal(1, sandbox["future"]!.GetValue<int>());
        Assert.False(sandbox["network"]!.GetValue<bool>());
    }

    [Fact]
    public void Set_enabled_writes_only_that_key()
    {
        var root = JsonNode.Parse($$"""{ {{Providers}}, "sandbox": { "network": false } }""")!.AsObject();
        GeneralDraft.SetSandboxEnabled(root, false);
        Assert.False(root["sandbox"]!["enabled"]!.GetValue<bool>());
        Assert.False(root["sandbox"]!["network"]!.GetValue<bool>());
        GeneralDraft.SetSandboxEnabled(root, true);
        Assert.Null(root["sandbox"]!["enabled"]);
    }

    [Fact]
    public void A_relative_path_cannot_be_saved()
    {
        var draft = Draft();
        draft.WritableText = "build/out";
        Assert.Contains("start with / or ~", draft.Problem);
    }
}
