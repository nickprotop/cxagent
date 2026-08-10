using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

public class ProviderConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-cfg-" + Guid.NewGuid().ToString("N"));
    public ProviderConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_dir, "config.json"), json);
    private AppPaths Paths() => new(_dir);
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void Loads_ValidMultiProviderConfig()
    {
        WriteConfig("""
        {
          "providers": {
            "claude": { "kind": "anthropic", "apiKey": "sk-ant", "model": "claude-x" },
            "local":  { "kind": "ollama", "model": "llama3.3" }
          },
          "defaultProvider": "claude"
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(2, s.Providers.Count);
        Assert.Equal("anthropic", s.Providers["claude"].Kind);
        Assert.Equal("claude", s.DefaultProvider);
    }

    [Fact]
    public void EnvVar_OverridesApiKey()
    {
        WriteConfig("""
        { "providers": { "claude": { "kind":"anthropic", "apiKey":"file-key", "model":"claude-x" } },
          "defaultProvider":"claude" }
        """);
        var env = new Dictionary<string, string> { ["CXAGENT_PROVIDER_CLAUDE_APIKEY"] = "env-key" };
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), env);
        Assert.Equal("env-key", s.Providers["claude"].ApiKey);
    }

    [Fact]
    public void Rejects_UnknownKind_And_MissingKey_And_DanglingDefault_AllAtOnce()
    {
        WriteConfig("""
        {
          "providers": {
            "bad":  { "kind": "made-up", "model": "x" },
            "nokey":{ "kind": "anthropic", "model": "claude-x" }
          },
          "defaultProvider": "ghost"
        }
        """);
        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));
        Assert.Contains(ex.Errors, e => e.Contains("made-up"));      // unknown kind
        Assert.Contains(ex.Errors, e => e.Contains("nokey"));        // missing key
        Assert.Contains(ex.Errors, e => e.Contains("ghost"));        // dangling defaultProvider
        Assert.True(ex.Errors.Count >= 3);                            // batched, not fail-fast
    }

    [Fact]
    public void Rejects_OpenAiCompatible_MissingBaseUrl()
    {
        WriteConfig("""
        { "providers": { "oai": { "kind":"openai-compatible", "apiKey":"k", "model":"gpt" } },
          "defaultProvider":"oai" }
        """);
        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));
        Assert.Contains(ex.Errors, e => e.Contains("baseUrl"));
    }

    [Fact]
    public void Rejects_Routing_ReferencingAbsentProvider()
    {
        WriteConfig("""
        {
          "providers": { "claude": { "kind":"anthropic", "apiKey":"k", "model":"claude-x" } },
          "defaultProvider": "claude",
          "llmAgent": { "routing": { "default": { "provider": "absent", "model": "m" } } }
        }
        """);
        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));
        Assert.Contains(ex.Errors, e => e.Contains("absent"));
    }

    [Fact]
    public void Routing_WithBlankModel_IsRejected()
    {
        WriteConfig("""
        {
          "providers": { "local": { "kind": "ollama", "model": "llama3.1", "baseUrl": "http://localhost:11434" } },
          "defaultProvider": "local",
          "llmAgent": { "routing": { "review": { "provider": "local", "model": "" } } }
        }
        """);

        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));
        Assert.Contains(ex.Errors, e => e.Contains("llmAgent.routing.review") && e.Contains("model"));
    }

    [Fact]
    public void MissingConfigFile_Throws_WithClearError()
    {
        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));
        Assert.Contains(ex.Errors, e => e.Contains("config.json"));
    }

    [Fact]
    public void Orchestrator_BudgetsAreParsed()
    {
        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p",
          "orchestrator": { "maxTokensPerCall": 8000, "goalTokenBudget": 200000 } }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(8000, s.Orchestrator.MaxTokensPerCall);
        Assert.Equal(200000, s.Orchestrator.GoalTokenBudget);
    }

    [Fact]
    public void Orchestrator_Absent_MeansUnbounded_NotZero()
    {
        // A missing block must NOT parse as 0 — that would make every goal breach instantly.
        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p" }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Null(s.Orchestrator.GoalTokenBudget);
        Assert.Null(s.Orchestrator.MaxTokensPerCall);
    }

    [Fact]
    public void Orchestrator_MaxWorkerTurnsRoundTrips()
    {
        // A setting that parses but is never read is the rot pattern this project has hit twice
        // (llmAgent.routing, RoleDefinition.Tools). Pin the parse; Task 3's plugin reads it.
        WriteConfig("""
        {
          "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://x" } },
          "defaultProvider": "local",
          "orchestrator": { "maxWorkerTurns": 4 }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(4, s.Orchestrator.MaxWorkerTurns);
    }

    [Fact]
    public void Orchestrator_MaxWorkerTurns_DefaultsWhenAbsent()
    {
        // Absent must mean the real default, never 0 — a 0 cap would make the agent return
        // empty before its first provider call. Asserted so a
        // silent change fails HERE rather than on a drive: this cap is generous on purpose, and
        // when it was tight (MaxWorkerTurns at 10) it silently broke real work.
        WriteConfig("""
        { "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://x" } },
          "defaultProvider": "local" }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        // Was 10. Measured: an implementer asked to edit six files spent all ten turns READING
        // them (16 read_file calls, zero writes) and reported done. Set where a LOOP lives, not
        // where work lives -- editing N files costs ~2N turns before discovery or a retry.
        Assert.Equal(200, s.Orchestrator.MaxWorkerTurns);
    }

    [Fact]
    public void Orchestrator_ContextCompressThresholdRoundTrips()
    {
        // A setting that parses but is never read is this project's recurring rot pattern
        // (llmAgent.routing, RoleDefinition.Tools, and MaxTokensPerCall — which is STILL unread today).
        // Pin the parse AND the default.
        WriteConfig("""
        {
          "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://x" } },
          "defaultProvider": "local",
          "orchestrator": { "contextCompressThreshold": 40000 }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(40000, s.Orchestrator.ContextCompressThreshold);
    }

    [Fact]
    public void Orchestrator_ContextCompressThreshold_IsNullWhenAbsent()
    {
        // P11 Task 2: absent must parse to null, NOT the 40,000 constant — an explicit
        // "contextCompressThreshold": 40000 has to stay distinguishable from "nobody said", or
        // EffectiveCompressThreshold's precedence (explicit > derived-from-window > constant) cannot
        // tell the two states apart and an explicit 40000 could never be told from "not configured".
        WriteConfig("""
        { "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://x" } },
          "defaultProvider": "local" }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Null(s.Orchestrator.ContextCompressThreshold);
    }

    [Fact]
    public void Provider_ContextWindow_IsConfigurablePerInstance()
    {
        // The window is a PROPERTY OF THE MODEL, and only the user knows which model a
        // custom endpoint is serving. Config always wins over anything probed.
        WriteConfig("""
        {
          "providers": { "local": { "kind": "openai-compatible", "model": "m", "apiKey": "k",
                                    "baseUrl": "http://x", "contextWindow": 8192 } },
          "defaultProvider": "local"
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(8192, s.Providers["local"].ContextWindow);
    }

    [Fact]
    public void Provider_ContextWindow_IsNullWhenUnknown()
    {
        // NULL, not a made-up default. An invented number would silently drive compression at the
        // wrong point, and "we don't know" must stay distinguishable from "we know it is 40,000".
        WriteConfig("""
        { "providers": { "local": { "kind": "ollama", "model": "m", "baseUrl": "http://x" } },
          "defaultProvider": "local" }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Null(s.Providers["local"].ContextWindow);
    }

    [Fact]
    public void Threshold_DerivesFromTheContextWindow_WhenKnown()
    {
        // A fixed 40,000 compresses at 19% of this machine's real 212,992-token window — throwing away
        // context while 81% sits unused. A fraction of the REAL window is the honest trigger.
        var settings = new OrchestratorSettings(null, null);
        Assert.Equal(160_000, settings.EffectiveCompressThreshold(contextWindow: 200_000));
    }

    [Fact]
    public void Threshold_FallsBackToTheFixedValue_WhenTheWindowIsUnknown()
    {
        // Unknown window => the configured constant, unchanged. No guessing a window from nothing.
        var settings = new OrchestratorSettings(null, null);
        Assert.Equal(settings.ContextCompressThreshold,
                     settings.EffectiveCompressThreshold(contextWindow: null));
    }

    [Fact]
    public void Threshold_AnExplicitConfiguredValueAlwaysWins()
    {
        // If the user set a number, honour it — they may know something about their setup we do not.
        var settings = new OrchestratorSettings(null, null, ContextCompressThreshold: 5_000);
        Assert.Equal(5_000, settings.EffectiveCompressThreshold(contextWindow: 200_000));
    }
    // ---- MCP servers -------------------------------------------------------------------------

    [Fact]
    public void Load_ReadsMcpServers()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "filesystem": { "command": ["npx", "-y", "@modelcontextprotocol/server-filesystem", "/tmp"] },
            "sqlite":     { "command": ["uvx", "mcp-server-sqlite"], "enabled": false, "timeoutMs": 60000 }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(2, s.McpServers.Count);
        Assert.Equal(["npx", "-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
                     s.McpServers["filesystem"].Command);

        // Enabled defaults to TRUE: a server someone bothered to configure is one they want, and
        // requiring "enabled": true on every entry is a footgun that reads as a broken config.
        Assert.True(s.McpServers["filesystem"].Enabled);
        Assert.False(s.McpServers["sqlite"].Enabled);
        Assert.Equal(60_000, s.McpServers["sqlite"].TimeoutMs);
    }

    /// <summary>The common case — no mcp block at all — is not an error and not a null.</summary>
    [Fact]
    public void Load_WithNoMcpBlock_YieldsNoServers()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p"
        }
        """);

        Assert.Empty(ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers);
    }

    /// <summary>
    /// A MALFORMED SERVER ENTRY IS SKIPPED, NOT FATAL.
    ///
    /// <para>Every other validation failure in this loader is collected into a
    /// <see cref="ProviderConfigException"/>, and an unloadable config refuses Settings outright and
    /// routes to the repair wizard. Putting MCP on that list would mean one typo'd command line takes
    /// the whole app down — no providers, no session, over an optional third-party tool server.</para>
    ///
    /// <para>So the bad entry is dropped, the rest of the config loads, and the reason is carried out
    /// on <see cref="ProviderSettings.Warnings"/> where the UI can say it. Silently ignoring it would
    /// be its own bug: the user would see a server that simply never appears.</para>
    /// </summary>
    [Fact]
    public void Load_WithAnEmptyCommand_SkipsThatServer_AndStillLoadsTheRest()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "broken": { "command": [] },
            "good":   { "command": ["python3", "-m", "server"] }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(["good"], s.McpServers.Keys);
        Assert.Single(s.Providers);                                    // the rest of the config survived
        Assert.Contains(s.Warnings, w => w.Contains("broken", StringComparison.Ordinal));
    }
}
