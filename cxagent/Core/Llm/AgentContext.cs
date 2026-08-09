using CxAgent.Core.Models;

namespace CxAgent.Core.Llm;

/// <summary>
/// One agent's conversation: the messages it is working from, how full its window is, and the
/// machinery for making room. Owned by the agent, for the agent's whole life.
///
/// <para>WHAT THIS REPLACES. The loop used to build a fresh working list from the session
/// conversation at the start of every goal and throw it away at the end — so the tool calls, file
/// reads and reasoning of goal N were gone before goal N+1 began (measured on a real run: 33 turns
/// of working context discarded). Only the goal text and the final answer survived. "Read X and
/// explain it" followed by "now change it" therefore re-read X, and a session that reached 58,000
/// tokens mid-goal dropped to ~5,000 the moment that goal ended.</para>
///
/// <para>NOBODY ELSE DOES THAT. Claude Code, Codex, opencode, gemini-cli, Cline, Roo and goose all
/// keep ONE growing list across user prompts, tool results included, and treat compaction as a
/// pressure valve tripped by token pressure — never as something that happens at a task boundary.
/// The rebuild also destroys the prompt cache: every one of those agents appends to a stable prefix
/// precisely so cached reads (~10% of input price) keep hitting, and rebuilding the list guarantees
/// a miss on a prefix that was nearly free to keep.</para>
///
/// <para>AND IT IS WHAT MAKES AN AGENT SELF-CONTAINED. An agent whose context is a local variable
/// inside one method cannot own anything: not its own occupancy readout, not a compression it can
/// perform on request, not continuity between the tasks it is given. A sub-agent gets its own
/// instance of this type, and that single fact is what lets it be a real agent rather than a
/// function call — which is exactly what the fan-out design assumes.</para>
/// </summary>
public sealed class AgentContext
{
    private readonly List<ChatMessage> _messages = [];

    /// <summary>
    /// The conversation, oldest first.
    ///
    /// <para>EXPOSED AS THE LIVE LIST, deliberately. The turn loop appends to it constantly — an
    /// assistant turn, then one tool result per call — and the compressor and pruner rewrite it in
    /// place. Handing out copies would mean the agent's context and the list actually being sent
    /// could drift apart, which is the class of bug this type exists to end. <see cref="Snapshot"/>
    /// is there for callers that genuinely need an isolated copy.</para>
    /// </summary>
    public List<ChatMessage> Messages => _messages;

    /// <summary>
    /// The provider's context window in tokens, when known. Null means nobody has told us — a
    /// percentage needs a denominator, and a guessed one is worse than none.
    /// </summary>
    public int? Window { get; }

    /// <summary>
    /// How full the window is, from the last turn the provider reported usage for.
    ///
    /// <para>ONE TURN'S MEASUREMENT, NOT A RUNNING SUM. This is the provider's own count of what it
    /// received, so it rises and — after compaction — falls. The status bar used to divide the
    /// CUMULATIVE ledger total by the window instead, which sums input and output across every turn;
    /// since each turn re-sends the whole conversation that figure grows quadratically and read 107%
    /// of a window that was not close to full, and being cumulative it could never move down to show
    /// that a compression had worked.</para>
    ///
    /// <para>Null until a turn reports usage. A reported 0 is never a measurement — both wires fall
    /// back to 0 when a provider omits usage, and treating that as "plenty of room" would mean a
    /// provider that never reports usage silently never compacts.</para>
    /// </summary>
    public int? Used { get; private set; }

    /// <summary>Used as a fraction of the window, when both are known.</summary>
    public double? UsedFraction => Used is { } u && Window is > 0 ? (double)u / Window.Value : null;

    public AgentContext(int? window = null) => Window = window;

    /// <summary>Appends a message.</summary>
    public void Add(ChatMessage message) => _messages.Add(message);

    /// <summary>Appends several messages.</summary>
    public void AddRange(IEnumerable<ChatMessage> messages) => _messages.AddRange(messages);

    /// <summary>
    /// Inserts at the front — for the system preamble, which must lead the conversation.
    /// </summary>
    public void Prepend(ChatMessage message) => _messages.Insert(0, message);

    /// <summary>Whether any message is present.</summary>
    public bool IsEmpty => _messages.Count == 0;

    /// <summary>Message count.</summary>
    public int Count => _messages.Count;

    /// <summary>
    /// A copy for sending to a provider. The wires want a <see cref="List{T}"/>, and handing out the
    /// live list would let a caller mutate the agent's context as a side effect of making a call.
    /// </summary>
    public List<ChatMessage> Snapshot() => [.. _messages];

    /// <summary>
    /// Records what the provider reported it received. Ignores a non-positive count, which is an
    /// absent measurement rather than an empty context.
    /// </summary>
    public void RecordUsage(int inputTokens)
    {
        if (inputTokens > 0) Used = inputTokens;
    }

    /// <summary>
    /// Marks the occupancy reading as no longer describing the conversation — called after compaction
    /// has rewritten it.
    ///
    /// <para>The true new figure is not knowable until the next call: occupancy is only ever read
    /// from what a provider reports it RECEIVED, and after compaction no call has been made yet.
    /// Estimating one locally would put a guess where every other number here is a measurement.</para>
    /// </summary>
    public void InvalidateUsage() => Used = null;

    /// <summary>Total characters of message content — the size compaction actually changes.</summary>
    public int TotalChars()
    {
        var total = 0;
        foreach (var m in _messages) total += m.Content?.Length ?? 0;
        return total;
    }

    /// <summary>
    /// Replaces the conversation wholesale. For the compressor, which rewrites rather than appends.
    /// </summary>
    public void Replace(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
    }

    /// <summary>
    /// Drops everything — <c>/clear</c>.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
        Used = null;
    }
}
