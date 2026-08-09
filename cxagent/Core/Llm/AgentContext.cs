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
    /// How many leading messages are STRUCTURAL rather than conversational — the working-directory
    /// preamble, today. Compression starts below this.
    ///
    /// <para>THE HEAD IS NOT HISTORY. Compression removed from index 0, which is where the preamble
    /// sits, so the first compaction of any session deleted the instructions that keep the model on
    /// real paths — measured before that preamble existed, ten of twenty shell calls hunted for
    /// directories that do not exist on this machine. The agent re-inserts it when no system message
    /// is present, so it then reappeared ABOVE the summary on the next turn, and again after every
    /// later compaction: the head never settled.</para>
    ///
    /// <para>DERIVED, not stored. A stored count goes stale the moment anything else edits the list,
    /// and this is consulted precisely while the list is being rewritten.</para>
    /// </summary>
    public int PinnedHeadCount => _messages.Count > 0 && _messages[0].Role == "system" ? 1 : 0;

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
    /// <param name="atChars">
    /// The size of the conversation THE PROVIDER SAW — captured before the call, not after.
    ///
    /// <para>Measuring it at record time is wrong and was wrong in practice: by then the assistant's
    /// reply and every tool result of that turn have been appended, so the baseline includes tens of
    /// thousands of characters the reading never covered. Measured live, that inflated baseline made
    /// a −32% compaction move the estimate by 1%.</para>
    /// </param>
    public void RecordUsage(int inputTokens, int atChars = 0)
    {
        if (inputTokens <= 0) return;

        var chars = atChars > 0 ? atChars : TotalChars();
        if (!IsPossible(inputTokens, chars))
        {
            // KEEP THE PREVIOUS READING, exactly as for a reported 0. Dropping to null would read as
            // "occupancy unknown" and silence the trigger; overwriting with the garbage would fire it
            // on a context that is nowhere near full.
            RejectedReadings++;
            LastRejectedReading = inputTokens;
            return;
        }

        Used = inputTokens;
        UsedAtChars = chars;
        IsEstimated = false;   // a real measurement supersedes any estimate
    }

    /// <summary>
    /// Whether a reported input-token count could describe <paramref name="chars"/> characters at all.
    ///
    /// <para>THE PROVIDER IS NOT ALWAYS TELLING THE TRUTH, and this is measured rather than defensive.
    /// Two of twenty-five readings logged on this machine were impossible: 31,060 characters reported
    /// as 132,495 input tokens, and 57,088 as 249,125 — roughly 17x more tokens than characters, in
    /// independent sessions, which reads as a systematic endpoint defect rather than noise. A local
    /// llama.cpp splitting <c>n_ctx</c> across slots is one way this happens; whatever the cause, the
    /// figure cannot be acted on.</para>
    ///
    /// <para>Two rules, and they catch different shapes. A token is at MINIMUM one character in any
    /// tokenizer, so more tokens than characters is arithmetically impossible whatever the language.
    /// And a reading larger than the window the provider is serving cannot describe what it received —
    /// which catches a large context whose density looks plausible but whose total does not.</para>
    ///
    /// <para>DELIBERATELY NOT A DENSITY BAND. Rejecting anything under, say, 2 chars/token would also
    /// reject real readings: code, JSON and CJK all tokenize far denser than English prose, and those
    /// are precisely the sessions that fill a window fastest and most need the trigger. The gate is
    /// for the impossible, not the unusual.</para>
    /// </summary>
    private bool IsPossible(int inputTokens, int chars)
    {
        // Nothing to compare against — an empty conversation cannot contradict any reading.
        if (chars <= 0) return true;

        if (inputTokens > chars) return false;
        if (Window is > 0 && inputTokens > Window.Value) return false;

        return true;
    }

    /// <summary>
    /// How many reported readings were rejected as impossible, and the last such value.
    ///
    /// <para>Surfaced rather than silently dropped: a provider misreporting usage is a real fault
    /// worth seeing, and a count that climbs is the only evidence a session has that its occupancy
    /// figure is being held back rather than simply not moving.</para>
    /// </summary>
    public int RejectedReadings { get; private set; }

    /// <summary>The most recent rejected reading, or null if none was. Diagnostic only.</summary>
    public int? LastRejectedReading { get; private set; }

    /// <summary>
    /// How large the conversation was when <see cref="Used"/> was measured.
    ///
    /// <para>Needed to scale that reading honestly after a compaction. The obvious ratio —
    /// chars-before-compaction to chars-after — is WRONG whenever the measurement predates the
    /// growth: a reading taken at 66,394 chars, scaled by a compaction from 98,630 to 66,700, moved
    /// the estimate by 1% while the context had actually shrunk by a third. The reading has to be
    /// scaled against the size IT was taken at, not against whatever the size happened to be when
    /// compaction started.</para>
    /// </summary>
    public int UsedAtChars { get; private set; }

    /// <summary>
    /// What the NEXT call will carry, in tokens — the last measurement rescaled to the conversation
    /// as it stands now.
    ///
    /// <para>THE MEASUREMENT IS ALWAYS ONE TURN BEHIND, and that is not a defect to fix but a fact to
    /// work with: occupancy is only ever reported for a call that has already been made, while a
    /// turn's tool results — the bulk of what fills a window — land after it. Checking pressure
    /// against the raw reading therefore tests the size of the conversation BEFORE this turn's file
    /// reads. Measured live: a context sent at 98,630 characters was checked against the 19,518-token
    /// reading taken at 66,394, passed a 25,000 threshold, and never compacted — while the very next
    /// measurement came back at 28,205, well over it.</para>
    ///
    /// <para>Rescaling by the measured token density closes that gap. It is an estimate and named as
    /// one; the alternative is a threshold that cannot see the growth it exists to catch.</para>
    /// </summary>
    public int? ProjectedUsed
    {
        get
        {
            if (Used is not { } used || UsedAtChars <= 0) return Used;
            return Math.Max(1, (int)(TotalChars() * ((double)used / UsedAtChars)));
        }
    }

    /// <summary>
    /// Characters per token, for the fallback path only — see <see cref="IsUnderPressure"/>.
    ///
    /// <para>THREE, not four. English prose runs about four, but an agent's context is dominated by
    /// what tool calls return — source code, JSON, file listings, stack traces — all of which
    /// tokenize denser than prose. Three keeps the converted threshold CONSERVATIVE: it fires a
    /// little early rather than a little late, and late is the failure that matters (a context that
    /// will not fit is a session that stops working, while an early compaction costs one call).</para>
    /// </summary>
    public const int CharsPerToken = 3;

    /// <summary>
    /// The share of the window left free, so a compaction happens BEFORE the next call would be
    /// refused rather than after.
    ///
    /// <para>opencode reserves an absolute buffer instead — <c>min(20_000, maxOutputTokens)</c> —
    /// which is the more precise rule when the model's output cap is known. A fraction is used here
    /// because that cap is not modelled: 15% of a 212k window is ~32k, of a 32k window is ~4.8k, and
    /// both leave room for a reply where one fixed number cannot serve both.</para>
    /// </summary>
    private const double ReserveFraction = 0.15;

    /// <summary>
    /// Whether the next call is close enough to the window that the conversation should be compacted
    /// first.
    ///
    /// <para>THE REAL LIMIT, NOT A HAND-PICKED NUMBER. This is opencode's rule
    /// (<c>session/overflow.ts</c>: <c>count >= model.limit.input - reserved</c>): the window is
    /// known — it is configured per provider instance and shown in the panel — so the threshold is
    /// derived from it rather than from a separate setting that can disagree with it.</para>
    ///
    /// <para>TOKENS WHEN WE HAVE THEM, CHARACTERS WHEN WE DO NOT. Reported usage is the honest
    /// measure of what the provider received, so it is preferred. But a local llama.cpp build often
    /// reports none at all — measured on this machine, whole turns with the field absent — and a
    /// token-only rule then never fires while the session grows until the endpoint refuses it.
    /// Characters are always countable, so they cover exactly that gap. Impossible readings never
    /// reach here: <see cref="RecordUsage"/> rejects them, so <see cref="Used"/> holds either a
    /// plausible measurement or the last one that was.</para>
    /// </summary>
    public bool IsUnderPressure
    {
        get
        {
            // No window, no ceiling to be under pressure against.
            if (Window is not { } window || window <= 0) return false;

            var budget = window * (1.0 - ReserveFraction);

            // ProjectedUsed, not Used: the measurement is always one turn behind the growth, so it is
            // rescaled to the conversation as it stands now — see its own doc.
            if (ProjectedUsed is { } tokens) return tokens >= budget;

            // Nothing was ever reported. Fall back to what we can count ourselves.
            return TotalChars() >= budget * CharsPerToken;
        }
    }

    /// <summary>
    /// Re-estimates occupancy after the conversation has been rewritten, and marks it approximate.
    ///
    /// <para>The exact figure is not knowable until the next call — occupancy is only ever read from
    /// what a provider reports it RECEIVED — but the exact SIZE is known, and tokens track characters
    /// closely enough that scaling the last reading beats keeping it. Keeping it was the reported
    /// bug: compress, and the gauge does not move.</para>
    ///
    /// <para>Scaled against <see cref="UsedAtChars"/> — the size the reading was TAKEN at — not
    /// against the size when compaction started. Those differ whenever turns have been added since
    /// the last measurement, which is the normal case: measured live, using the wrong baseline moved
    /// the estimate 1% on a compaction that removed a third of the conversation.</para>
    /// </summary>
    public void EstimateUsageAfterCompaction()
    {
        if (Used is not { } used || UsedAtChars <= 0) { Used = null; return; }

        // TOKENS PER CHARACTER, not a before/after ratio on the conversation.
        //
        // The ratio approach kept failing because the two sizes it compares are taken at different
        // moments: the reading is measured when a turn is SENT, but the tool results of that turn are
        // appended afterwards, so by the time compaction runs the conversation has grown by tens of
        // thousands of characters the reading never covered. Measured live, that made a −32%
        // compaction move the estimate by 1%, twice, on two different attempts to fix it.
        //
        // A density is stable across both moments. The provider counted `used` tokens for
        // `UsedAtChars` characters; applying that rate to whatever survives compaction gives an
        // estimate that does not care when either figure was taken.
        var density = (double)used / UsedAtChars;
        var now = TotalChars();

        IsEstimated = true;
        Used = Math.Max(1, (int)(now * density));
        UsedAtChars = now;
    }

    /// <summary>
    /// True when <see cref="Used"/> is arithmetic rather than a measurement — set by compaction,
    /// cleared by the next real reading. The readout marks it so a scaled figure never poses as
    /// measured.
    /// </summary>
    public bool IsEstimated { get; private set; }

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
