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
    private int _input;
    private int _output;
    private bool _breachRaised;

    public TokenLedger(int? goalTokenBudget) => _budget = goalTokenBudget;

    /// <summary>
    /// A ledger carrying spend that already happened — for a session restored from disk.
    ///
    /// <para>A CONSTRUCTOR RATHER THAN A REPLAYED <see cref="Record"/> CALL, and the difference
    /// matters. Replaying one synthetic <see cref="LlmUsage"/> would reach the budget check and fire
    /// <see cref="Breached"/> on any session resumed above its budget — reporting as new an error the
    /// user was already shown in the process that crashed, at the moment they are trying to pick the
    /// work back up. The breach flag starts already-raised for the same reason: if the total is over
    /// budget on arrival, that crossing is history, and only a FURTHER crossing is news.</para>
    /// </summary>
    public TokenLedger(int? goalTokenBudget, int inputTokens, int outputTokens)
    {
        _budget = goalTokenBudget;
        _input = inputTokens;
        _output = outputTokens;
        _total = inputTokens + outputTokens;
        _breachRaised = IsBreached;
    }

    public int TotalTokens => _total;

    /// <summary>
    /// Tokens SENT so far — the conversation, re-sent in full on every call.
    ///
    /// <para>Split from output because the two behave nothing alike. Input grows with the
    /// conversation and dominates a long session (every turn re-sends everything before it), while
    /// output is what the model actually produced. A single total hides which one is growing, and
    /// they have different remedies: compress the history, or ask for less.</para>
    /// </summary>
    public int InputTokens => _input;

    /// <summary>Tokens GENERATED so far — the model's own production.</summary>
    public int OutputTokens => _output;
    public bool IsBreached => _budget is { } b && _total > b;

    /// <summary>Raised ONCE, the first time the running total crosses the budget.</summary>
    public event EventHandler<int>? Breached;

    public void Record(LlmUsage usage)
    {
        _input += usage.InputTokens;
        _output += usage.OutputTokens;
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
