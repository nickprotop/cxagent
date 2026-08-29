using System.Text.Json;
using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Jobs;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The mechanisms keyed on tool names — the category that fails SILENTLY.
///
/// <para>These tell nobody anything false. They stop working, and no user report will ever say "the
/// build challenge no longer fires". A narrowed session degrades in ways nothing surfaces, which is
/// why they get tests rather than a comment.</para>
///
/// <para>Two of the three are correct as no-ops and one is not, and the difference is whether the
/// mechanism ACTS or merely OBSERVES. IsWrite and the build/test tracking observe what happened, so
/// with nothing to observe they have nothing to do. WithPlanPath acts BEFORE the child runs — it
/// injects an instruction — so silence there means telling a child to do the impossible.</para>
/// </summary>
public class ToolSelectionMechanismTests
{
    private sealed class Recording : ILlmProvider
    {
        private readonly Queue<LlmResponse> _queued = new();
        /// <summary>Every message seen across ALL calls. Keeping only the last would miss the tool
        /// results, which arrive on the call AFTER the one that requested them.</summary>
        public List<ChatMessage> Seen { get; } = [];

        public List<ChatMessage>? LastMessages { get; private set; }

        public void Enqueue(LlmResponse r) => _queued.Enqueue(r);

        public string ProviderId => "rec";
        public string ModelId => "rec-model";
        public string DisplayName => "Rec";
        public bool SupportsToolCalling => true;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            LastMessages = [.. messages];
            Seen.AddRange(messages);
            return Task.FromResult(_queued.Count > 0 ? _queued.Dequeue() : Done());
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var r = await ChatAsync(messages, tools, ct);
            yield return new LlmStreamChunk(r.Text, null, true);
        }
    }

    private static LlmResponse Done() => new()
    {
        Text = "done",
        StopReason = "end_turn",
        Usage = new LlmUsage { InputTokens = 1, OutputTokens = 1 },
    };

    // --- IsWrite and build/test tracking: inert is correct, and needs no test ----------
    //
    // Both are set only by a call that RAN: `wrote` by a successful write (Agent.cs:1229), the
    // build/test verdicts by a run_shell that looked like a build (:1176). A withheld tool never
    // reaches either — ToolRefusalTests.AWithheldInjectedToolDoesNotRUN pins that it does not
    // execute — so the mechanisms observe nothing and do nothing.
    //
    // NOTHING TO ASSERT THAT IS NOT ALREADY ASSERTED. A test here would drive a whole turn to
    // observe an absence that follows from a fact another test already pins, and its failure would
    // point at the wrong place. The decision is recorded rather than tested, which is honest:
    // these two are inert BECAUSE the dispatch guard works, not because of anything done here.
    //
    // WithPlanPath below is the one that needed a change, and it is the one with tests.

    // --- WithPlanPath: silence is NOT correct ------------------------------------------

    [Fact]
    public void APlanPathIsNotHandedToAChildThatCannotWrite()
    {
        // IT ACTS RATHER THAN OBSERVES. The mechanism injects "write your plan file to <path>" and
        // then reports what is on disk — so a child that cannot write is told to do the impossible
        // and then reported as having failed. The contradiction is guaranteed, not possible.
        var provider = new Recording();
        var spawner = new SubAgentSpawner(
            FactoryWith(new ToolSelection([Tool.Inherited, Tool.Not.WriteFile, Tool.Not.ReplaceInFile]), provider),
            PlannerCatalog());

        Assert.DoesNotContain("plans/", PromptGivenToChild(spawner, provider));
    }

    [Fact]
    public void APlanPathIS_HandedToAChildThatCanWrite()
    {
        // No collateral damage: the mechanism is untouched when nothing was narrowed.
        var provider = new Recording();
        var spawner = new SubAgentSpawner(FactoryWith(null, provider), PlannerCatalog());

        Assert.Contains("plans/", PromptGivenToChild(spawner, provider));
    }

    /// <summary>
    /// TWO PLANNERS WITH ONE LABEL GET TWO PATHS. Slugging the label separates planners the parent
    /// labelled differently; it does nothing for the case a parent actually produces when it splits
    /// one job in two and describes both the same way. Both were then told to write one file, both
    /// did, and the survivor was whichever finished last — a whole planner run lost with no error
    /// anywhere, which is the failure the slug was introduced to prevent.
    /// </summary>
    [Fact]
    public void ASecondPlannerWithTheSameLabelGetsItsOwnPath()
    {
        var dir = Directory.CreateTempSubdirectory("planpath-").FullName;
        try
        {
            var provider = new Recording();
            var spawner = new SubAgentSpawner(
                FactoryWith(null, provider, workingDir: dir), PlannerCatalog());

            var first = PromptGivenToChild(spawner, provider);
            Assert.Contains("plans/plan-it.md", first, StringComparison.Ordinal);

            // THE FIRST PLANNER'S FILE NOW EXISTS, which is what a running planner leaves behind
            // before the second is spawned.
            Directory.CreateDirectory(Path.Combine(dir, "plans"));
            File.WriteAllText(Path.Combine(dir, "plans", "plan-it.md"), "the first plan");

            var second = PromptGivenToChild(spawner, provider);

            Assert.Contains("plans/plan-it-2.md", second, StringComparison.Ordinal);
            Assert.Equal("the first plan",
                File.ReadAllText(Path.Combine(dir, "plans", "plan-it.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static AgentTypeCatalog PlannerCatalog() =>
        new(new Dictionary<string, AgentTypeConfig>(), null);

    private static SubAgentFactory FactoryWith(
        ToolSelection? selection, Recording provider, string? workingDir = null) =>
        new(new SubAgentFactory.SubAgentRuntime
        {
            WorkingDir = workingDir,
            Provider = provider,
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 5,
            ToolSelection = selection,
        });

    /// <summary>
    /// Spawns a planner and returns the child's system prompt.
    ///
    /// <para>The caller context is rendered into it, so the plan-path instruction is observable
    /// without a test seam — and observing what the MODEL is told is closer to the thing that
    /// matters than reading a field would be.</para>
    /// </summary>
    private static string PromptGivenToChild(SubAgentSpawner spawner, Recording provider)
    {
        spawner.TryInvokeAsync(
            new ToolCall
            {
                Id = "c",
                Name = Tool.Agent,
                Arguments = JsonSerializer.SerializeToElement(
                    new { description = "plan it", prompt = "plan it", type = "planner" }),
            },
            onChild: null, CancellationToken.None).GetAwaiter().GetResult();

        return string.Join("\n", (provider.LastMessages ?? []).Select(m => m.Content ?? ""));
    }
}
