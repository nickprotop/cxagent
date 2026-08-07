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
}
