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

}
