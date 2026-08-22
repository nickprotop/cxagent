using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE CACHE KEY IS A SECURITY PROPERTY, NOT AN OPTIMISATION. Keying on (kind, subject) alone lets a
/// benign first write to a path cache ALLOW, and a later overwrite of the SAME path with DIFFERENT
/// content reuse that verdict without the classifier ever seeing its diff — defeating the
/// capped-diff defence entirely. <see cref="DIFFERENT_CONTENT_TO_THE_SAME_PATH_IS_A_DIFFERENT_ACTION"/>
/// is the regression test for exactly that hole; see <see cref="ActionClassifier"/> for the key.
/// </summary>
public class ClassifierCacheTests
{
    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    [Fact]
    public void TheSameActionTwiceInATurn_CallsTheModelOnce()
    {
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        _ = classifier.JudgeAsync(request, default).Result;
        _ = classifier.JudgeAsync(request, default).Result;

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void DIFFERENT_CONTENT_TO_THE_SAME_PATH_IS_A_DIFFERENT_ACTION()
    {
        // THE CACHE-POISONING HOLE. Keying on (kind, subject) alone lets a benign first write to
        // config.json cache ALLOW, and a later malicious overwrite of the same path reuse it WITHOUT the
        // classifier seeing its diff — defeating the capped-diff defence entirely.
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);

        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "hello" } }, default).Result;
        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt") with { Facts = new() { Diff = "rm -rf" } }, default).Result;

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void ResetTurnState_ClearsTheCache()
    {
        var provider = new CountingProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        _ = classifier.JudgeAsync(request, default).Result;
        classifier.ResetTurnState();
        _ = classifier.JudgeAsync(request, default).Result;

        Assert.Equal(2, provider.Calls);
    }

    /// <summary>Only real verdicts are cached — a provider that throws must be asked again every
    /// time, never a cached ASK standing in for a transient blip a retry might have resolved.</summary>
    [Fact]
    public async Task AFailure_IsNeverCached()
    {
        var provider = new ThrowingCountingProvider();
        var classifier = new ActionClassifier(provider);
        var request = FileWrite("/tmp/a.txt");

        await classifier.JudgeAsync(request, default);
        await classifier.JudgeAsync(request, default);

        Assert.Equal(2, provider.Calls);
    }

    /// <summary>
    /// THE ACTUAL TURN BOUNDARY, exercised end to end — not just ActionClassifier.ResetTurnState()
    /// in isolation (the three tests above), but Session.RunTurnAsync (Session.Turn.cs) actually
    /// calling it. A verdict must decide ONE action, never a whole session: the same write repeated
    /// in a SECOND turn is a different decision to make, even though it hashes to the same cache
    /// key, because a turn boundary is where the goal, requester and project-instructions context
    /// that also feed the classifier can change. Proven by call count on the CLASSIFIER's own
    /// provider — a fake that answered "same key, no call" without the reset actually running would
    /// make this fail with 1, not the 2 asserted.
    /// </summary>
    [Fact]
    public async Task TheSameWriteInTwoTurns_ClassifiesTwice()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-cache-turn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var rules = new PermissionRulesStore(new AppPaths(dir));
            // AUTO MODE, TRUSTED FOLDER — the two preconditions for the classifier running at all
            // (PermissionPolicy consults it only once the silent/boundary/rule paths have all
            // declined to answer on their own).
            rules.SetTrust(dir, TrustState.Trusted);

            // THE AGENT'S OWN MODEL — separate from the classifier's, so ChatCallCount on THIS one
            // says nothing about caching; only the classifier provider's count matters here.
            var agent = new MockLlmProvider();
            var target = Path.Combine(dir, "a.txt");
            var writeArgs = new { path = target, content = "hello" };
            // TWO IDENTICAL TURNS: same tool call, same content, both times. A cache keyed on
            // anything narrower than the full rendered action would make the second call free even
            // across the turn boundary — which is exactly the behaviour under test.
            agent.EnqueueResponse(LlmResponse.WithToolCall("write_file", writeArgs));
            agent.EnqueueResponse(new LlmResponse { Text = "done", StopReason = "end_turn" });
            agent.EnqueueResponse(LlmResponse.WithToolCall("write_file", writeArgs));
            agent.EnqueueResponse(new LlmResponse { Text = "done again", StopReason = "end_turn" });

            var judge = new CountingProvider("ALLOW");
            var decider = PermissionDecider.ForTesting(
                new PermissionPolicy(dir, rules, EditMode.Auto), rules, notice: null,
                (_, _, _) => Task.FromResult(PermissionChoice.Deny));
            decider.Classifier = new ActionClassifier(judge);

            using var manager = SessionManager.Create(new ProcessSetup
            {
                Paths = new AppPaths(Path.Combine(dir, "state")),
                Config = ResolvedConfig.ForTesting(agent),
                BuildGate = _ => decider,
            });

            var session = manager.Open(dir, new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
                Policy = new PermissionPolicy(dir, rules, EditMode.Auto),
            });

            await session.SendAndWait("write hello to a.txt");
            await session.SendAndWait("write hello to a.txt again");

            // TWO TURNS, TWO CALLS. A cache that survived the boundary would show 1 here — that is
            // the failure this test exists to catch; the within-a-turn case (1 call for a repeat) is
            // already covered by TheSameActionTwiceInATurn_CallsTheModelOnce above.
            Assert.Equal(2, judge.Calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- fakes ------------------------------------------------------------------------------

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

    private sealed class ThrowingCountingProvider : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "throwing-counting";
        public string DisplayName => "ThrowingCounting";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromException<LlmResponse>(new HttpRequestException("down"));
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
