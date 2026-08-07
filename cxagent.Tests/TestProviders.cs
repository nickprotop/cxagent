using System.Collections.Concurrent;
using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.UI;

namespace CxAgent.Tests;

/// <summary>
/// Shared fake providers used by GoalRunnerTests and AppShellE2ETests.
/// Extracted here so there is exactly ONE copy — no duplication between test files.
/// </summary>

/// <summary>
/// A fake IChatSink that records everything, so a run can be asserted without a real window.
/// Promoted here from GoalRunnerTests (where it was private) when OrchestratorLoopTests needed it —
/// same "one copy only" reason this file exists.
///
/// <para><see cref="Errors"/>/<see cref="Messages"/> are collections, not single values: the consult
/// loop can report several things in one run (a cap hit, an unparseable decision, a failed
/// modification) and a last-one-wins field would hide all but the last. <see cref="Error"/> and
/// <see cref="Result"/> are kept as last-value convenience views over those collections so
/// GoalRunnerTests' existing assertions keep working unchanged.</para>
///
/// <para><see cref="Messages"/> collects EVERY user-visible line — assistant text appended via
/// <see cref="AppendAssistant"/>, errors, and the goal result — because the loop's "say why you
/// stopped" contract is about what reached the user, not which sink method carried it.</para>
/// </summary>
public sealed class RecordingSink : IChatSink
{
    public readonly List<string> Users = new();
    public readonly ConcurrentQueue<string> AssistantTokens = new();

    /// <summary>Every error reported, in order. Concurrent: the loop's sink calls can land on a
    /// thread-pool thread (a JobTransitioned continuation with no SynchronizationContext installed).</summary>
    public readonly ConcurrentQueue<string> ErrorQueue = new();

    /// <summary>Every user-visible line, in order — assistant text, errors, and the goal result.</summary>
    public readonly ConcurrentQueue<string> MessageQueue = new();

    public List<string> Errors => ErrorQueue.ToList();
    public List<string> Messages => MessageQueue.ToList();

    /// <summary>The most recent goal result, or null if none was shown.</summary>
    public GoalState? Result { get; private set; }

    /// <summary>The most recent error, or null if none was reported (a last-value view over
    /// <see cref="Errors"/>).</summary>
    public string? Error => ErrorQueue.LastOrDefault();

    private long _id;

    public ChatMessageId AddUserTurn(string text) { Users.Add(text); return new ChatMessageId(_id++); }
    public ChatMessageId BeginAssistantTurn() => new ChatMessageId(_id++);

    /// <summary>Turns closed via EndAssistantTurn — a turn left open spins its thinking indicator forever.</summary>
    public List<long> EndedTurns { get; } = new();

    public void EndAssistantTurn(ChatMessageId id) => EndedTurns.Add(id.Value);

    public void AppendAssistant(ChatMessageId id, string token)
    {
        AssistantTokens.Enqueue(token);
        MessageQueue.Enqueue(token);
    }

    public void ShowGoalResult(GoalState state, int failedCount)
    {
        Result = state;
        MessageQueue.Enqueue($"goal {state}, {failedCount} failed");
    }

    public void ShowError(string message)
    {
        ErrorQueue.Enqueue(message);
        MessageQueue.Enqueue(message);
    }

    /// <summary>How many times copilot mode asked the UI to surface the approval affordance.</summary>
    public int ApprovalRequests { get; private set; }

    /// <summary>The detail text of each approval request — P9b's mid-goal gate NAMES the jobs it
    /// is asking about, and a test that only counted requests could not tell the two gates apart.</summary>
    public List<string?> ApprovalDetails { get; } = new();

    public void ShowApprovalRequest(string? detail = null)
    {
        ApprovalRequests++;
        ApprovalDetails.Add(detail);
    }

    public void SetAssistantHeader(ChatMessageId id, string header) { }
    public void ShowSystemMessage(string message) => MessageQueue.Enqueue(message);
}

/// <summary>
/// Streams a two-step create_plan tool call followed by the done-marker,
/// simulating the minimal happy-path response from a real LLM.
/// </summary>
public sealed class FakePlanProvider : ILlmProvider
{
    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public ILlmProvider WithModel(string model) => this;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    /// <summary>
    /// The consult (P8 Task 5): once GoalRunner drives through OrchestratorLoop, EVERY plan fixture is
    /// asked what to do after its jobs finish, so ChatAsync can no longer throw. <c>finish_goal</c> is
    /// the neutral answer — it ends the loop without adding, cancelling, or re-parameterising anything,
    /// so each fixture's existing assertions still describe the plan it compiled.
    /// </summary>
    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => Task.FromResult(LlmResponse.WithToolCall("consult",
            new { action = "finish_goal", summary = "done", rationale = "plan ran as written" }));

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmStreamChunk("Plan: ", null, false);
        yield return new LlmStreamChunk("two steps.", null, false);
        var args = JsonDocument.Parse("""
        {"summary":"two","jobs":[
          {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}},
          {"id":"s2","name":"Step 2","type":"wait","params":{"seconds":0.0},"depends_on":["s1"]}]}
        """).RootElement.Clone();
        yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
        yield return new LlmStreamChunk(null, null, true);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Returns a plan the compiler REJECTS on the first create_plan turn, then a valid one on the
/// second. Exists to prove GoalRunner's single plan-repair attempt actually happens: without it
/// a rejected plan is a dead goal, which is strictly worse than the placeholder-on-disk bug the
/// rejecting guard was added to prevent.
/// </summary>
public sealed class RejectThenRepairProvider : ILlmProvider
{
    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public ILlmProvider WithModel(string model) => this;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    /// <summary>How many create_plan turns were streamed — 2 proves the repair turn happened.</summary>
    public int PlanTurns { get; private set; }

    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => Task.FromResult(LlmResponse.WithToolCall("consult",
            new { action = "finish_goal", summary = "done", rationale = "plan ran as written" }));

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        PlanTurns++;

