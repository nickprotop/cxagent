using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// TASK 12: ONE MODEL, TWO PROMPTS, NO CONFIG CHANGE. A single-token triage pass answers the common
/// case in one cheap call; only what it flags pays for a second, reasoning call. Measured behind this
/// split: single-token triage alone had an 8.5% false-positive rate (over-blocking ordinary, safe
/// actions); adding the reasoning stage on flagged actions brought that down to 0.4%. Running the
/// expensive stage on EVERY action would buy the same accuracy at several times the cost — the whole
/// point of triage is that most actions never reach stage two.
///
/// <para>See <see cref="ActionClassifier"/> for what "flagged" means (triage verdict != Allow) and
/// why: a triage ALLOW is cheap to trust (a false ALLOW here is a false negative, not the failure mode
/// task 12 exists to fix), while a triage ASK or DENY is exactly the case with room for a false
/// positive — the one stage two is tuned to catch.</para>
/// </summary>
public class TwoStageClassifierTests
{
    private static PermissionRequest FileWrite(string path) =>
        new(PermissionKind.FileWrite, path, path);

    [Fact]
    public void AnUnflaggedActionCostsOneCall()
    {
        // The point of a triage stage: the common case never reaches the expensive one.
        var provider = new ScriptedProvider("ALLOW");

        _ = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void AFlaggedActionGetsASecondOpinion()
    {
        // Stage one is tuned to over-block; stage two is what takes the false-positive rate down. A
        // flagged action that stopped at stage one would make auto mode useless.
        var provider = new ScriptedProvider("ASK", "ALLOW");

        var decision = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(2, provider.Calls);
        Assert.Equal(ClassifierVerdict.Allow, decision.Verdict);
    }

    [Fact]
    public void TheSecondStageSharesTheFirstStagesMessages()
    {
        // PREFIX SHARING IS THE COST MODEL. If stage two used a different system prompt the cache would
        // miss and the "nearly free" claim would be void.
        var provider = new ScriptedProvider("ASK", "ALLOW");

        _ = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(provider.Sent[0][0].Content, provider.Sent[1][0].Content);   // same system message
        Assert.True(provider.Sent[1].Count > provider.Sent[0].Count);             // a continuation
    }

    [Fact]
    public void ADenyFromTriageAlsoGetsASecondOpinion()
    {
        // Flagged means "not a clean ALLOW" — DENY is the other consequential verdict a false
        // positive could hide behind, so it goes through stage two exactly like ASK does.
        var provider = new ScriptedProvider("DENY", "ALLOW");

        var decision = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(2, provider.Calls);
        Assert.Equal(ClassifierVerdict.Allow, decision.Verdict);
    }

    [Fact]
    public void StageTwoIsWhereTheReasonComesFrom()
    {
        var provider = new ScriptedProvider("ASK", "DENY: writes outside the project");

        var decision = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(ClassifierVerdict.Deny, decision.Verdict);
        Assert.Equal("writes outside the project", decision.Reason);
    }

    // A SHORT INJECTED DEADLINE, NOT THE REAL 10s. These two tests exist to prove a hung stage still
    // yields Ask — that property does not need a real 10-second wait to demonstrate, and the suite
    // paying it twice (once per stage under test) was what pushed the whole run from ~7s to ~20s,
    // erasing the headroom between "normal" and this repo's 20s "that's a hang" convention. The
    // constructor's stageDeadline parameter exists for exactly this.
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AStageOneTimeoutMeansAsk()
    {
        var provider = new TimeoutOnFirstCallProvider();

        var decision = await new ActionClassifier(provider, ShortDeadline)
            .JudgeAsync(FileWrite("/tmp/a.txt"), default);

        Assert.Equal(ClassifierVerdict.Ask, decision.Verdict);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task AStageTwoTimeoutMeansAsk()
    {
        var provider = new TimeoutOnSecondCallProvider();

        var decision = await new ActionClassifier(provider, ShortDeadline)
            .JudgeAsync(FileWrite("/tmp/a.txt"), default);

        Assert.Equal(ClassifierVerdict.Ask, decision.Verdict);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void AnUnparseableStageOneAnswerMeansAsk()
    {
        var provider = new ScriptedProvider("banana");

        var decision = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(ClassifierVerdict.Ask, decision.Verdict);
    }

    [Fact]
    public void AnUnparseableStageTwoAnswerMeansAsk()
    {
        // Stage one flags (ASK), stage two comes back nonsense — the flag must not fall back to
        // stage one's own verdict; an unparseable second opinion is still a failure, and every
        // failure here means Ask.
        var provider = new ScriptedProvider("ASK", "banana");

        var decision = new ActionClassifier(provider).JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;

        Assert.Equal(ClassifierVerdict.Ask, decision.Verdict);
    }

    [Fact]
    public void TheTriageFlagRateCounterCountsFlaggedActionsOnly()
    {
        var provider = new ScriptedProvider("ALLOW");
        var classifier = new ActionClassifier(provider);
        _ = classifier.JudgeAsync(FileWrite("/tmp/a.txt"), default).Result;
        Assert.Equal(0, classifier.TriageFlagCount);

        var flagged = new ActionClassifier(new ScriptedProvider("ASK", "ALLOW"));
        _ = flagged.JudgeAsync(FileWrite("/tmp/b.txt"), default).Result;
        Assert.Equal(1, flagged.TriageFlagCount);
    }

    // ---- fakes ------------------------------------------------------------------------------

    /// <summary>Answers one scripted reply per call, in order, and records every message list it was
    /// sent — <see cref="Sent"/> is what proves stage two shares stage one's system message.</summary>
    private sealed class ScriptedProvider(params string?[] replies) : ILlmProvider
    {
        private int _index;
        public int Calls { get; private set; }
        public List<List<ChatMessage>> Sent { get; } = new();

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            // SNAPSHOT, NOT THE LIVE LIST — JudgeAsync reuses and extends the same List<ChatMessage>
            // instance across both stages (that reuse is what keeps the two calls' shared prefix
            // byte-identical). Storing the reference itself would make Sent[0] "grow" retroactively
            // to match Sent[1] once stage two appends to it, which defeats the whole point of
            // recording what each call actually saw.
            Sent.Add(new List<ChatMessage>(messages));
            var reply = _index < replies.Length ? replies[_index] : replies[^1];
            _index++;
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

    private sealed class TimeoutOnFirstCallProvider : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "timeout-first";
        public string DisplayName => "TimeoutFirst";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }

        public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
            List<ToolDefinition>? tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TimeoutOnSecondCallProvider : ILlmProvider
    {
        public int Calls { get; private set; }

        public string ProviderId => "timeout-second";
        public string DisplayName => "TimeoutSecond";
        public string ModelId => "test-model";
        public bool SupportsToolCalling => false;
        public bool SupportsStreaming => false;

        public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
            CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) return new LlmResponse { Text = "ASK" };
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
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
