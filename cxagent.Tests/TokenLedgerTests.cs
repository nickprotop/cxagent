using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class TokenLedgerTests
{
    [Fact]
    public void Record_AccumulatesInputAndOutput()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 50 });
        l.Record(new LlmUsage { InputTokens = 10, OutputTokens = 5 });
        Assert.Equal(165, l.TotalTokens);
    }



    // ---- prefix cache ---------------------------------------------------------------------------

    /// <summary>
    /// THE NUMBER THAT SAYS WHETHER A LONG SESSION IS EXPENSIVE. Input dominates a tool loop and
    /// grows every turn, but a re-sent prefix that hits the cache is nearly free — so 6M input with
    /// a 95% hit rate and 6M with none are wildly different sessions that looked identical here.
    /// </summary>
    [Fact]
    public void Record_AccumulatesCachedInput_AndReportsTheRate()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 1000, OutputTokens = 10, CachedInputTokens = 900, CacheReported = true });
        l.Record(new LlmUsage { InputTokens = 1000, OutputTokens = 10, CachedInputTokens = 500, CacheReported = true });

        Assert.Equal(1400, l.CachedInputTokens);
        Assert.Equal(0.70, l.CacheHitRate!.Value, 3);
    }

    /// <summary>
    /// NULL, NOT ZERO, when the provider never reports. "This endpoint does not tell us" and "every
    /// byte missed the cache" are opposite facts, and showing 0% for the first sends a user hunting
    /// for a cache problem they do not have.
    /// </summary>
    [Fact]
    public void CacheHitRate_IsNullWhenNoProviderReported()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 5000, OutputTokens = 20 });

        Assert.Null(l.CacheHitRate);
        Assert.Equal(0, l.CachedInputTokens);
    }

    /// <summary>A provider that reports a genuine miss is NOT the same as one that stays silent —
    /// this one must show 0%, where the test above must show nothing at all.</summary>
    [Fact]
    public void CacheHitRate_IsZeroWhenReportedAsAMiss()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 5000, OutputTokens = 20, CachedInputTokens = 0, CacheReported = true });

        Assert.Equal(0.0, l.CacheHitRate!.Value, 3);
    }

    // ---- cache, split by who spent it ---------------------------------------------------------

    /// <summary>
    /// THE QUESTION THE SESSION TOTAL CANNOT ANSWER. A local endpoint usually runs one slot, so a
    /// parent and its children take turns through a single KV cache. Averaged together, a parent
    /// hitting 95% hides workers hitting 20% — the session looks healthy while every spawn pays
    /// full price.
    /// </summary>
    [Fact]
    public void CacheHitRate_IsReportedSeparatelyForWorkersAndThisAgent()
    {
        var l = new TokenLedger();

        // This agent: 1,000 in, 900 cached — a warm conversation.
        l.Record(new LlmUsage { InputTokens = 1000, OutputTokens = 10, CachedInputTokens = 900, CacheReported = true });

        // Workers: 1,000 in, 200 cached — the cold-start cost of diverging contexts.
        l.Record(new LlmUsage { InputTokens = 1000, OutputTokens = 10, CachedInputTokens = 200, CacheReported = true },
            subAgent: true);

        var (own, workers) = l.CacheHitRateByAgent;

        Assert.Equal(0.90, own!.Value, 3);
        Assert.Equal(0.20, workers!.Value, 3);

        // And the session-wide figure is the average that hides both.
        Assert.Equal(0.55, l.CacheHitRate!.Value, 3);
    }

    /// <summary>
    /// OWN IS THE REMAINDER, not a separate tally — so it cannot drift from the totals. With no
    /// workers at all, this agent's rate IS the session rate.
    /// </summary>
    [Fact]
    public void WithNoWorkers_ThisAgentsRateMatchesTheSessionRate()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 500, OutputTokens = 5, CachedInputTokens = 400, CacheReported = true });

        var (own, workers) = l.CacheHitRateByAgent;

        Assert.Equal(l.CacheHitRate!.Value, own!.Value, 3);
        Assert.Null(workers);
    }

    /// <summary>NULL, NOT ZERO, per side. A provider that never reports must not make either side
    /// look like a total miss — the distinction the whole feature rests on.</summary>
    [Fact]
    public void WithNothingReported_BothSidesAreNull()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 500, OutputTokens = 5 });
        l.Record(new LlmUsage { InputTokens = 500, OutputTokens = 5 }, subAgent: true);

        var (own, workers) = l.CacheHitRateByAgent;

        Assert.Null(own);
        Assert.Null(workers);
    }

    /// <summary>
    /// THE LEDGER CARRIES WHAT THE WARMING COST. A session can show a healthy hit rate while the
    /// writes that produced it cost more than the reads saved — true on OpenAI and Anthropic, never
    /// true on a local endpoint, and indistinguishable without this figure.
    /// </summary>
    [Fact]
    public void Record_AccumulatesCacheWrites()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 2000, OutputTokens = 5, CacheWriteTokens = 2000, CacheReported = true });
        l.Record(new LlmUsage { InputTokens = 2000, OutputTokens = 5, CachedInputTokens = 1900, CacheReported = true });

        Assert.Equal(2000, l.CacheWrittenTokens);
        Assert.Equal(1900, l.CachedInputTokens);
    }

    /// <summary>Zero where warming is free — every local endpoint — so the readout stays a single
    /// clean figure rather than claiming a cost that was never incurred.</summary>
    [Fact]
    public void WithNoWritesReported_TheWriteTallyStaysZero()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 1000, OutputTokens = 5, CachedInputTokens = 900, CacheReported = true });

        Assert.Equal(0, l.CacheWrittenTokens);
    }

    // ---- concurrency ---------------------------------------------------------------------------

    /// <summary>
    /// EVERY SPEND IS COUNTED WHEN AGENTS RECORD CONCURRENTLY.
    ///
    /// <para>The counters were plain <c>+=</c> — a read-modify-write — so two writers could read the
    /// same value, add to it, and store, losing one spend entirely. The failure is invisible: nothing
    /// throws, the total is just quietly too low, and a budget stops binding without anyone noticing.
    /// It was already multi-writer before sub-agents; they make it likely rather than theoretical.</para>
    ///
    /// <para>Eight threads × 500 records is enough to lose updates reliably on the old code, and the
    /// arithmetic is exact — an off-by-a-few assertion would pass on a race that dropped a handful.</para>
    /// </summary>
    [Fact]
    public void Record_FromManyThreads_LosesNothing()
    {
        const int threads = 8, perThread = 500;
        var ledger = new TokenLedger();

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                ledger.Record(new LlmUsage { InputTokens = 3, OutputTokens = 2 });
        });

        Assert.Equal(threads * perThread * 3, ledger.InputTokens);
        Assert.Equal(threads * perThread * 2, ledger.OutputTokens);
        Assert.Equal(threads * perThread * 5, ledger.TotalTokens);
    }



    // ---- per-model attribution ------------------------------------------------------------------

    /// <summary>
    /// ONE LEDGER, TALLIED BY MODEL — not a ledger per model. Spend is a property of the SESSION:
    /// the figure a user wants is what this conversation cost, not what each model cost in
    /// isolation. Splitting would make the total the status bar shows a sum of things nobody asked
    /// to have summed.
    /// </summary>
    [Fact]
    public void Record_TalliesByModel_WhileTotalsStaySessionWide()
    {
        var ledger = new TokenLedger();

        ledger.Record(new LlmUsage { InputTokens = 100, OutputTokens = 20 }, "big-model");
        ledger.Record(new LlmUsage { InputTokens = 30, OutputTokens = 5 }, "small-model");
        ledger.Record(new LlmUsage { InputTokens = 10, OutputTokens = 2 }, "big-model");

        Assert.Equal(132, ledger.ByModel["big-model"]);
        Assert.Equal(35, ledger.ByModel["small-model"]);

        // The session total is unchanged by attribution — it is still every token spent.
        Assert.Equal(167, ledger.TotalTokens);
    }

    /// <summary>
    /// A CALLER THAT DOES NOT NAME A MODEL STILL RECORDS. It simply does not appear in the map —
    /// better than a bucket named "unknown", which would read as a model.
    /// </summary>
    [Fact]
    public void Record_WithNoModel_CountsTowardTheTotalButNotTheBreakdown()
    {
        var ledger = new TokenLedger();

        ledger.Record(new LlmUsage { InputTokens = 50, OutputTokens = 10 });

        Assert.Equal(60, ledger.TotalTokens);
        Assert.Empty(ledger.ByModel);
    }

    // ---- what it cost -----------------------------------------------------------------------

    /// <summary>
    /// PER INSTANCE, because which endpoint is spending is the actionable fact. A fan-out routing
    /// workers to a paid instance looks identical to one that does not, if the readout is a single
    /// number.
    /// </summary>
    [Fact]
    public void Record_AccumulatesCostPerInstance()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 5, Cost = 0.001m }, "openrouter:gemini");
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 5, Cost = 0.002m }, "openrouter:gemini");
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 5, Cost = 0.004m }, "other:model");

        Assert.Equal(0.003m, l.CostByInstance["openrouter:gemini"]);
        Assert.Equal(0.004m, l.CostByInstance["other:model"]);
        Assert.Equal(0.007m, l.TotalCost);
    }

    /// <summary>
    /// AN INSTANCE THAT NEVER REPORTED IS ABSENT, not zero. A mixed session — some turns local,
    /// some paid — must show a cost against the paid instance and none against the local one, which
    /// is the honest answer to "what did this cost me".
    /// </summary>
    [Fact]
    public void AnInstanceThatReportedNoCost_IsAbsentRatherThanZero()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 5, Cost = 0.001m }, "openrouter:gemini");
        l.Record(new LlmUsage { InputTokens = 900, OutputTokens = 50 }, "local:qwen3");

        Assert.True(l.CostByInstance.ContainsKey("openrouter:gemini"));
        Assert.False(l.CostByInstance.ContainsKey("local:qwen3"));
        Assert.Equal(0.001m, l.TotalCost);
    }

    /// <summary>NULL, NOT ZERO, when nothing reported at all — a local-only session has no cost to
    /// show, and showing $0.00 claims a measurement never made.</summary>
    [Fact]
    public void WithNothingReported_TotalCostIsNull()
    {
        var l = new TokenLedger();
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 5 }, "local:qwen3");

        Assert.Null(l.TotalCost);
        Assert.Empty(l.CostByInstance);
    }

}