        // Turn 1: `s2` is a write depending on TWO jobs with no content — ambiguous, rejected at
        // compile time. (Was a D11 reference violation until that syntax was removed; the point of
        // this fixture is a plan the COMPILER refuses, whatever the current rules are.)
        // Turn 2: the same work, unambiguous.
        var json = PlanTurns == 1
            ? """
              {"summary":"bad","jobs":[
                {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}},
                {"id":"s1b","name":"Step 1b","type":"wait","params":{"seconds":0.0}},
                {"id":"s2","name":"Step 2","type":"file","depends_on":["s1","s1b"],
                 "params":{"action":"write","path":"/tmp/cx-repair-test.md"}}]}
              """
            : """
              {"summary":"good","jobs":[
                {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}},
                {"id":"s2","name":"Step 2","type":"wait","params":{"seconds":0.0},"depends_on":["s1"]}]}
              """;

        var args = JsonDocument.Parse(json).RootElement.Clone();
        yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
        yield return new LlmStreamChunk(null, null, true);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Plans one job, then on the first consult asks to ADD another — so P9b's mid-goal gate actually
/// fires. Subsequent consults finish the goal, so the loop terminates either way.
/// </summary>
public sealed class AddsAJobMidGoalProvider : ILlmProvider
{
    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public ILlmProvider WithModel(string model) => this;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    private int _consults;

    /// <summary>How many times the orchestrator was consulted — proves the loop actually ran.</summary>
    public int Consults => _consults;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
    {
        // First consult: add a job. After that: finish, so the goal cannot loop forever whether the
        // addition was approved or declined.
        if (Interlocked.Increment(ref _consults) == 1)
            return Task.FromResult(LlmResponse.WithToolCall("consult", new
            {
                action = "modify",
                rationale = "one more step",
                jobs_to_add = new[]
                {
                    new { id = "extra", name = "Extra Step", type = "wait", @params = new { seconds = 0.0 } },
                },
            }));

        return Task.FromResult(LlmResponse.WithToolCall("consult",
            new { action = "finish_goal", summary = "done", rationale = "nothing left" }));
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var args = JsonDocument.Parse("""
        {"summary":"one","jobs":[
          {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}}]}
        """).RootElement.Clone();
        yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args }, false);
        yield return new LlmStreamChunk(null, null, true);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Reports a FIXED <c>InputTokens</c> on every response — streamed and non-streamed alike — so Task
/// 3's auto-compression trigger can be exercised deterministically. This is real provider data in
/// production (the wire's own <c>prompt_tokens</c>/<c>input_tokens</c> field); this fake just pins it
/// to a caller-chosen value instead of whatever a live model would report. Otherwise behaves like
/// <see cref="FakePlanProvider"/> — one job, one consult that finishes the goal — so a run through it
/// completes normally and the compression check runs against a real (if trivial) goal.
/// </summary>
public sealed class HighInputTokenProvider : ILlmProvider
{
    private readonly int _inputTokens;
    public HighInputTokenProvider(int inputTokens) => _inputTokens = inputTokens;

    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public ILlmProvider WithModel(string model) => this;
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => true;

    public Task<LlmResponse> ChatAsync(List<ChatMessage> m, List<ToolDefinition>? t, CancellationToken ct)
        => Task.FromResult(LlmResponse.WithToolCall("consult",
            new { action = "finish_goal", summary = "done", rationale = "plan ran as written" })
            with { Usage = new LlmUsage { InputTokens = _inputTokens, OutputTokens = 1 } });

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(
        List<ChatMessage> m, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var args = JsonDocument.Parse("""
        {"summary":"one","jobs":[
          {"id":"s1","name":"Step 1","type":"wait","params":{"seconds":0.0}}]}
        """).RootElement.Clone();
        yield return new LlmStreamChunk(null, new ToolCall { Name = "create_plan", Id = "c1", Arguments = args },
            false, new LlmUsage { InputTokens = _inputTokens, OutputTokens = 1 });
        yield return new LlmStreamChunk(null, null, true);
        await Task.CompletedTask;
    }
}

/// <summary>
/// A provider whose ChatAsync always throws. Promoted here (from GoalRunnerTests, where it was
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
    public ILlmProvider WithModel(string model) => this;
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
    public ILlmProvider WithModel(string model) => this;
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
    public ILlmProvider WithModel(string model) => this;
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
/// has a real measurement to act on. Without usage, GoalRunner's `_lastInputTokens` stays null and
/// compression declines — correct behaviour, but it makes a compression test pass for the wrong reason.
/// </summary>
public sealed class AnswersWithUsageProvider : ILlmProvider
{
    private readonly int _inputTokens;
    public AnswersWithUsageProvider(int inputTokens) => _inputTokens = inputTokens;

    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public string ModelId => "test-model";
    public ILlmProvider WithModel(string model) => this;
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
