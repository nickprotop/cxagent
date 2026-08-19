using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The two config levels: <c>llmAgent.tools</c> (S1, beside the embedder's) and
/// <c>agents.&lt;type&gt;.tools</c> (S4).
///
/// <para>Both hold TERMS rather than resolved names, because config is read long before the offered
/// set is known — a skills catalog appears, an embedder injects per session. A set resolved at load
/// would freeze before either.</para>
/// </summary>
public class ToolSelectionConfigTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "toolcfg-" + Guid.NewGuid().ToString("N")[..8]);

    public ToolSelectionConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private ProviderSettings Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return ProviderConfigLoader.LoadAndValidate(new AppPaths(_dir), new Dictionary<string, string>());
    }

    /// <summary>A minimal valid config with the given extra top-level blocks spliced in.</summary>
    private static string Config(string extra) =>
        "{ \"providers\": { \"p\": { \"kind\": \"openai-compatible\", \"model\": \"m\", "
        + "\"apiKey\": \"k\", \"baseUrl\": \"http://x\" } }, " + extra + " }";

    [Fact]
    public void LlmAgentToolsIsReadAsTerms()
    {
        // defaultProvider IS REQUIRED HERE, unlike the parse-only tests above: resolution asks the
        // registry for its default and fails without one.
        var s = Load("{ \"providers\": { \"p\": { \"kind\": \"openai-compatible\", "
            + "\"model\": \"m\", \"apiKey\": \"k\", \"baseUrl\": \"http://x\" } }, "
            + "\"defaultProvider\": \"p\", "
            + "\"llmAgent\": { \"tools\": [\"inherited\", \"-run_shell\"] } }");

        Assert.Equal(["inherited", "-run_shell"], s.Tools!.Terms);
    }

    [Fact]
    public void AgentTypeToolsIsReadAsTerms()
    {
        var s = Load(Config("\"agents\": { \"explore\": { \"tools\": [\"inherited\", \"-write_file\"] } }"));

        Assert.Equal(["inherited", "-write_file"], s.AgentTypes["explore"].Tools!.Terms);
    }

    [Fact]
    public void ABuiltinTypeMaySetToolsUnlikeBriefing()
    {
        // A briefing on a shipped type is ignored with a warning — it is text the code depends on.
        // A toolset is a property of THIS DEPLOYMENT, which cxagent cannot know, so it is kept.
        var s = Load(Config("\"agents\": { \"explore\": { \"briefing\": \"mine\", "
            + "\"tools\": [\"inherited\", \"-write_file\"] } }"));

        Assert.Equal(["inherited", "-write_file"], s.AgentTypes["explore"].Tools!.Terms);
        Assert.Contains(s.Warnings, w => w.Contains("briefing is ignored"));
    }

    [Fact]
    public void AMalformedTermWarnsAndIsDroppedRatherThanThrowing()
    {
        // Apply throws per REQUEST, long after load — so an unvalidated bad term would open the
        // session fine and fail every turn. Config warns instead, matching its own contract.
        var s = Load(Config("\"llmAgent\": { \"tools\": [\"inherited\", \"*run_shell\", \"-grep\"] }"));

        Assert.Equal(["inherited", "-grep"], s.Tools!.Terms);
        Assert.Contains(s.Warnings, w => w.Contains("*run_shell") && w.Contains("not understood"));
    }

    [Fact]
    public void AnUnknownToolNameIsNotWarnedAbout()
    {
        // Names arrive late — an injected tool, a skill that appears — so a name matching nothing
        // today may match tomorrow, and an unmatched name grants nothing. Only the grammar is checked.
        var s = Load(Config("\"llmAgent\": { \"tools\": [\"inherited\", \"-not_a_real_tool\"] }"));

        Assert.Equal(["inherited", "-not_a_real_tool"], s.Tools!.Terms);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("not_a_real_tool"));
    }

    [Fact]
    public void AnEmptyArrayMeansNoToolsNotNoOpinion()
    {
        // `[]` is the one explicit way to say "nothing". Returning null would make it unsayable.
        var s = Load(Config("\"llmAgent\": { \"tools\": [] }"));

        Assert.NotNull(s.Tools);
        Assert.Empty(s.Tools!.Terms);
    }

    [Fact]
    public void AnAbsentToolsKeyMeansNoOpinion()
    {
        var s = Load(Config("\"llmAgent\": { }"));

        Assert.Null(s.Tools);
    }

    [Fact]
    public void LlmAgentToolsReachesTheSessionsAgent()
    {
        // PARSING IS NOT REACHING. Every test above proves config READS the key; none proved it is
        // applied, and the chain is four hops — settings.Tools, Catalog.Tools, resolution.Tools,
        // then Then() at SessionFactory. CONFIG.md documents this key, so something has to fail if
        // a hop is dropped.
        // defaultProvider IS REQUIRED HERE, unlike the parse-only tests above: resolution asks the
        // registry for its default and fails without one, so Config() alone is not enough.
        var s = Load(Config("\"defaultProvider\": \"p\", "
            + "\"llmAgent\": { \"tools\": [\"inherited\", \"-run_shell\"] }"));

        Assert.Equal(["inherited", "-run_shell"], s.Tools!.Terms);

        // useMock: false, DELIBERATELY. The mock arm returns a fixed catalog before reading config
        // at all, so a test that passed true would exercise none of the chain and pass however
        // broken it was. The config names an http:// base URL that is never contacted: resolution
        // builds a registry and reads settings, it does not call the provider.
        var resolved = ConfigResolver.Resolve(
            new AppPaths(_dir), new Dictionary<string, string>(), useMock: false);

        Assert.True(resolved.Errors.Count == 0,
            "resolution failed: " + string.Join("; ", resolved.Errors));
        Assert.NotNull(resolved.Tools);
        Assert.Equal(["inherited", "-run_shell"], resolved.Tools!.Terms);
    }

    [Fact]
    public void TheAllTermIsAcceptedGrammar()
    {
        var s = Load(Config("\"agents\": { \"wide\": { \"briefing\": \"b\", "
            + "\"tools\": [\"all\", \"-run_shell\"] } }"));

        Assert.Equal(["all", "-run_shell"], s.AgentTypes["wide"].Tools!.Terms);
        Assert.DoesNotContain(s.Warnings, w => w.Contains("not understood"));
    }
}

