using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The <c>webSearch</c> block: an optional Tavily key. Without one, searches use DuckDuckGo, so a
/// missing or broken key is never a reason to refuse a config.
/// </summary>
public class WebSearchConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-websearch-cfg").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Providers = """
        "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } },
        "defaultProvider": "local"
        """;

    private ProviderSettings Load(string json, Dictionary<string, string>? env = null)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), env ?? []);
    }

    [Fact]
    public void Literal_key_is_read() =>
        Assert.Equal("tvly-x", Load($$"""{ {{Providers}}, "webSearch": { "tavilyApiKey": "tvly-x" } }""")
            .WebSearch?.TavilyApiKey);

    [Fact]
    public void Env_placeholder_is_expanded() =>
        Assert.Equal("tvly-env", Load($$"""{ {{Providers}}, "webSearch": { "tavilyApiKey": "{env:TAVILY_API_KEY}" } }""",
            new() { ["TAVILY_API_KEY"] = "tvly-env" }).WebSearch?.TavilyApiKey);

    [Fact]
    public void Unset_placeholder_warns_and_leaves_no_key()
    {
        var settings = Load($$"""{ {{Providers}}, "webSearch": { "tavilyApiKey": "{env:NOT_SET_HERE}" } }""");
        Assert.Null(settings.WebSearch?.TavilyApiKey);
        Assert.Contains(settings.Warnings, w => w.Contains("webSearch.tavilyApiKey"));
    }

    [Fact]
    public void Absent_block_means_no_key() =>
        Assert.Null(Load($$"""{ {{Providers}} }""").WebSearch?.TavilyApiKey);

    [Fact]
    public void Non_string_key_is_a_warning_not_an_error()
    {
        var settings = Load($$"""{ {{Providers}}, "webSearch": { "tavilyApiKey": 42 } }""");
        Assert.Null(settings.WebSearch?.TavilyApiKey);
        Assert.Contains(settings.Warnings, w => w.Contains("webSearch.tavilyApiKey"));
    }

    /// <summary>BOTH CATALOG SITES — launch and a /model switch — for the reason
    /// ShellDetachOnTimeoutConfigTests gives: a key carried by one works only after a switch.</summary>
    [Fact]
    public void Resolved_config_carries_it_from_both_construction_sites()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"),
            $$"""{ {{Providers}}, "webSearch": { "tavilyApiKey": "tvly-x" } }""");
        var env = new Dictionary<string, string>();
        Assert.Equal("tvly-x", ConfigResolver.Resolve(new AppPaths(_dir), env, useMock: false).WebSearch.TavilyApiKey);
        Assert.Equal("tvly-x", ConfigResolver.ResolveInstance(new AppPaths(_dir), env, "local")!.WebSearch.TavilyApiKey);
    }

    [Fact]
    public void An_unconfigured_machine_resolves_to_no_key()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ {{Providers}} }""");
        Assert.Null(ConfigResolver.Resolve(new AppPaths(_dir), new Dictionary<string, string>(), useMock: false).WebSearch.TavilyApiKey);
    }

    /// <summary>The sample documents every key, and must still load once it documents this one.</summary>
    [Fact]
    public void Sample_config_parses_with_its_webSearch_block()
    {
        var sample = File.ReadAllText(Path.Combine(RepoRoot(), "config.sample.json"));
        Assert.Contains("\"webSearch\"", sample);
        var settings = ProviderConfigLoader.Parse(sample, new Dictionary<string, string> { ["TAVILY_API_KEY"] = "k" });
        Assert.Equal("k", settings.WebSearch?.TavilyApiKey);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config.sample.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("config.sample.json not found above the test binaries");
    }
}
