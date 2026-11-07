using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>memory.enabled</c>: on in the app unless config turns it off; off for a library caller unless it turns it on.
/// </summary>
public class MemoryConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cxagent-memory-cfg").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Providers = """
        "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://localhost:1/v1" } },
        "defaultProvider": "local"
        """;

    private ProviderSettings Load(string extra)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ {{Providers}}{{extra}} }""");
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
    }

    [Fact]
    public void The_key_is_read_and_absent_is_null()
    {
        Assert.False(Load(""", "memory": { "enabled": false }""").Memory);
        Assert.True(Load(""", "memory": { "enabled": true }""").Memory);
        Assert.Null(Load("").Memory);
    }

    [Theory]
    [InlineData("\"off\"")]
    [InlineData("""{ "enabled": "no" }""")]
    public void A_malformed_value_warns_and_keeps_the_default(string written)
    {
        var settings = Load($", \"memory\": {written}");
        Assert.Null(settings.Memory);
        Assert.Contains(settings.Warnings, w => w.Contains("memory", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unconfigured_app_remembers_and_a_switched_off_one_does_not_on_both_paths()
    {
        var env = new Dictionary<string, string>();
        Load("");
        Assert.True(ConfigResolver.Resolve(new AppPaths(_dir), env, useMock: false).Memory);
        Assert.True(ConfigResolver.ResolveInstance(new AppPaths(_dir), env, "local")!.Memory);

        // BOTH SITES: launch, and the one /model takes — a key carried by one alone works only after a switch.
        Load(""", "memory": { "enabled": false }""");
        Assert.False(ConfigResolver.Resolve(new AppPaths(_dir), env, useMock: false).Memory);
        Assert.False(ConfigResolver.ResolveInstance(new AppPaths(_dir), env, "local")!.Memory);
    }

    [Fact]
    public void A_library_caller_has_memory_only_when_it_asks()
    {
        static AgentConfig Config(bool memory) => new()
        {
            Models = { ["local"] = new ModelConfig(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1", ApiKey = "k" } },
            Memory = memory,
        };
        Assert.False(new AgentConfig { Models = { ["local"] = new ModelConfig(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://localhost:1/v1", ApiKey = "k" } } }.Resolve().Memory);
        Assert.True(Config(true).Resolve().Memory);
    }
}
