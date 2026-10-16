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
        try
        {
            // A DEADLINE, so a test that forgets to release fails instead of hanging the suite.
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            string text;
            lock (_answers) text = _answers.Count > 0 ? _answers.Dequeue() : "ok";
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
