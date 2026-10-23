using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;

namespace CxAgent.Tests;

/// <summary>Answers each request only once released; counts requests and the most ever in flight.</summary>
internal sealed class HeldProvider(params string[] answers) : ILlmProvider
{
    private readonly Queue<string> _answers = new(answers);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight, _maxInFlight, _calls;
    public int Calls => Volatile.Read(ref _calls);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);
    public List<ChatMessage>? LastMessages { get; private set; }

    /// <summary>How many requests have been answered — counted as each one returns, unlike <see cref="Calls"/>.</summary>
    public int Answered => Volatile.Read(ref _answered);
    private int _answered;

    /// <summary>
    /// Runs as each request arrives, before it waits — for a test that has to know what was true at that
    /// moment rather than poll for it afterwards.
    /// </summary>
    public Action? OnCall { get; init; }

    /// <summary>Lets every request so far, and every later one, answer.</summary>
    public void Release() => _gate.TrySetResult();

    public string ProviderId => "held";
    public string ModelId => "held";
    public string DisplayName => "Held";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        LastMessages = [.. messages];
        OnCall?.Invoke();
        try
        {
            // A DEADLINE, so a test that forgets to release fails instead of hanging the suite.
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            string text;
            lock (_answers) text = _answers.Count > 0 ? _answers.Dequeue() : "ok";
            Interlocked.Increment(ref _answered);
            return new LlmResponse { Text = text, StopReason = "end_turn" };
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

/// <summary>
/// Answers one request per <see cref="Step"/>; counts requests and the most ever in flight.
///
/// <para>FOR A ROW WHOSE POINT IS A LIMIT. Released one at a time, every state between steps is stable —
/// the requests in flight stay in flight until the test lets one go — so the test can wait for an exact
/// count instead of sleeping and hoping nothing more arrives. <see cref="HeldProvider"/> releases all at
/// once, which turns a cap's queue into a race.</para>
/// </summary>
internal sealed class SteppedProvider : ILlmProvider
{
    private readonly SemaphoreSlim _steps = new(0);
    private int _inFlight, _maxInFlight, _calls;
    public int Calls => Volatile.Read(ref _calls);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    /// <summary>Lets one waiting request — or the next to arrive — answer.</summary>
    public void Step() => _steps.Release();

    public string ProviderId => "stepped";
    public string ModelId => "stepped";
    public string DisplayName => "Stepped";
    public bool SupportsToolCalling => true;
    public bool SupportsStreaming => false;

    public async Task<LlmResponse> ChatAsync(List<ChatMessage> messages, List<ToolDefinition>? tools, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight))
               && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen) { }
        try
        {
            // A DEADLINE, as HeldProvider's: a test that forgets a step fails instead of hanging the suite.
            if (!await _steps.WaitAsync(TimeSpan.FromSeconds(15), ct))
                throw new TimeoutException("no step came for this request");
            return new LlmResponse { Text = "ok", StopReason = "end_turn" };
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public async IAsyncEnumerable<LlmStreamChunk> ChatStreamAsync(List<ChatMessage> messages, List<ToolDefinition>? tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var r = await ChatAsync(messages, tools, ct);
        yield return new LlmStreamChunk(r.Text, null, true);
    }
}

internal static class Poll
{
    /// <summary>Waits up to five seconds for a condition; answers whether it held.</summary>
    public static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 250; i++) { if (condition()) return true; await Task.Delay(20); }
        return condition();
    }
}

internal static class Calls
{
    public static ToolCall Spawn(string id = "c1", string description = "find thing",
        string prompt = "find the thing", bool? background = null) => new()
    {
        Id = id,
        Name = CxAgent.Core.Jobs.Tool.Agent,
        Arguments = System.Text.Json.JsonSerializer.SerializeToElement(background is { } b
            ? (object)new { description, prompt, background = b }
            : new { description, prompt }),
    };

    public static ToolCall Named(string name, object args, string id = "t1") => new()
    {
        Id = id, Name = name, Arguments = System.Text.Json.JsonSerializer.SerializeToElement(args),
    };

    public static string Text(this MockLlmProvider p) =>
        string.Join("\n", p.LastMessages!.Select(m => m.Content));

    public static string Text(this HeldProvider p) =>
        string.Join("\n", p.LastMessages!.Select(m => m.Content));
}

/// <summary>Records every delivery, and answers as the session would.</summary>
internal sealed class RecordingDelivery : IAgentDelivery
{
    private readonly List<Delivered> _got = [];
    public DeliveryOutcome Answer { get; set; } = DeliveryOutcome.Woke;
    public IReadOnlyList<Delivered> Got { get { lock (_got) return [.. _got]; } }

    public DeliveryOutcome Tell(string agentId, string text) =>
        Tell(agentId, new AgentNotice(Shown: text, ForModel: text));

    public DeliveryOutcome Tell(string agentId, AgentNotice notice)
    {
        lock (_got) _got.Add(new Delivered(agentId, notice, Left: false));
        return Answer;
    }

    public DeliveryOutcome Leave(string agentId, AgentNotice notice)
    {
        lock (_got) _got.Add(new Delivered(agentId, notice, Left: true));
        return Answer == DeliveryOutcome.Woke ? DeliveryOutcome.Queued : Answer;
    }

    /// <summary>Whether a withdrawal finds the text still waiting; false is a message already read.</summary>
    public bool Withdrawable { get; set; }
    private readonly List<(string To, string Text)> _withdrawn = [];
    public IReadOnlyList<(string To, string Text)> Withdrawn { get { lock (_withdrawn) return [.. _withdrawn]; } }

    public bool Withdraw(string agentId, string text)
    {
        lock (_withdrawn) _withdrawn.Add((agentId, text));
        return Withdrawable;
    }

    public async Task<Delivered> Next(int index = 0)
    {
        for (var i = 0; i < 250; i++)
        {
            lock (_got) if (_got.Count > index) return _got[index];
            await Task.Delay(20);
        }
        throw new TimeoutException($"delivery #{index} never came");
    }
}

internal sealed record Delivered(string To, AgentNotice Notice, bool Left);

internal static class StoreClaims
{
    /// <summary>
    /// Claims a kept child and announces its start, as a foreground send does once it has its slot — the
    /// two steps every test that wants a busy, announced child needs together.
    /// </summary>
    public static bool ClaimAndAnnounce(this SubAgentStore store, string name)
    {
        if (!store.TryClaim(name)) return false;
        store.AnnounceBegin(name);
        return true;
    }
}
