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
    private int _cached;
    private int _cacheReports;

    // THE SAME FOUR NUMBERS, SPLIT BY WHO SPENT THEM. See CacheHitRateByAgent for why the session
    // total hides the question a fan-out user actually has.
    private int _workerInput;
    private int _workerCached;
    private int _workerReports;

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
    /// How many of the input tokens the provider served from its prefix cache.
    ///
    /// <para>THE NUMBER THAT SAYS WHETHER A LONG SESSION IS EXPENSIVE. Input dominates a tool loop
    /// and grows every turn, but a re-sent prefix that hits the cache is nearly free — so a session
    /// with 6M input tokens and a 95% hit rate costs a fraction of one with 6M and no cache. Without
    /// this the two are indistinguishable in every readout the app has.</para>
    /// </summary>
    public int CachedInputTokens => Volatile.Read(ref _cached);

    /// <summary>
    /// The share of input served from cache, or null when no provider ever reported the figure.
    ///
    /// <para>NULL RATHER THAN ZERO, because "this provider does not tell us" and "every byte missed"
    /// are opposite facts and a 0% shown for the first is a lie the user would act on.</para>
    /// </summary>
    /// <summary>
    /// The prefix-cache hit rate for THIS agent and for its workers, separately — or null for either
    /// when nothing reported.
    ///
    /// <para>WHY THE SESSION TOTAL IS NOT ENOUGH. A parent and its children spend against the same
    /// endpoint but hold different conversations — measured on one fan-out, four children ended at
    /// 85k, 43k, 31k and 58k tokens sharing only a ~5k prompt head. Whether that costs anything
    /// depends entirely on the server, and the two cases look identical in a combined figure.</para>
    ///
    /// <para>IT DEPENDS ON THE SERVER, AND THE DEFAULT IS KIND. llama.cpp's slots cannot each hold
    /// more than one conversation, but <c>--cache-ram</c> (8 GiB by default) parks an idle slot's KV
    /// state in host memory and restores it when that conversation returns — 28ms against 5,850ms
    /// to recompute 20k tokens, measured. So divergent children do NOT thrash a default server.
    /// Started with <c>-cram 0</c>, or on a machine where that budget is lowered, the same fan-out
    /// becomes expensive. Same app, different economics — which is why this is measured per agent
    /// rather than assumed.</para>
    ///
    /// <para>Averaged together, a parent hitting 95% hides workers hitting 20%: the session looks
    /// healthy while every spawn quietly pays full price. Split, the numbers answer a question a
    /// user can act on — whether more slots would pay for themselves, which is a config change
    /// rather than a code change.</para>
    /// </summary>
    public (double? Own, double? Workers) CacheHitRateByAgent
    {
        get
        {
            var workerInput = Volatile.Read(ref _workerInput);
            var workerCached = Volatile.Read(ref _workerCached);
            var workers = Volatile.Read(ref _workerReports) == 0 ? (double?)null
                : workerInput == 0 ? 0
                : (double)workerCached / workerInput;

            // OWN IS THE REMAINDER, not a separate tally: every recorded call is either this agent's
            // or a worker's, so subtracting is exact and cannot drift from the totals above.
            var ownInput = InputTokens - workerInput;
            var ownCached = CachedInputTokens - workerCached;
            var own = Volatile.Read(ref _cacheReports) - Volatile.Read(ref _workerReports) == 0
                ? (double?)null
                : ownInput <= 0 ? 0
                : (double)ownCached / ownInput;

            return (own, workers);
        }
    }

    public double? CacheHitRate
    {
        get
        {
            if (Volatile.Read(ref _cacheReports) == 0) return null;
            var input = InputTokens;
            return input == 0 ? 0 : (double)CachedInputTokens / input;
        }
    }

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
        {
            Interlocked.Add(ref _subAgent, usage.InputTokens + usage.OutputTokens);
            Interlocked.Add(ref _workerInput, usage.InputTokens);
            if (usage.CacheReported)
            {
                Interlocked.Add(ref _workerCached, usage.CachedInputTokens);
                Interlocked.Increment(ref _workerReports);
            }
        }

        Interlocked.Add(ref _input, usage.InputTokens);

        // COUNTED ONLY WHEN REPORTED, so a provider that stays silent leaves CacheHitRate null
        // rather than dragging a real hit rate down toward zero with every unreported turn.
        if (usage.CacheReported)
        {
            Interlocked.Add(ref _cached, usage.CachedInputTokens);
            Interlocked.Increment(ref _cacheReports);
        }
        Interlocked.Add(ref _output, usage.OutputTokens);

        Interlocked.Add(ref _total, usage.InputTokens + usage.OutputTokens);
    }
}
