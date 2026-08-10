using CxAgent.Core.Llm;
using Xunit;

namespace CxAgent.Tests;

public class TokenLedgerTests
{
    [Fact]
    public void Record_AccumulatesInputAndOutput()
    {
        var l = new TokenLedger(goalTokenBudget: null);
        l.Record(new LlmUsage { InputTokens = 100, OutputTokens = 50 });
        l.Record(new LlmUsage { InputTokens = 10, OutputTokens = 5 });
        Assert.Equal(165, l.TotalTokens);
    }

    [Fact]
    public void NullBudget_NeverBreaches()
    {
        var l = new TokenLedger(goalTokenBudget: null);
        l.Record(new LlmUsage { InputTokens = 10_000_000, OutputTokens = 0 });
        Assert.False(l.IsBreached);
        Assert.False(l.WouldBreach(10_000_000));
    }

    [Fact]
    public void Breached_RaisesOnce_WhenBudgetCrossed()
    {
        var l = new TokenLedger(goalTokenBudget: 100);
        int raised = 0;
        l.Breached += (_, _) => raised++;

        l.Record(new LlmUsage { InputTokens = 60, OutputTokens = 0 });   // 60 — under
        Assert.False(l.IsBreached);
        Assert.Equal(0, raised);

        l.Record(new LlmUsage { InputTokens = 60, OutputTokens = 0 });   // 120 — over
        Assert.True(l.IsBreached);
        Assert.Equal(1, raised);

        l.Record(new LlmUsage { InputTokens = 60, OutputTokens = 0 });   // still over
        Assert.Equal(1, raised);   // must NOT re-raise on every later call
    }

    [Fact]
    public void WouldBreach_IsPredictive_AndDoesNotMutate()
    {
        var l = new TokenLedger(goalTokenBudget: 100);
        l.Record(new LlmUsage { InputTokens = 90, OutputTokens = 0 });

        Assert.True(l.WouldBreach(20));    // 110 > 100
        Assert.False(l.WouldBreach(5));    // 95 <= 100
        Assert.Equal(90, l.TotalTokens);   // asking must not spend
        Assert.False(l.IsBreached);
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
        var ledger = new TokenLedger(null);

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                ledger.Record(new LlmUsage { InputTokens = 3, OutputTokens = 2 });
        });

        Assert.Equal(threads * perThread * 3, ledger.InputTokens);
        Assert.Equal(threads * perThread * 2, ledger.OutputTokens);
        Assert.Equal(threads * perThread * 5, ledger.TotalTokens);
    }

    /// <summary>
    /// THE BREACH IS ANNOUNCED ONCE, however many threads cross it together.
    ///
    /// <para>The flag was tested and then assigned — two threads could both see "not yet" and both
    /// raise, reporting one budget crossing as two separate problems. A compare-and-swap makes the
    /// check and the set one operation.</para>
    /// </summary>
    [Fact]
    public void Breached_FiresExactlyOnce_UnderConcurrentRecords()
    {
        var ledger = new TokenLedger(goalTokenBudget: 100);
        var raised = 0;
        ledger.Breached += (_, _) => Interlocked.Increment(ref raised);

        // Every one of these crosses the budget on its own, so a racy check-and-set has every chance
        // to fire more than once.
        Parallel.For(0, 16, _ => ledger.Record(new LlmUsage { InputTokens = 50, OutputTokens = 50 }));

        Assert.Equal(1, raised);
    }

    /// <summary>The total carried by the event is one that actually existed — taken from the add that
    /// crossed, not re-read afterwards when another thread may already have moved it.</summary>
    [Fact]
    public void Breached_CarriesATotalAtOrAboveTheBudget()
    {
        var ledger = new TokenLedger(goalTokenBudget: 100);
        var reported = 0;
        ledger.Breached += (_, total) => reported = total;

        ledger.Record(new LlmUsage { InputTokens = 80, OutputTokens = 40 });

        Assert.True(reported > 100, $"reported {reported}, which is not over the budget it breached");
    }
}
