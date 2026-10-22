using SharpConsoleUI;
using SharpConsoleUI.Flows;

namespace CxAgent.UI;

/// <summary>
/// Small reusable one-off dialogs: wrap a single <see cref="ChoiceStepContent"/> call in
/// <c>Flow.Run</c>, and map cancellation/fault uniformly to <c>null</c>.
///
/// Centralised here so callers — the plugin manager's choices and confirmations — reuse one wrapper
/// instead of each hand-rolling their own <c>Flow.Run</c>.
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
                TitleOf(title),
                FlowButtons.None);

            if (chosen is null || chosen == CancelChoice)
                return (string?)null;

            return chosen;
        }, cancellationToken: ct);

        return result.Completed ? result.Value : null;
    }

    /// <summary>
    /// The window's title: the prompt's first line, which is its question. The lines after it are detail
    /// the dialog body shows in full; flattened into a one-row title they only crowd it.
    /// </summary>
    internal static string TitleOf(string prompt) => prompt.Replace("\r\n", "\n").Split('\n')[0];
}
