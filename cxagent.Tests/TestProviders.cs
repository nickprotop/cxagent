using CxAgent.Core.Sessions;
using System.Collections.Concurrent;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;

namespace CxAgent.Tests;

/// <summary>
/// Shared fake providers used by AgentHostTests and AppShellE2ETests.
/// Extracted here so there is exactly ONE copy — no duplication between test files.
/// </summary>

/// <summary>
/// A fake ISessionObserver that records everything, so a run can be asserted without a real window.
/// Promoted here from AgentHostTests (where it was private) when OrchestratorLoopTests needed it —
/// same "one copy only" reason this file exists.
///
/// <para><see cref="Errors"/>/<see cref="Messages"/> are collections, not single values: the consult
/// loop can report several things in one run (a cap hit, an unparseable decision, a failed
/// modification) and a last-one-wins field would hide all but the last. <see cref="Error"/> and
/// <see cref="Result"/> are kept as last-value convenience views over those collections so
/// AgentHostTests' existing assertions keep working unchanged.</para>
///
/// <para><see cref="Messages"/> collects EVERY user-visible line — assistant text appended via
/// <see cref="AssistantTextAppended"/>, errors, and the goal result — because the loop's "say why you
/// stopped" contract is about what reached the user, not which sink method carried it.</para>
/// </summary>
public sealed class RecordingSink : ISessionObserver
{
    public readonly List<string> Users = new();
    public readonly ConcurrentQueue<string> AssistantTokens = new();

    /// <summary>Every error reported, in order. Concurrent: the loop's sink calls can land on a
    /// thread-pool thread (a JobTransitioned continuation with no SynchronizationContext installed).</summary>
    public readonly ConcurrentQueue<string> ErrorQueue = new();

    /// <summary>Every user-visible line, in order — assistant text and errors.</summary>
    public readonly ConcurrentQueue<string> MessageQueue = new();

    public List<string> Errors => ErrorQueue.ToList();
    public List<string> Messages => MessageQueue.ToList();

    /// <summary>The most recent error, or null if none was reported (a last-value view over
    /// <see cref="Errors"/>).</summary>
    public string? Error => ErrorQueue.LastOrDefault();

    public void UserTurnAdded(ChatMessageId id, string text) => Users.Add(text);
    public readonly List<string> Notices = new();
    public void Said(string message) => Notices.Add(message);
    public void AssistantTurnBegan(ChatMessageId id) { }

    /// <summary>Turns closed via AssistantTurnEnded — a turn left open spins its thinking indicator forever.</summary>
    public List<long> EndedTurns { get; } = new();

    public void AssistantTurnEnded(ChatMessageId id) => EndedTurns.Add(id.Value);

    public void AssistantTextAppended(ChatMessageId id, string token)
    {
        AssistantTokens.Enqueue(token);
        MessageQueue.Enqueue(token);
    }

    /// <summary>Kept SEPARATE from AssistantTokens: reasoning is a different kind of text, and a fake
    /// that merged the two would let a test assert on body content that was actually thinking.</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> ReasoningTokens = new();

    public void AssistantReasoningAppended(ChatMessageId id, string text) => ReasoningTokens.Enqueue(text);

    public void Failed(string message)
    {
        ErrorQueue.Enqueue(message);
        MessageQueue.Enqueue(message);
    }

    public void AssistantLabelled(ChatMessageId id, string header) { }
}

/// <summary>
/// A provider whose ChatAsync always throws. Promoted here (from AgentHostTests, where it was
/// private) for P11 Task 3's fallback test — SessionCompressor must degrade to truncation, not crash,
/// when the summarising call fails. Not a stand-in for the other two ThrowingProviders in
/// LlmAgentJobPluginTests/ProviderProbeTests: those assert on a SPECIFIC exception shape
/// (LlmProviderException / HttpRequestException) their own tests depend on, so they stay private —
/// this is the generic "just throw" fake shared by every caller that only needs ChatAsync to fail.
/// </summary>
public sealed class ThrowingProvider : ILlmProvider
{
    public string ProviderId => "bad";
    public string DisplayName => "Bad";
    public string ModelId => "test-model";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;
    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => throw new NotImplementedException();
    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmStreamChunk("", null, false);
        throw new LlmProviderException("bad", 401, "secret-vendor-body", "auth failed");
    }
}

