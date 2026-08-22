using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TASK 11 FOR CHILDREN: a sub-agent speculates on a classifier verdict exactly like its parent
/// does, once <see cref="SubAgentFactory.SubAgentRuntime.Classifier"/> is set. Without this a child's
/// gated tool calls were the one place left paying the classifier's full round trip SYNCHRONOUSLY —
/// every other call site had already been moved ahead of the gate by <see cref="Agent"/>'s own
/// speculation loop; a delegated <c>run_shell</c> was the exception, because <see
/// cref="SubAgentFactory.Create"/> built its child with no classifier to speculate with at all.
///
/// <para>THE PROPERTY THAT ACTUALLY REGRESSES IS THE CALL COUNT, not any internal cache state: a
/// speculated verdict costs the classifier's underlying provider exactly ONE call (the speculative
/// one, reused by the real request), where a synchronous-only path costs one call PER gated tool call
/// regardless of speculation. Asserting on the count is what <see cref="SpeculativeClassifierTests"/>
/// already does for the parent; this file is the same idiom one layer down.</para>
/// </summary>
public class SubAgentSpeculationTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-subspec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A trusted, auto-mode policy — the floor <see cref="PermissionPolicy.EffectFor"/>
    /// requires before a classifier is consulted at all. See <c>ReviewEffectTests.TrustedAuto</c>,
    /// the same construction one file over.</summary>
    private static PermissionPolicy TrustedAuto(string root)
    {
        var rules = new PermissionRulesStore(new AppPaths(MakeTempDir()));
        rules.SetTrust(root, TrustState.Trusted);
        return new PermissionPolicy(root, rules, EditMode.Auto);
    }

    private static SubAgentFactory NewFactory(
        MockLlmProvider childProvider, ActionClassifier? classifier, PermissionPolicy? policy) =>
        new(new SubAgentFactory.SubAgentRuntime
        {
            Provider = childProvider,
            Executors = JobRegistry.CreateWithBuiltins(),
            Ledger = new TokenLedger(),
            MaxTurns = 50,
            CompressAbove = 40_000,
            Policy = policy,
            Classifier = classifier,
            WorkingDir = policy?.Root,
        });

    /// <summary>A provider whose one queued response is a gated write, followed by a plain answer
    /// so the turn can finish once the write's tool result comes back.</summary>
    private static MockLlmProvider ProviderThatWrites(string path)
    {
        var provider = new MockLlmProvider();
        provider.EnqueueResponse(
            LlmResponse.WithToolCall("write_file", new { path, content = "x" }));
        provider.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
        return provider;
    }

    [Fact]
    public async Task AChildsToolCall_WarmsTheClassifiersCache()
    {
        var root = MakeTempDir();
        var policy = TrustedAuto(root);
        var classifierProvider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(classifierProvider);

        var child = NewFactory(ProviderThatWrites(Path.Combine(root, "a.txt")), classifier, policy)
            .Create();
        await child.Agent.SendAsync("write the file", CancellationToken.None);

        // ONE CALL, NOT TWO. The child's own dispatch loop parses the write_file call and starts
        // Speculate before PermissionGatedExecutor's synchronous JudgeAsync ever runs — if the child
        // were built with no classifier, or built with one it never told Agent about, this would be
        // 2: one wasted speculative call never made, one full round trip paid at the gate.
        Assert.Equal(1, classifierProvider.Calls);
    }

    /// <summary>THE NULL PATH — the graceful fallback the parent already has. A child built with no
    /// classifier (headless, most tests, a fixed AllowAll/DenyAll gate upstream) must still run to
    /// completion; it just never speculates.</summary>
    [Fact]
    public async Task AChildWithNoClassifier_StillRuns()
    {
        var root = MakeTempDir();
        var policy = TrustedAuto(root);

        var child = NewFactory(ProviderThatWrites(Path.Combine(root, "a.txt")), classifier: null, policy)
            .Create();
        var result = await child.Agent.SendAsync("write the file", CancellationToken.None);

        Assert.Equal(SendOutcome.Completed, result.Outcome);
        Assert.Equal("done", result.Text);
    }

    // ---- fakes (same shape as SpeculativeClassifierTests.CountingProvider) --------------------

    private sealed class CountingProvider(string? reply) : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "counting";
        public string DisplayName => "Counting";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse { Text = reply });
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
