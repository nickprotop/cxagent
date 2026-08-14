using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

public class ProviderResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-pr-" + Guid.NewGuid().ToString("N"));
    public ProviderResolutionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, "config.json"), json);
    private AppPaths Paths() => new(_dir);
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void Mock_ReturnsMockProvider_IgnoringConfig()
    {
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: true);
        Assert.True(r.HasProvider);
        Assert.Equal("mock", r.Provider!.ProviderId);
    }

    /// <summary>
    /// Regression (P5b live-drive): --mock must be a WORKING demo path, not a bare test double.
    /// MockLlmProvider is queue-driven — ChatAsync/ChatStreamAsync call Queue.Dequeue(), which
    /// throws InvalidOperationException("Queue empty.") when nothing was enqueued. The resolver
    /// previously returned `new MockLlmProvider()` unseeded, so every --mock goal submission failed
    /// with a red "✗ Queue empty." in the chat and NO jobs were ever created (SetJobs runs only
    /// after PlanCompiler.BuildDag, which needs a plan). Seed a canned create_plan so --mock
    /// actually decomposes and runs a DAG.
    /// </summary>
    [Fact]
    public async Task Mock_IsSeeded_WithARunnablePlan()
    {
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: true);
        var resp = await r.Provider!.ChatAsync(new List<CxAgent.Core.Models.ChatMessage>(), null, default);

        var call = Assert.Single(resp.ToolCalls);
        Assert.Equal("create_plan", call.Name);

        var jobs = call.Arguments.GetProperty("jobs");
        Assert.True(jobs.GetArrayLength() >= 2, "seeded plan should have multiple jobs so the panel shows several blocks");

        // Every job must name a built-in plugin type, or the run fails at execution.
        var builtins = CxAgent.Core.Plugins.PluginRegistry.CreateWithBuiltins();
        foreach (var j in jobs.EnumerateArray())
            Assert.True(builtins.TryGet(j.GetProperty("type").GetString()!, out _),
                $"seeded plan uses unknown plugin type '{j.GetProperty("type").GetString()}'");
    }

    /// <summary>
    /// Regression: orchestrator token budgets parsed and round-tripped correctly but were DEAD IN
    /// PRODUCTION — `AgentHost` accepts an `OrchestratorSettings?` and AppBootstrap never passed one,
    /// because `ProviderResolution` dropped the `ProviderSettings` that `ProviderResolver.Resolve`
    /// already had in hand. The cap was unit-tested and unenforced: exactly the shape of bug that a
    /// green suite hides. The resolution must carry the settings so the live runner can be bounded.
    /// </summary>
    [Fact]
    public void Resolve_CarriesOrchestratorSettings_SoTheLiveRunnerCanBeBounded()
    {
        Write("""
        { "providers": { "claude": { "kind":"anthropic", "apiKey":"sk", "model":"m" } },
          "defaultProvider":"claude",
          "orchestrator": { "maxTurns": 42 } }
        """);

        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: false);

        Assert.True(r.HasProvider);
        Assert.NotNull(r.Orchestrator);
        Assert.Equal(42, r.Orchestrator!.MaxTurns);
    }

    /// <summary>--mock has no config file, so it must resolve unconfigured (null), not crash.</summary>
    [Fact]
    public void Mock_HasNoOrchestratorBudgets()
    {
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: true);
        Assert.True(r.HasProvider);
        Assert.Null(r.Orchestrator?.MaxTurns);
    }

    [Fact]
    public void ValidConfig_ReturnsDefaultProvider()
    {
        Write("""
        { "providers": { "claude": { "kind":"anthropic", "apiKey":"sk", "model":"claude-x" } },
          "defaultProvider":"claude" }
        """);
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: false);
        Assert.True(r.HasProvider);
        Assert.Equal("claude", r.Provider!.ProviderId);   // ProviderId = instance name (P4 T8)
    }

    [Fact]
    public void MissingConfig_NoProvider_WithActionableError_NoThrow()
    {
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: false);   // no config.json written
        Assert.False(r.HasProvider);
        Assert.NotEmpty(r.Errors);
        Assert.Contains(r.Errors, e => e.Contains("config.json"));
    }

    [Fact]
    public void InvalidConfig_NoProvider_SurfacesBatchedErrors_NoThrow()
    {
        Write("""{ "providers": { "bad": { "kind":"made-up", "model":"x" } }, "defaultProvider":"ghost" }""");
        var r = ProviderResolver.Resolve(Paths(), NoEnv, useMock: false);
        Assert.False(r.HasProvider);
        Assert.True(r.Errors.Count >= 2);   // batched: unknown kind + dangling default
    }
}
