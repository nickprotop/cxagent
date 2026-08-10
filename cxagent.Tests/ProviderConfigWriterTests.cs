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
    // ---- MCP servers -------------------------------------------------------------------------

    /// <summary>
    /// The writer OWNS "mcp" once the editor can change it. A server the user deleted in Settings has
    /// to actually disappear from the file, which means writing the block wholesale rather than
    /// merging into whatever was there.
    /// </summary>
    [Fact]
    public void Write_ThenLoad_RoundTripsMcpServers()
    {
        var s = Settings(("claude", new ProviderInstanceConfig("anthropic", "claude-x", "sk", null, null)))
            with
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["context7"] = new(["npx", "-y", "@upstash/context7-mcp"], Enabled: true, TimeoutMs: null),
                    ["sqlite"] = new(["uvx", "mcp-server-sqlite"], Enabled: false, TimeoutMs: 60_000),
                },
            };

        ProviderConfigWriter.Write(Paths(), s);
        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());

        Assert.Equal(["npx", "-y", "@upstash/context7-mcp"], loaded.McpServers["context7"].Command);
        Assert.True(loaded.McpServers["context7"].Enabled);
        Assert.False(loaded.McpServers["sqlite"].Enabled);
        Assert.Equal(60_000, loaded.McpServers["sqlite"].TimeoutMs);
    }

    /// <summary>
    /// Per-server keys we do not model — a future knob, a hand-added field — are merged back, the way
    /// the orchestrator block already does it. Owning the block is not licence to discard what is in
    /// it.
    /// </summary>
    [Fact]
    public void Write_PreservesUnknownPerServerKeys()
    {
        // "env" was this test's stand-in for an unmodelled key until env became a real option, at
        // which point the writer rightly started owning it. Use a key we genuinely do not model, or
        // the test asserts the opposite of what it means.
        File.WriteAllText(ConfigPath, """
        {
          "providers": {},
          "mcp": { "context7": { "command": ["old"], "someFutureKnob": { "depth": 3 } } }
        }
        """);

        ProviderConfigWriter.Write(Paths(),
            Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)))
                with
                {
                    McpServers = new Dictionary<string, McpServerConfig>
                    {
                        ["context7"] = new(["npx", "-y", "@upstash/context7-mcp"], true, null),
                    },
                });

        var root = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        var server = root["mcp"]!["context7"]!.AsObject();
        Assert.Equal(3, server["someFutureKnob"]!["depth"]!.GetValue<int>());
        Assert.Equal("npx", server["command"]![0]!.GetValue<string>());   // and ours won on the key we own
    }

    /// <summary>Environment and working directory survive a save — they are how a server gets its
    /// credentials and finds the right tree, so losing them on an unrelated Settings save would break
    /// a working server silently.</summary>
    [Fact]
    public void Write_ThenLoad_RoundTripsEnvironmentAndWorkingDirectory()
    {
        var s = Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)))
            with
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["context7"] = new(["npx", "-y", "@upstash/context7-mcp"], true, null,
                        Environment: new Dictionary<string, string> { ["CONTEXT7_API_KEY"] = "secret" },
                        WorkingDirectory: "/srv/project"),
                },
            };

        ProviderConfigWriter.Write(Paths(), s);
        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());

        Assert.Equal("secret", loaded.McpServers["context7"].Environment!["CONTEXT7_API_KEY"]);
        Assert.Equal("/srv/project", loaded.McpServers["context7"].WorkingDirectory);
    }

    /// <summary>A server removed in Settings is gone from the file, not resurrected by the merge.</summary>
    [Fact]
    public void Write_RemovesAServerThatIsNoLongerConfigured()
    {
        File.WriteAllText(ConfigPath, """
        {
          "providers": {},
          "mcp": { "gone": { "command": ["x"] }, "kept": { "command": ["y"] } }
        }
        """);

        ProviderConfigWriter.Write(Paths(),
            Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)))
                with
                {
                    McpServers = new Dictionary<string, McpServerConfig> { ["kept"] = new(["y"], true, null) },
                });

        var mcp = JsonNode.Parse(File.ReadAllText(ConfigPath))!["mcp"]!.AsObject();
        Assert.False(mcp.ContainsKey("gone"));
        Assert.True(mcp.ContainsKey("kept"));
    }
    /// <summary>
    /// A remote server round-trips as a url with its headers, and NEVER also as a command — the
    /// loader skips an entry carrying both, so writing both would produce a file this same writer's
    /// config could not load back.
    /// </summary>
    [Fact]
    public void Write_ThenLoad_RoundTripsARemoteServer()
    {
        var s = Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)))
            with
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["remote"] = new([], true, null, null, null,
                        Url: "https://mcp.context7.com/mcp",
                        Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }),
                },
            };

        ProviderConfigWriter.Write(Paths(), s);

        var written = JsonNode.Parse(File.ReadAllText(ConfigPath))!["mcp"]!["remote"]!.AsObject();
        Assert.False(written.ContainsKey("command"), "a remote server must not also be written as a command");

        var loaded = ProviderConfigLoader.LoadAndValidate(Paths(), new Dictionary<string, string>());
        Assert.Equal("https://mcp.context7.com/mcp", loaded.McpServers["remote"].Url);
        Assert.Equal("Bearer abc", loaded.McpServers["remote"].Headers!["Authorization"]);
        Assert.True(loaded.McpServers["remote"].IsRemote);
    }

    /// <summary>And a server switched from remote back to local loses its url, or the loader would
    /// skip it as ambiguous on the next launch.</summary>
    [Fact]
    public void Write_ALocalServer_ClearsAnyPreviousUrl()
    {
        File.WriteAllText(ConfigPath, """
        {
          "providers": {},
          "mcp": { "srv": { "url": "https://old.example/mcp" } }
        }
        """);

        ProviderConfigWriter.Write(Paths(),
            Settings(("claude", new ProviderInstanceConfig("anthropic", "m", "k", null, null)))
                with
                {
                    McpServers = new Dictionary<string, McpServerConfig> { ["srv"] = new(["npx", "y"]) },
                });

        var written = JsonNode.Parse(File.ReadAllText(ConfigPath))!["mcp"]!["srv"]!.AsObject();
        Assert.False(written.ContainsKey("url"));
        Assert.Equal("npx", written["command"]![0]!.GetValue<string>());
    }
}
