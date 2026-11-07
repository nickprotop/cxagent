using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.UI.Config;
using CxAgent.Web;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>colleagues.enabled</c>: on in the app unless config turns it off, off for a library caller unless it turns it on —
/// and the same switch in F8 › General and on the browser's config page, written through one draft.
/// </summary>
public sealed class ColleaguesSwitchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-colleagues-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Provider = """ "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } }, "defaultProvider": "local" """;

    private static ProviderSettings Settings(string extra = "") =>
        ProviderConfigLoader.Parse("{" + Provider + (extra.Length > 0 ? ", " + extra : "") + "}", new Dictionary<string, string>());

    private static JsonObject Root(string extra = "") => JsonNode.Parse("{" + Provider + (extra.Length > 0 ? ", " + extra : "") + "}")!.AsObject();

    private const string Off = "\"colleagues\": { \"enabled\": false }";

    [Fact]
    public void The_key_is_read_absent_is_null_and_a_bad_one_warns()
    {
        Assert.False(Settings(Off).Colleagues);
        Assert.True(Settings("\"colleagues\": { \"enabled\": true }").Colleagues);
        Assert.Null(Settings().Colleagues);

        var bad = Settings("\"colleagues\": \"off\"");
        Assert.Null(bad.Colleagues);
        Assert.Contains(bad.Warnings, w => w.Contains("colleagues", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unconfigured_app_keeps_colleagues_on_both_paths_and_a_switched_off_one_does_not()
    {
        var env = new Dictionary<string, string>();
        var paths = new AppPaths(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{" + Provider + "}");
        Assert.True(ConfigResolver.Resolve(paths, env, useMock: false).Colleagues);
        Assert.True(ConfigResolver.ResolveInstance(paths, env, "local")!.Colleagues);

        File.WriteAllText(Path.Combine(_dir, "config.json"), "{" + Provider + ", " + Off + "}");
        Assert.False(ConfigResolver.Resolve(paths, env, useMock: false).Colleagues);
        Assert.False(ConfigResolver.ResolveInstance(paths, env, "local")!.Colleagues);
    }

    [Fact]
    public void A_library_caller_has_colleagues_only_when_it_asks()
    {
        static AgentConfig Config(bool? on) => new()
        {
            Models = { ["local"] = new ModelConfig(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1", ApiKey = "k" } },
            Colleagues = on ?? false,
        };
        Assert.False(Config(null).Resolve().Colleagues);
        Assert.True(Config(true).Resolve().Colleagues);
    }

    [Fact]
    public void Draft_reads_on_when_absent_and_turning_it_off_writes_the_block()
    {
        Assert.True(GeneralDraft.From(Settings()).ColleaguesEnabled);
        Assert.False(GeneralDraft.From(Settings(Off)).ColleaguesEnabled);

        var root = Root();
        var draft = GeneralDraft.From(Settings());
        draft.ColleaguesEnabled = false;
        Assert.True(draft.IsDirty);
        draft.Apply(root);
        Assert.False(root["colleagues"]!["enabled"]!.GetValue<bool>());

        var off = Root(Off);
        var back = GeneralDraft.From(Settings(Off));
        back.ColleaguesEnabled = true;
        back.Apply(off);
        Assert.Null(off["colleagues"]);
    }

    [Fact]
    public void Browser_form_shows_and_writes_the_same_key()
    {
        var draft = GeneralDraft.From(Settings());

        Assert.Contains("\"colleagues\"", WebConfigForms.General(draft).ToJsonString());

        WebConfigForms.SetGeneral(draft, new JsonObject { ["colleagues"] = "false" });
        Assert.False(draft.ColleaguesEnabled);
        var root = Root();
        draft.Apply(root);
        Assert.False(root["colleagues"]!["enabled"]!.GetValue<bool>());
    }
}
