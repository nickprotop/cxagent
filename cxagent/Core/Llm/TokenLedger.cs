namespace CxAgent.Core.Llm;

/// <summary>
/// Cumulative orchestrator token spend for ONE goal, against an optional budget.
///
/// The three failure counters (RetryCount/MaxRetries, the 2-round correction cap, ungated manual
/// diagnosis) already prevent infinite loops. This is the orthogonal COST guard for the case the spec
/// calls out: many jobs each retrying and diagnosing within their own limits, where the aggregate is
/// what the user cares about. A null budget means unbounded — the default, so cost control is opt-in
/// and nobody's first run dies on a budget they never set.
/// </summary>
public sealed class TokenLedger
{
    private readonly int? _budget;
    private int _total;
    private bool _breachRaised;

    public TokenLedger(int? goalTokenBudget) => _budget = goalTokenBudget;

    public int TotalTokens => _total;
    public bool IsBreached => _budget is { } b && _total > b;

    /// <summary>Raised ONCE, the first time the running total crosses the budget.</summary>
    public event EventHandler<int>? Breached;

    public void Record(LlmUsage usage)
    {
        _total += usage.InputTokens + usage.OutputTokens;
        if (!_breachRaised && IsBreached)
        {
            _breachRaised = true;
            Breached?.Invoke(this, _total);
        }
    }

    /// <summary>Would spending <paramref name="estimatedTokens"/> more cross the budget? Does not spend.</summary>
    public bool WouldBreach(int estimatedTokens) =>
        _budget is { } b && _total + estimatedTokens > b;
}
