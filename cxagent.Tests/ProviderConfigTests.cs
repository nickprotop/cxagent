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
    public void Orchestrator_MaxTurnsRoundTrips()
    {
        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p",
          "orchestrator": { "maxTurns": 42 } }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(42, s.Orchestrator.MaxTurns);
    }

    /// <summary>
    /// ABSENT STAYS NULL, and that is the whole reason this field is nullable.
    ///
    /// <para>It used to default to a real 200, which made "the user chose 200" and "the user chose
    /// nothing" the same value — and the app worked around it by opening config.json a second time,
    /// by hand, to recover the distinction. What absence MEANS is decided by the reader
    /// (AgentHost.CeilingFor), not smuggled into the parse.</para>
    /// </summary>
    [Fact]
    public void Orchestrator_MaxTurnsAbsent_StaysNull()
    {
        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p" }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Null(s.Orchestrator.MaxTurns);
    }

    /// <summary>Zero is the explicit no-cap, so it must survive the parse as zero.</summary>
    [Fact]
    public void Orchestrator_MaxTurnsZero_IsKept_AndNegativeIsIgnored()
    {
        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p",
          "orchestrator": { "maxTurns": 0 } }
        """);
        Assert.Equal(0, ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).Orchestrator.MaxTurns);

        WriteConfig("""
        { "providers": { "p": { "kind":"ollama", "model":"m", "baseUrl":"http://x" } },
          "defaultProvider":"p",
          "orchestrator": { "maxTurns": -5 } }
        """);
        var negative = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Null(negative.Orchestrator.MaxTurns);
        Assert.Contains(negative.Warnings, w => w.Contains("negative"));
    }

    [Fact]
    public void Orchestrator_ContextCompressThresholdRoundTrips()
    {
        // A setting that parses but is never read is this project's recurring rot pattern — it has
        // cost llmAgent.routing, RoleDefinition.Tools and two orchestrator keys so far. Pin the
        // parse AND the default, so this one cannot join them.
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
        var settings = new OrchestratorSettings();
        Assert.Equal(160_000, settings.EffectiveCompressThreshold(contextWindow: 200_000));
    }

    [Fact]
    public void Threshold_FallsBackToTheFixedValue_WhenTheWindowIsUnknown()
    {
        // Unknown window => the configured constant, unchanged. No guessing a window from nothing.
        var settings = new OrchestratorSettings();
        Assert.Equal(settings.ContextCompressThreshold,
                     settings.EffectiveCompressThreshold(contextWindow: null));
    }

    [Fact]
    public void Threshold_AnExplicitConfiguredValueAlwaysWins()
    {
        // If the user set a number, honour it — they may know something about their setup we do not.
        var settings = new OrchestratorSettings(ContextCompressThreshold: 5_000);
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

    /// <summary>
    /// A server can be given its own environment and working directory.
    ///
    /// <para><c>env</c> is the spec's prescribed credential channel for stdio — <i>"retrieve
    /// credentials from the environment"</i>. A child already inherits ours, but that is
    /// process-wide: two servers needing different values for the same variable cannot both be served
    /// by an export, and a key meant for one server should not be visible to all of them.</para>
    ///
    /// <para><c>cwd</c> matters because servers that take a path argument resolve it relative to
    /// where they were started — from the wrong directory a filesystem server reads the wrong
    /// tree.</para>
    /// </summary>
    [Fact]
    public void Load_ReadsPerServerEnvironmentAndWorkingDirectory()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "context7": {
              "command": ["npx", "-y", "@upstash/context7-mcp"],
              "env": { "CONTEXT7_API_KEY": "secret" },
              "cwd": "/srv/project"
            }
          }
        }
        """);

        var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["context7"];

        Assert.Equal("secret", server.Environment!["CONTEXT7_API_KEY"]);
        Assert.Equal("/srv/project", server.WorkingDirectory);
    }

    /// <summary>Both are optional, and absent means null rather than an empty dictionary — "inherit
    /// ours" has to stay distinguishable from "start with nothing".</summary>
    [Fact]
    public void Load_WithNoEnvOrCwd_LeavesThemNull()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": { "plain": { "command": ["x"] } }
        }
        """);

        var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["plain"];

        Assert.Null(server.Environment);
        Assert.Null(server.WorkingDirectory);
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
    // ---- transport selection -------------------------------------------------------------------

    /// <summary>
    /// A `url` server is an HTTP one. The transport is inferred from which key is present rather than
    /// from a `"type"` field, because the two are never both meaningful: a command is a process and a
    /// url is an endpoint, and asking the user to say which in a third place is a way to get a config
    /// that contradicts itself.
    /// </summary>
    [Fact]
    public void Load_ReadsARemoteServerAndItsHeaders()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "remote": {
              "url": "https://mcp.context7.com/mcp",
              "headers": { "Authorization": "Bearer abc" }
            }
          }
        }
        """);

        var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["remote"];

        Assert.Equal("https://mcp.context7.com/mcp", server.Url);
        Assert.Equal("Bearer abc", server.Headers!["Authorization"]);
        Assert.True(server.IsRemote);
        Assert.Empty(server.Command);
    }

    /// <summary>A `command` server is still a local one, and says so.</summary>
    [Fact]
    public void Load_ALocalServer_IsNotRemote()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": { "local": { "command": ["npx", "thing"] } }
        }
        """);

        var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["local"];

        Assert.False(server.IsRemote);
        Assert.Null(server.Url);
    }

    /// <summary>
    /// BOTH is ambiguous and is SKIPPED rather than guessed at. Picking one silently would run
    /// whichever we happened to prefer — possibly spawning a process for someone who meant to reach a
    /// remote endpoint, which is a security-relevant difference, not a cosmetic one.
    /// </summary>
    [Fact]
    public void Load_WithBothCommandAndUrl_SkipsThatServerWithAWarning()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "confused": { "command": ["npx", "x"], "url": "https://example.com/mcp" },
            "fine":     { "command": ["npx", "y"] }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(["fine"], s.McpServers.Keys);
        Assert.Contains(s.Warnings, w => w.Contains("confused", StringComparison.Ordinal));
    }

    /// <summary>And NEITHER is nothing to start at all — the same skip, so a half-written entry never
    /// becomes a server that silently does nothing.</summary>
    [Fact]
    public void Load_WithNeitherCommandNorUrl_SkipsThatServerWithAWarning()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": { "empty": { "enabled": true } }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Empty(s.McpServers);
        Assert.Contains(s.Warnings, w => w.Contains("empty", StringComparison.Ordinal));
    }
    /// <summary>
    /// SUBSTITUTION NEVER TOUCHES `command`.
    ///
    /// <para>Interpolating into an argv that spawns a process turns a config file into a
    /// code-execution seam, and "my API key ended up as a process argument — and in ps output"
    /// is a far worse failure than "I had to type the command out". env and headers are the
    /// credential channels; the command line is not one.</para>
    /// </summary>
    [Fact]
    public void Load_DoesNotSubstitutePlaceholdersIntoTheCommand()
    {
        Environment.SetEnvironmentVariable("CXA_CFG_TEST", "expanded-value");
        try
        {
            WriteConfig("""
            {
              "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
              "defaultProvider": "p",
              "mcp": {
                "srv": {
                  "command": ["npx", "{env:CXA_CFG_TEST}"],
                  "env": { "TOKEN": "{env:CXA_CFG_TEST}" }
                }
              }
            }
            """);

            var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["srv"];

            // The env value expanded...
            Assert.Equal("expanded-value", server.Environment!["TOKEN"]);
            // ...and the command did NOT.
            Assert.Equal(["npx", "{env:CXA_CFG_TEST}"], server.Command);
        }
        finally { Environment.SetEnvironmentVariable("CXA_CFG_TEST", null); }
    }

    /// <summary>A header placeholder expands, which is how an API key reaches a remote server without
    /// being written into config.json.</summary>
    [Fact]
    public void Load_SubstitutesIntoHeaderValues()
    {
        Environment.SetEnvironmentVariable("CXA_CFG_KEY", "sk-from-env");
        try
        {
            WriteConfig("""
            {
              "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
              "defaultProvider": "p",
              "mcp": {
                "remote": {
                  "url": "https://example.test/mcp",
                  "headers": { "Authorization": "Bearer {env:CXA_CFG_KEY}" }
                }
              }
            }
            """);

            var server = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv).McpServers["remote"];

            Assert.Equal("Bearer sk-from-env", server.Headers!["Authorization"]);
        }
        finally { Environment.SetEnvironmentVariable("CXA_CFG_KEY", null); }
    }

    /// <summary>An unset placeholder warns and loads — never fatal, per the MCP block's whole
    /// contract.</summary>
    [Fact]
    public void Load_WithAnUnsetPlaceholder_WarnsRatherThanFailing()
    {
        WriteConfig("""
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "defaultProvider": "p",
          "mcp": {
            "remote": {
              "url": "https://example.test/mcp",
              "headers": { "Authorization": "Bearer {env:CXA_DEFINITELY_UNSET}" }
            }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Single(s.McpServers);                                    // still loaded
        Assert.Contains(s.Warnings, w => w.Contains("CXA_DEFINITELY_UNSET", StringComparison.Ordinal));
    }

    // ---- agents: sub-agent types ----------------------------------------------------------------

    private const string TwoProviders = """
        "providers": {
          "local": { "kind": "ollama", "model": "llama3.3" },
          "fast":  { "kind": "ollama", "model": "llama3.2-1b" }
        },
        "defaultProvider": "local"
        """;

    [Fact]
    public void Agents_ParsesBriefingProviderAndMaxTurns()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": {
            "explore": { "briefing": "Search and report.", "provider": "fast", "maxTurns": 30 },
            "review":  { "briefing": "Review for correctness." }
          }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(2, s.AgentTypes.Count);
        Assert.Equal("Search and report.", s.AgentTypes["explore"].Briefing);
        Assert.Equal("fast", s.AgentTypes["explore"].Provider);
        Assert.Equal(30, s.AgentTypes["explore"].MaxTurns);

        // Omitted provider and maxTurns are null — INHERIT, not a default invented here.
        Assert.Null(s.AgentTypes["review"].Provider);
        Assert.Null(s.AgentTypes["review"].MaxTurns);
    }

    /// <summary>
    /// AN ABSENT BLOCK IS THE COMMON CASE AND COSTS NOTHING. Empty here does not mean "no types" —
    /// the implicit `general` is supplied downstream, so config only ever gets to OVERRIDE it.
    /// </summary>
    [Fact]
    public void Agents_AbsentBlock_IsEmpty_AndNotAnError()
    {
        WriteConfig($$"""{ {{TwoProviders}} }""");
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Empty(s.AgentTypes);
        Assert.Empty(s.Warnings);
    }

    /// <summary>
    /// A BAD TYPE IS DROPPED WITH A WARNING, NOT AN ERROR — the same rule the MCP block follows, and
    /// for the same reason: everything that stops the app is something there is no session without,
    /// and a type is an optional convenience. A typo'd briefing must not take providers down.
    /// </summary>
    [Fact]
    public void Agents_MissingBriefing_IsDroppedWithAWarning()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": { "broken": { "provider": "fast" }, "good": { "briefing": "Fine." } }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.False(s.AgentTypes.ContainsKey("broken"));
        Assert.True(s.AgentTypes.ContainsKey("good"));
        Assert.Contains(s.Warnings, w => w.Contains("broken", StringComparison.Ordinal)
                                      && w.Contains("briefing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A DESCRIPTION IS FOR THE PARENT, and it has to survive the parse to reach the catalog. It is
    /// the only thing the model sees before choosing a type, so a key that is silently dropped is a
    /// feature that looks configured and does nothing.
    /// </summary>
    [Fact]
    public void Agents_CarryTheirDescription()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": {
            "explore": {
              "description": "when answering means reading across several files",
              "briefing": "You search and report."
            }
          }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal("when answering means reading across several files",
            s.AgentTypes["explore"].Description);
    }

    /// <summary>
    /// ABSENT IS NOT EMPTY. Every config written before this key existed has no description, and the
    /// catalog decides what to show from exactly this distinction.
    /// </summary>
    [Fact]
    public void Agents_WithoutADescription_HaveNull()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": { "explore": { "briefing": "You search and report." } }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Null(s.AgentTypes["explore"].Description);
    }

    /// <summary>
    /// A LONG DESCRIPTION IS KEPT WHOLE. The 140-character bound belonged to Summarise, which existed
    /// to stop a SCAVENGED sentence running away; this text is written on purpose for this slot, and
    /// truncating it would be the app rewriting its author.
    /// </summary>
    [Fact]
    public void Agents_LongDescription_IsNotTruncated()
    {
        var longText = new string('x', 400);
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": { "explore": { "description": "{{longText}}", "briefing": "b" } }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(400, s.AgentTypes["explore"].Description!.Length);
    }

    /// <summary>Whitespace-only is absent. A description of spaces would print a blank catalog line,
    /// which reads as a bug rather than as "nothing was configured".</summary>
    [Fact]
    public void Agents_WhitespaceOnlyDescription_IsTreatedAsAbsent()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": { "explore": { "description": "   ", "briefing": "b" } }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Null(s.AgentTypes["explore"].Description);
    }

    /// <summary>
    /// A PROVIDER THAT IS NOT CONFIGURED IS CAUGHT AT LOAD. Found three turns into a child's run it
    /// reads as a sub-agent bug; found here it reads as what it is. The type survives on the parent's
    /// provider rather than being dropped — the briefing is still useful.
    /// </summary>
    [Fact]
    public void Agents_UnknownProvider_WarnsAndFallsBackToTheParents()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": { "explore": { "briefing": "Search.", "provider": "typo" } }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Null(s.AgentTypes["explore"].Provider);
        Assert.Contains(s.Warnings, w => w.Contains("typo", StringComparison.Ordinal));
        // The message names what IS valid, not only what was wrong.
        Assert.Contains(s.Warnings, w => w.Contains("fast", StringComparison.Ordinal));
    }

    /// <summary>
    /// ZERO IS MEANINGFUL, NEGATIVE IS A TYPO. Zero is the same explicit opt-out AgentHost.TurnCeiling
    /// gives the session, so it cannot be lumped in with invalid values and clamped away.
    /// </summary>
    [Fact]
    public void Agents_MaxTurnsZero_IsKept_AndNegativeIsIgnored()
    {
        WriteConfig($$"""
        {
          {{TwoProviders}},
          "agents": {
            "unbounded": { "briefing": "Runs long.", "maxTurns": 0 },
            "typo":      { "briefing": "Oops.", "maxTurns": -1 }
          }
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Equal(0, s.AgentTypes["unbounded"].MaxTurns);
        Assert.Null(s.AgentTypes["typo"].MaxTurns);   // ignored, so it inherits
        Assert.Contains(s.Warnings, w => w.Contains("maxTurns", StringComparison.Ordinal));
    }

    /// <summary>
    /// OFF UNLESS ASKED. Writing to a cache is billed on the providers that need a breakpoint —
    /// Anthropic at 1.25x normal input, Gemini at input plus storage — so a config that says nothing
    /// must not be opted in.
    /// </summary>
    [Fact]
    public void CacheControl_DefaultsToFalse_WhenTheFieldIsAbsent()
    {
        WriteConfig("""
        {
          "providers": {
            "p": { "kind": "openai-compatible", "baseUrl": "http://x/v1", "apiKey": "k", "model": "m" }
          },
          "defaultProvider": "p"
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.False(s.Providers["p"].CacheControl);
    }

    /// <summary>And true when it does ask — the whole point of the field.</summary>
    [Fact]
    public void CacheControl_IsReadFromConfig()
    {
        WriteConfig("""
        {
          "providers": {
            "p": { "kind": "openai-compatible", "baseUrl": "http://x/v1", "apiKey": "k",
                   "model": "m", "cacheControl": true }
          },
          "defaultProvider": "p"
        }
        """);
        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.True(s.Providers["p"].CacheControl);
    }
}
