using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A whole configuration written in code, mirroring config.json rather than the runtime.
///
/// <para>WHAT IT REPLACES. Authoring a ResolvedConfig by hand states the same fact several times:
/// the provider appears both alone and inside the catalog, its name as InstanceName and as a
/// dictionary key, its window in both places. A caller can make those disagree and nothing stops
/// them. Here each fact appears once.</para>
/// </summary>
public class AgentConfigTests
{
    /// <summary>
    /// THE SHAPE, against a real config: this is the machine's own config.json — three providers,
    /// a default, a classifier, two agent-type overrides, one MCP server and a turn budget — with
    /// every value stated exactly once.
    /// </summary>
    private static AgentConfig Machine => new()
    {
        Models =
        {
            ["local"] = new(ProviderKind.OpenAiCompatible, "qwen3.6-35b-a3b-ud-iq4_xs.gguf")
                        { BaseUrl = "http://localhost:8771/v1", ContextWindow = 212_992 },

            ["openrouter"] = new(ProviderKind.OpenAiCompatible, "google/gemini-2.5-flash-lite")
                             { BaseUrl = "https://openrouter.ai/api/v1", ContextWindow = 1_048_576,
                               ApiKey = "key", CacheControl = true,
                               Headers = { ["X-Title"] = "cxagent" } },

            ["deepseek"] = new(ProviderKind.OpenAiCompatible, "deepseek/deepseek-v3.2")
                           { BaseUrl = "https://openrouter.ai/api/v1", ContextWindow = 163_840,
                             ApiKey = "key", Headers = { ["X-Title"] = "cxagent" } },
        },

        DefaultModel = "local",
        Classifier = "local",
        MaxTurns = 300,

        Agents =
        {
            ["explore"] = new() { MaxTurns = 30 },
            ["planner"] = new() { MaxTurns = 40 },
        },

        Mcp =
        {
            ["context7"] = new("npx", "-y", "@upstash/context7-mcp"),
        },
    };

    [Fact]
    public void ItResolvesToTheDefaultModel()
    {
        var resolved = Machine.Resolve();

        Assert.Empty(resolved.Errors);
        Assert.True(resolved.HasProvider);
        Assert.Equal("local", resolved.InstanceName);
        Assert.Equal(212_992, resolved.ContextWindow);
        Assert.Equal("qwen3.6-35b-a3b-ud-iq4_xs.gguf", resolved.Provider!.ModelId);
    }

    // THE CATALOG CARRIES ALL THREE, so /model can offer the others and a session can switch.
    [Fact]
    public void EveryModelReachesTheCatalog()
    {
        var resolved = Machine.Resolve();

        // ORDER-INDEPENDENT, because InstanceNames is a dictionary's keys and its order is not a
        // promise. Sorting by a hand-written rank would pass even if a name were missing.
        Assert.Equal(3, resolved.Providers!.InstanceNames.Count);
        Assert.Contains("local", resolved.Providers.InstanceNames);
        Assert.Contains("openrouter", resolved.Providers.InstanceNames);
        Assert.Contains("deepseek", resolved.Providers.InstanceNames);

        Assert.Equal(1_048_576, resolved.Providers.InstanceWindows["openrouter"]);
    }

    // THE REST OF THE FILE COMES TOO — classifier, budget, agent types, MCP.
    [Fact]
    public void EverythingElseIsCarried()
    {
        var resolved = Machine.Resolve();

        Assert.Equal("local", resolved.ClassifierInstance);
        Assert.Equal(300, resolved.Orchestrator!.MaxTurns);
        Assert.Equal(40, resolved.AgentTypes["planner"].MaxTurns);
        Assert.Equal(["npx", "-y", "@upstash/context7-mcp"], resolved.McpServers["context7"].Command);
    }

    // ONE MODEL NEEDS NO DEFAULT, because there is nothing to choose between.
    [Fact]
    public void ASingleModelIsTheDefault()
    {
        var resolved = new AgentConfig
        {
            Models = { ["only"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://x/v1" } },
        }.Resolve();

        Assert.Equal("only", resolved.InstanceName);
    }

    // MISTAKES COME BACK AS ERRORS, not exceptions — the caller is usually assembling this from its
    // own settings and wants to say what went wrong.
    [Theory]
    [InlineData("missing", null, "defaultModel")]
    [InlineData("local", "absent", "classifier")]
    public void MistakesAreReported(string? defaultModel, string? classifier, string expected)
    {
        var resolved = new AgentConfig
        {
            Models = { ["local"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://x/v1" } },
            DefaultModel = defaultModel,
            Classifier = classifier,
        }.Resolve();

        Assert.False(resolved.HasProvider);
        Assert.Contains(resolved.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SeveralModelsWithoutADefaultIsAnError()
    {
        var resolved = new AgentConfig
        {
            Models =
            {
                ["a"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://x/v1" },
                ["b"] = new(ProviderKind.OpenAiCompatible, "m") { BaseUrl = "http://y/v1" },
            },
        }.Resolve();

        Assert.Contains(resolved.Errors, e => e.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    // ANTHROPIC TAKES NO BASE URL, which is why Kind is positional and BaseUrl is not: the shape of
    // the entry differs by driver, and inferring the driver from a URL that may not exist is a guess.
    [Fact]
    public void AnthropicNeedsNoBaseUrl()
    {
        var resolved = new AgentConfig
        {
            Models = { ["claude"] = new(ProviderKind.Anthropic, "claude-sonnet-4-5") { ApiKey = "k" } },
        }.Resolve();

        Assert.True(resolved.HasProvider);
        Assert.Equal("claude-sonnet-4-5", resolved.Provider!.ModelId);
    }

    // AND IT DRIVES A SESSION, which is the point of the whole type.
    [Fact]
    public void ItConfiguresAManager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            using var manager = SessionManager.Create(new ProcessSetup
            {
                Paths = new CxAgent.Core.Storage.AppPaths(dir),
                Config = Machine.Resolve(),
            });

            Assert.Equal("local", manager.Config.InstanceName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
