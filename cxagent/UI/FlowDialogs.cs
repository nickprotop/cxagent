using SharpConsoleUI;
using SharpConsoleUI.Flows;

namespace CxAgent.UI;

/// <summary>
/// Small reusable one-off dialogs driven outside a wizard, following the pattern established by
/// <see cref="RecoveryFlow.RunAsync"/>: wrap a single <see cref="ChoiceStepContent"/> or
/// <c>ctx.Prompt</c> call in <c>Flow.Run</c>, and map cancellation/fault uniformly to <c>null</c>.
///
/// Centralised here so later callers (the model catalog editor, the role editor) reuse these
/// instead of each hand-rolling their own <c>Flow.Run</c> wrapper.
/// </summary>
public static class FlowDialogs
{
    private const string CancelChoice = "Cancel";

    /// <summary>
    /// Presents <paramref name="choices"/> as a button-per-option dialog and returns the chosen
    /// value, or <c>null</c> on cancel/dismiss/fault.
    /// </summary>
    /// <param name="appendCancel">
    /// When true (the default), a trailing "Cancel" choice is appended so the user always has an
    /// escape hatch. Pass false when the caller supplies its own terminal choice with different
    /// semantics — a menu whose last entry is "Done" (save and exit) must not also offer a synthetic
    /// "Cancel" (discard), because the two are not the same and showing both reads as ambiguous.
    /// Dismissing the dialog still yields <c>null</c> either way, so the escape hatch is never lost.
    /// </param>
    public static async Task<string?> ChooseAsync(
        ConsoleWindowSystem ws, Window? parent, string title, IReadOnlyList<string> choices,
        CancellationToken ct, bool appendCancel = true)
    {
        var result = await Flow.Run(ws, parent, async ctx =>
        {
            var withCancel = appendCancel ? choices.Concat(new[] { CancelChoice }).ToList() : choices.ToList();
            var chosen = await ctx.Show(
                new ChoiceStepContent(title, withCancel),
                title,
                FlowButtons.None);

            if (chosen is null || chosen == CancelChoice)
                return (string?)null;

            return chosen;
        }, cancellationToken: ct);

        return result.Completed ? result.Value : null;
    }

    /// <summary>
    /// Presents a single free-text prompt and returns the entered text, or <c>null</c> on
    /// cancel/dismiss/fault.
    /// </summary>
    public static async Task<string?> AskAsync(
        ConsoleWindowSystem ws, Window? parent, string title, string message, string? initial, CancellationToken ct)
    {
        var result = await Flow.Run(ws, parent, async ctx =>
        {
            return await ctx.Prompt(title, message, initial: initial);
        }, cancellationToken: ct);

        return result.Completed ? result.Value : null;
    }
}