/// <summary>
/// That a type's tools actually REACH the child — parsing is not applying.
///
/// <para>The config tests above prove `agents.&lt;type&gt;.tools` is read. Nothing there proves
/// SubAgentFactory does anything with it: dropping `type?.Tools` at the Create call left the whole
/// suite green, which is how this file came to exist.</para>
/// </summary>
public class AgentTypeToolsReachTheChildTests
{
    private sealed class Answering : ILlmProvider
    {
        public string ProviderId => "t";
        public string ModelId => "t-model";
        public string DisplayName => "T";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;
        public List<ToolDefinition>? LastTools { get; private set; }

        public Task<LlmResponse> ChatAsync(List<CxAgent.Core.Models.ChatMessage> messages,
            List<ToolDefinition>? tools, CancellationToken ct)
        {
            LastTools ??= tools;
            return Task.FromResult(new LlmResponse
            {
                Text = "done",
                StopReason = "end_turn",
                Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
            });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
            List<CxAgent.Core.Models.ChatMessage> messages, List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    [Fact]
    public async Task ATypesToolsNarrowTheChildItCreates()
    {
        var provider = new Answering();
        var factory = new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
        {
            Provider = provider,
            Plugins = PluginRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 5,
        });

        var type = new CxAgent.Core.Agents.AgentType("explore", "b",
            CxAgent.Core.Agents.TypeRouting.Inherited)
        {
            Tools = new ToolSelection([Tool.Inherited, Tool.Not.RunShell]),
        };

        var child = factory.Create(type: type);
        await child.Agent.SendAsync("go", CancellationToken.None);

        var offered = (provider.LastTools ?? []).Select(t => t.Name).ToList();

        Assert.DoesNotContain(Tool.RunShell, offered);
        Assert.Contains(Tool.ReadFile, offered);
    }
}