/// <summary>
/// Replays a fixed script of responses and records the prompt text it was sent each time. Promoted
/// here (from OrchestratorLoopTests, where it was private) for P11 Task 3's SessionCompressor tests,
/// which need the same "script a reply, assert what was sent" shape ChatAsync already provides —
/// SessionCompressor's summarising call is a plain ChatAsync, not a stream. Every scripted reply must
/// carry Usage explicitly (see the `Usage` helper in callers) so a caller that forgets to meter a call
/// shows up as a zero ledger rather than passing silently.
/// </summary>
public sealed class RecordingProvider : ILlmProvider
{
    private readonly Queue<LlmResponse> _responses;

    public RecordingProvider(params LlmResponse[] responses) => _responses = new Queue<LlmResponse>(responses);

    /// <summary>The flattened text of every message sent, one entry per ChatAsync call.</summary>
    public List<string> Prompts { get; } = new();

    public string ProviderId => "recording";
    public string DisplayName => "Recording";
    public string ModelId => "test-model";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        Prompts.Add(string.Join("\n", messages.Select(m => m.Content)));
        // Running dry means the caller made more calls than the test scripted — a real failure to
        // surface, not a silent default reply.
        if (_responses.Count == 0)
            throw new InvalidOperationException("RecordingProvider ran out of scripted replies.");
        return Task.FromResult(_responses.Dequeue());
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var resp = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(resp.Text, resp.ToolCalls.FirstOrDefault(), IsFinal: true, Usage: resp.Usage);
    }
}

/// <summary>
/// Answers conversationally: prose, no create_plan call. "hello" needs no plan, and neither does a
/// question — this is a legitimate outcome, not a failed goal.
/// </summary>
public sealed class AnswersWithoutPlanningProvider : ILlmProvider
{
    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => Task.FromResult(new LlmResponse { Text = "hello!" });

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmStreamChunk("Hello! ", null, false);
        yield return new LlmStreamChunk("How can I help?", null, false);
        yield return new LlmStreamChunk(null, null, true);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Answers conversationally (no create_plan) AND reports InputTokens, so the context-pressure path
/// has a real measurement to act on. Without usage, AgentHost's `_lastInputTokens` stays null and
/// compression declines — correct behaviour, but it makes a compression test pass for the wrong reason.
/// </summary>
public sealed class AnswersWithUsageProvider : ILlmProvider
{
    private readonly int _inputTokens;
    public AnswersWithUsageProvider(int inputTokens) => _inputTokens = inputTokens;

    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => Task.FromResult(new LlmResponse
        {
            Text = "summary of earlier turns",
            Usage = new LlmUsage { InputTokens = _inputTokens, OutputTokens = 10 },
        });

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmStreamChunk("Hello!", null, false);
        yield return new LlmStreamChunk(null, null, true, new LlmUsage { InputTokens = _inputTokens, OutputTokens = 10 });
        await Task.CompletedTask;
    }
}

/// <summary>
/// An IToolObserver that records rather than renders. Shared here for the same reason the providers
/// above are — there was one private copy per test file, and a third was about to be written.
/// </summary>
public sealed class NullJobPanel : IToolObserver
{
    /// <summary>
    /// The latest state of every job the loop reported, keyed by id — compression renders as one
    /// of these.
    /// </summary>
    /// <remarks>
    /// BY ID, because Job is MUTATED IN PLACE: the loop hands the same instance to ToolsChanged while
    /// running and to ToolUpdated when it finishes, so appending to a list records one object twice
    /// and both entries show the final state. A real panel keys by id for the same reason.
    /// </remarks>
    private readonly Dictionary<string, Job> _jobs = new();

    public IReadOnlyCollection<Job> Jobs => _jobs.Values;

    public void ToolsChanged(IReadOnlyList<Job> jobs) { foreach (var j in jobs) _jobs[j.Id] = j; }

    /// <summary>Counted separately from progress ticks: ToolUpdated is for REAL transitions, and a
    /// repeating tick routed through it would re-expand and blank the row on every call.</summary>
    public void ToolUpdated(Job job) { _jobs[job.Id] = job; StateTransitions++; }

    /// <summary>How many times ToolUpdated was called — one per genuine state change.</summary>
    public int StateTransitions { get; private set; }
    /// <summary>Progress ticks land in the same map, so a test can assert what a running row was
    /// showing — which is the only place that text ever appears.</summary>
    public void ToolProgressed(Job job) { _jobs[job.Id] = job; ProgressTicks++; }

    /// <summary>How many progress ticks arrived — a frozen row is zero.</summary>
    public int ProgressTicks { get; private set; }

    public void ToolResourcesSampled(string jobId, ResourceSnapshot snapshot) { }
    public void ToolOutputAppended(string jobId, string delta) { }
}
