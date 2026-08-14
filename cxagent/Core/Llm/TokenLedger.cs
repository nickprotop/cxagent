namespace CxAgent.Core.Llm;

/// <summary>
/// Cumulative orchestrator token spend for ONE goal, against an optional budget.
///
/// The three failure counters (RetryCount/MaxRetries, the 2-round correction cap, ungated manual
/// diagnosis) already prevent infinite loops. This is the orthogonal COST guard for the case the spec
/// calls out: many jobs each retrying and diagnosing within their own limits, where the aggregate is
/// what the user cares about. A null budget means unbounded — the default, so cost control is opt-in
/// and nobody's first run dies on a budget they never set.
///
/// <para>THREAD-SAFE, because it is already multi-writer and about to become more so. Every write
/// goes through <see cref="Interlocked"/> and every read through <see cref="Volatile"/>: the counters
/// were plain <c>+=</c>, which is a read-modify-write, so two agents recording at once silently lost
/// one of their spends — and the failure is invisible, showing up only as a total that is quietly too
/// low. Sub-agents make that likely rather than theoretical.</para>
/// </summary>
public sealed class TokenLedger
{
    private int _total;
    private int _input;
    private int _output;

    public TokenLedger() { }

    /// <summary>
    /// A ledger carrying spend that already happened — for a session restored from disk.
    ///
    /// <para>A CONSTRUCTOR RATHER THAN A REPLAYED <see cref="Record"/> CALL: the restored totals are
    /// history, not usage this process observed, and <see cref="Record"/> also drives the per-model
    /// and sub-agent splits — replaying one synthetic <see cref="LlmUsage"/> would attribute a whole
    /// session's spend to whichever model happened to be current.</para>
    /// </summary>
    public TokenLedger(int inputTokens, int outputTokens)
    {
        _input = inputTokens;
        _output = outputTokens;
        _total = inputTokens + outputTokens;
    }

    public int TotalTokens => Volatile.Read(ref _total);

    /// <summary>
    /// Tokens SENT so far — the conversation, re-sent in full on every call.
    ///
    /// <para>Split from output because the two behave nothing alike. Input grows with the
    /// conversation and dominates a long session (every turn re-sends everything before it), while
    /// output is what the model actually produced. A single total hides which one is growing, and
    /// they have different remedies: compress the history, or ask for less.</para>
    /// </summary>
    public int InputTokens => Volatile.Read(ref _input);

    /// <summary>Tokens GENERATED so far — the model's own production.</summary>
    public int OutputTokens => Volatile.Read(ref _output);

    /// <summary>
    /// Spend so far, per model id — for the models that actually spent something.
    ///
    /// <para>ONE LEDGER, TALLIED BY MODEL, rather than a ledger per model. Spend is a property of
    /// the SESSION — the figure a user wants is what this conversation cost, not what each model
    /// cost in isolation — so the totals are authoritative and this map is attribution laid over
    /// them. A caller that does not name a model still records into the totals; it simply does not
    /// appear here.</para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ByModel
    {
        get { lock (_byModel) return new Dictionary<string, int>(_byModel, StringComparer.Ordinal); }
    }

    /// <summary>What each model SENT and GENERATED, split — the same ↑/↓ the totals carry.</summary>
    /// <remarks>
    /// PER MODEL, because the panel is the aggregator and a session-wide ↑/↓ beside a per-model
    /// breakdown answers two different questions in one block. The split is the thing worth knowing
    /// per model, too: a planner that reads the whole repo and returns a page is almost all input,
    /// a model that writes code is not, and one summed figure hides which is which — exactly the
    /// distinction the totals were split to expose.
    ///
    /// <para>Taken under the SAME lock as <see cref="ByModel"/> and returned as one snapshot, so a
    /// reader can never see a model's total and its split disagree.</para>
    /// </remarks>
    public IReadOnlyDictionary<string, (int Input, int Output)> SplitByModel
    {
        get
        {
            lock (_byModel)
                return new Dictionary<string, (int, int)>(_splitByModel, StringComparer.Ordinal);
        }
    }

    // A LOCK, NOT A CONCURRENT DICTIONARY. Two agents on one ledger is already the normal case, and
    // the read is a snapshot the UI takes a few times a second — a dictionary that is internally
    // thread-safe would still let a reader see a torn view across several keys. Both maps share the
    // one lock: they are two views of the same fact and must never be observed out of step.
    private readonly Dictionary<string, int> _byModel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Input, int Output)> _splitByModel = new(StringComparer.Ordinal);

    /// <summary>
    /// What sub-agents have spent, of <see cref="TotalTokens"/>.
    ///
    /// <para>SEPARATE FROM <see cref="ByModel"/> because the common fan-out session runs its children
    /// on the PARENT'S model — one provider, one model id — and a per-model breakdown then has a
    /// single row and shows nothing. "A worker spent this" is the question a fan-out session actually
    /// asks, and model identity does not answer it.</para>
    /// </summary>
    public int SubAgentTokens => Volatile.Read(ref _subAgent);

    private int _subAgent;

    /// <param name="modelId">
    /// Which model spent it, or null when the caller does not know. Null records into the totals and
    /// nothing else — better than inventing a bucket named "unknown", which would look like a model.
    /// </param>
    /// <param name="subAgent">True when a CHILD spent this, so the panel can attribute it.</param>
    public void Record(LlmUsage usage, string? modelId = null, bool subAgent = false)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
            lock (_byModel)
            {
                _byModel[modelId] = _byModel.GetValueOrDefault(modelId)
                                  + usage.InputTokens + usage.OutputTokens;

                var (input, output) = _splitByModel.GetValueOrDefault(modelId);
                _splitByModel[modelId] = (input + usage.InputTokens, output + usage.OutputTokens);
            }

        if (subAgent)
            Interlocked.Add(ref _subAgent, usage.InputTokens + usage.OutputTokens);

        Interlocked.Add(ref _input, usage.InputTokens);
        Interlocked.Add(ref _output, usage.OutputTokens);

        Interlocked.Add(ref _total, usage.InputTokens + usage.OutputTokens);
    }
}
