using System.Text.Json;
using System.Text.Json.Nodes;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class ProviderConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-pcw-" + Guid.NewGuid().ToString("N"));
    public ProviderConfigWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private AppPaths Paths() => new(_dir);
    private string ConfigPath => Path.Combine(_dir, "config.json");

    private static ProviderSettings Settings(params (string name, ProviderInstanceConfig cfg)[] entries) =>
        new(entries.ToDictionary(e => e.name, e => e.cfg), entries.FirstOrDefault().name,
            Array.Empty<string>(), new Dictionary<string, RoutingTarget>());

    [Fact]
    public void Write_ThenLoad_RoundTrips()
    {
        var s = Settings(("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-abc", null, null)));
        ProviderConfigWriter.Write(Paths(), s);

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        Assert.Equal("claude", loaded.DefaultProvider);
        Assert.Equal("anthropic", loaded.Providers["claude"].Kind);
        Assert.Equal("claude-x", loaded.Providers["claude"].Model);
        Assert.Equal("sk-abc", loaded.Providers["claude"].ApiKey);
    }

    [Fact]
    public void Write_PreservesUnknownTopLevelKeys()
    {
        // A hand-edited config with blocks cxagent's model doesn't cover.
        File.WriteAllText(ConfigPath, """
        {
          "providers": {},
          "defaultProvider": null,
          "jobs": { "maxParallel": 7 },
          "ui": { "theme": "light" }
        }
        """);

        ProviderConfigWriter.Write(Paths(),
            Settings(("local", new ProviderInstanceConfig("ollama", "llama3.1", null, "http://localhost:11434", null))));

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal(7, doc.RootElement.GetProperty("jobs").GetProperty("maxParallel").GetInt32());
        Assert.Equal("light", doc.RootElement.GetProperty("ui").GetProperty("theme").GetString());
        Assert.Equal("local", doc.RootElement.GetProperty("defaultProvider").GetString());
    }

    [Fact]
    public void OrchestratorSettings_RoundTripThroughWriteAndLoad()
    {
        var orch = new OrchestratorSettings(5000, 100_000,
            MaxWorkerTurns: 6, ContextCompressThreshold: 30_000);
        ProviderConfigWriter.Write(Paths(),
            Settings(("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk-abc", null, null)))
                with { Orchestrator = orch });

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        Assert.Equal(orch, loaded.Orchestrator);   // record equality — every field, one assert
    }

    [Fact]
    public void Write_OmitsUnconfiguredOrchestratorFields_SoNullStaysDistinctFromChosen()
    {
        // ContextCompressThreshold null means "nobody said" (ProviderConfig.cs:46-58) and AgentHost
        // derives from the context window or falls back. Writing an explicit number on the first Save
        // would permanently collapse that distinction for every config the dialog ever touches.
        ProviderConfigWriter.Write(Paths(),
            Settings(("local", new ProviderInstanceConfig("ollama", "llama3.1", null, "http://localhost:11434", null)))
                with { Orchestrator = OrchestratorSettings.Unbounded });

        var raw = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("contextCompressThreshold", raw);
        Assert.DoesNotContain("maxTokensPerCall", raw);

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        Assert.Null(loaded.Orchestrator.ContextCompressThreshold);
        Assert.Null(loaded.Orchestrator.MaxTokensPerCall);
    }

    [Fact]
    public void Write_PreservesUnknownKeysInsideTheOrchestratorBlock()
    {
        // Same contract the llmAgent block honours (ProviderConfigWriter.cs:62): own only the known
        // keys, merge into the existing object — a hand-edited future knob must survive a Save.
        File.WriteAllText(ConfigPath,
            """{"providers":{},"defaultProvider":null,"orchestrator":{"futureKnob":1,"maxWorkerTurns":9}}""");

        ProviderConfigWriter.Write(Paths(),
            Settings(("local", new ProviderInstanceConfig("ollama", "llama3.1", null, "http://localhost:11434", null)))
                with { Orchestrator = new OrchestratorSettings(null, null, MaxWorkerTurns: 12) });

        var orch = JsonNode.Parse(File.ReadAllText(ConfigPath))!
            .AsObject()["orchestrator"]!.AsObject();
        Assert.Equal(1, (int)orch["futureKnob"]!);
        Assert.Equal(12, (int)orch["maxWorkerTurns"]!);
    }

    [Fact]
    public void Write_OmitsNullApiKeyAndBaseUrl()
    {
        ProviderConfigWriter.Write(Paths(),
            Settings(("local", new ProviderInstanceConfig("ollama", "llama3.1", null, null, null))));

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var p = doc.RootElement.GetProperty("providers").GetProperty("local");
        Assert.False(p.TryGetProperty("apiKey", out _));
        Assert.False(p.TryGetProperty("baseUrl", out _));
    }

    [Fact]
    public void Write_RoundTripsExtraHeaders()
    {
        var s = Settings(("openrouter", new ProviderInstanceConfig(
            "openai-compatible", "anthropic/claude-sonnet-4-5", "sk-or-1",
            "https://openrouter.ai/api/v1",
            new Dictionary<string, string> { ["HTTP-Referer"] = "https://example.test", ["X-Title"] = "cxagent" })));
        ProviderConfigWriter.Write(Paths(), s);

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        var headers = loaded.Providers["openrouter"].ExtraHeaders;
        Assert.NotNull(headers);
        Assert.Equal("https://example.test", headers!["HTTP-Referer"]);
        Assert.Equal("cxagent", headers["X-Title"]);
    }

    [Fact]
    public void Write_LeavesNoTempFile()
    {
        ProviderConfigWriter.Write(Paths(),
            Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null))));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_RoundTripsContextWindow()
    {
        // Same rot pattern the codebase already guards against elsewhere (llmAgent.routing,
        // RoleDefinition.Tools): a field that parses but the writer silently drops on next save.
        var s = Settings(("local", new ProviderInstanceConfig(
            "ollama", "m", null, "http://x", null, 8192)));
        ProviderConfigWriter.Write(Paths(), s);

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        Assert.Equal(8192, loaded.Providers["local"].ContextWindow);
    }

    [Fact]
    public void Write_OmitsContextWindow_WhenNull()
    {
        ProviderConfigWriter.Write(Paths(),
            Settings(("local", new ProviderInstanceConfig("ollama", "llama3.1", null, "http://x", null))));

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var p = doc.RootElement.GetProperty("providers").GetProperty("local");
        Assert.False(p.TryGetProperty("contextWindow", out _));
    }

    [Fact]
    public void Write_IsOverwritable_WithoutDuplicatingProviders()
    {
        var paths = Paths();
        ProviderConfigWriter.Write(paths, Settings(("a", new ProviderInstanceConfig("anthropic", "m1", "k", null, null))));
        ProviderConfigWriter.Write(paths, Settings(("b", new ProviderInstanceConfig("anthropic", "m2", "k", null, null))));

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var provs = doc.RootElement.GetProperty("providers");
        Assert.False(provs.TryGetProperty("a", out _));   // replaced, not merged
        Assert.True(provs.TryGetProperty("b", out _));
    }
}
