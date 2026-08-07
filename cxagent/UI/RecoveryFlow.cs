using CxAgent.Core.Models;
using SharpConsoleUI;
using SharpConsoleUI.Flows;

namespace CxAgent.UI;

/// <summary>
/// Presents an <see cref="AiDiagnosis"/> to the user and gets their decision — the human gate between
/// diagnosis (the LLM deciding what happened) and applying a fix (<c>DagModifier.TryApply</c>).
///
/// Follows the same split as <see cref="SetupWizard"/>: a pure projection (<see cref="Describe"/>,
/// <see cref="DescribeBreach"/>, <see cref="ChoicesFor"/>) that is fully unit-tested, and a UI-only
/// <see cref="RunAsync"/> that needs a live host and is instead covered by a tmux drive.
///
/// This class DECIDES; it never mutates the dag. The caller applies the chosen <see cref="RecoveryAction"/>
/// (e.g. via <c>DagModifier.TryApply</c>) — keeping the apply path single-authored regardless of who
/// requested the change (LLM diagnosis here, P7 copilot editing later).
/// </summary>
public static class RecoveryFlow
{
    private const string CancelChoice = "Cancel";

    /// <summary>
    /// Renders <paramref name="d"/> for a confirm dialog: the cause, why it leads to the suggested
    /// action, and the action itself in plain language (never the enum member name) — the user is
    /// approving someone else's judgement and needs all three to answer sensibly.
    /// </summary>
    public static string Describe(AiDiagnosis d) =>
        $"{d.Cause}\n\nSuggested: {ActionLabel(d.Action)}\n\n{d.Rationale}";

    /// <summary>
    /// Renders a token-budget breach: both numbers (spend and cap) and a reminder that the goal is
    /// paused awaiting a decision, not cancelled — work can still resume once the user decides.
    /// </summary>
    public static string DescribeBreach(int totalTokens, int budget) =>
        $"This goal has used {totalTokens:N0} tokens, over its budget of {budget:N0}.\n\n"
        + "The goal is paused until you decide how to proceed.";

    /// <summary>
    /// The choices to present for <paramref name="d"/>. Always includes an escape hatch ("Cancel") so
    /// the user can decline whatever the model suggests without the goal hanging. Omits "Apply" for
    /// <see cref="RecoveryAction.AskUser"/> — the model explicitly declined to decide, so offering to
    /// apply something on its behalf would be a lie.
    /// </summary>
    public static IReadOnlyList<string> ChoicesFor(AiDiagnosis d) =>
        d.Action == RecoveryAction.AskUser
            ? new[] { CancelChoice }
            : new[] { $"Apply: {ActionLabel(d.Action)}", CancelChoice };

    private static string ActionLabel(RecoveryAction a) => a switch
    {
        RecoveryAction.Retry => "retry the job unchanged",
        RecoveryAction.ModifyAndRetry => "modify its parameters and retry",
        RecoveryAction.InsertBefore => "insert a fix-up job before it and retry",
        RecoveryAction.Skip => "skip this job and let dependents proceed",
        RecoveryAction.AskUser => "ask you what to do",
        _ => a.ToString(),
    };

    /// <summary>
    /// Presents <see cref="Describe"/> and <see cref="ChoicesFor"/> to the user and maps their choice
    /// back to a <see cref="RecoveryAction"/>. Returns <c>null</c> when the user declined (Cancel,
    /// dismiss, or cancellation) — the caller must not apply anything in that case. Does NOT apply the
    /// diagnosis's modification itself; that is the caller's job via <c>DagModifier.TryApply</c>.
    /// </summary>
    public static async Task<RecoveryAction?> RunAsync(
        ConsoleWindowSystem ws, Window? parent, AiDiagnosis d, CancellationToken ct)
    {
        var result = await Flow.Run(ws, parent, async ctx =>
        {
            var choices = ChoicesFor(d);
            var chosen = await ctx.Show(
                new ChoiceStepContent(Describe(d), choices),
                "Recovery",
                FlowButtons.None);

            if (chosen is null || chosen == CancelChoice)
                return (RecoveryAction?)null;

            return d.Action;
        }, cancellationToken: ct);

        return result.Completed ? result.Value : null;
    }
}
