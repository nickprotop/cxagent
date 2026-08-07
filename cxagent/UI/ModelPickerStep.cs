using CxAgent.Core.Llm;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// Pure narrowing of a model catalog. Separated from the UI step so the matching rules are
/// unit-testable without a live window — OpenRouter returns several hundred ids, so getting the
/// filter wrong is a usability failure that must be caught by tests, not by driving the app.
/// </summary>
public static class ModelFilter
{
    /// <summary>
    /// Case-insensitive substring match against <paramref name="query"/>, capped at
    /// <paramref name="limit"/> results. A blank/whitespace query is treated as "no query" (returns
    /// everything, up to the limit) rather than matched literally. A non-blank query that matches
    /// nothing returns an EMPTY list — it must never silently fall back to the full catalog, which
    /// would hand the user the very 400-entry list they were trying to narrow.
    /// </summary>
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> models, string? query, int limit = 40)
    {
        IEnumerable<string> seq = models;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            seq = seq.Where(m => m.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        return seq.Take(limit).ToList();
    }
}

/// <summary>
/// The outcome of a model pick. <paramref name="Cancelled"/> is true only when the user DISMISSED
/// the dialog (Escape/Cancel) — not when they submitted an empty value, which yields
/// <c>("", false)</c>. Callers that drive a wizard step need the two apart: dismiss means "go back a
/// step", empty means "re-ask this one".
/// </summary>
public record ModelPick(string? Model, bool Cancelled)
{
    public static readonly ModelPick Dismissed = new(null, Cancelled: true);
}

/// <summary>
/// Picks a model from a provider's catalog: type to narrow, choose from the narrowed list. Falls
/// back to free-text entry when the provider cannot enumerate (IModelCatalog is optional and its
/// contract is to return empty rather than throw) or when the list is empty for any other reason.
/// </summary>
public static class ModelPicker
{
    public static async Task<string?> PickAsync(
        ConsoleWindowSystem ws, Window? parent, ILlmProvider provider, string? initial, CancellationToken ct)
        => (await PickDetailedAsync(ws, parent, provider, initial, ct)).Model;

    /// <summary>
    /// As <see cref="PickAsync"/>, but distinguishes DISMISSED (the user backed out of the dialog)
    /// from "no model chosen" for any other reason.
    ///
    /// The distinction cannot be recovered from a null return: a dismissed chooser, a dismissed
    /// free-text prompt, and a blank free-text entry all yield null. Callers driving a wizard step
    /// need it, because dismiss must map to "go to the previous step" while a blank entry must map to
    /// "re-ask" — collapsing the two strands a user who pressed Escape to fix an earlier answer on a
    /// dialog with no way out.
    /// </summary>
    public static async Task<ModelPick> PickDetailedAsync(
        ConsoleWindowSystem ws, Window? parent, ILlmProvider provider, string? initial, CancellationToken ct)
    {
        IReadOnlyList<string> models = Array.Empty<string>();
        if (provider is IModelCatalog cat)
            models = await cat.ListModelsAsync(ct);

        if (models.Count == 0)
        {
            var typed = await FlowDialogs.AskAsync(ws, parent, "Model", "Model id:", initial, ct);
            // AskAsync returns null on dismiss/cancel/fault, and "" on an empty submission — so null
            // here IS a dismissal, and the two are separable at this level even though the caller of
            // PickAsync cannot see the difference.
            return typed is null ? ModelPick.Dismissed : new ModelPick(typed, Cancelled: false);
        }

        string? query = null;
        while (true)
        {
            var shown = ModelFilter.Apply(models, query);
            var options = new List<string>();
            options.Add(query is null ? "[ Filter… ]" : $"[ Filter: {query} ]");
            options.AddRange(shown);
            if (shown.Count == 0)
                options.Add("(no matches — choose Filter to change the search)");

            var picked = await FlowDialogs.ChooseAsync(
                ws, parent, $"Model ({shown.Count} of {models.Count})", options, ct);
            if (picked is null) return ModelPick.Dismissed;

            if (picked.StartsWith("[ Filter"))
            {
                query = await FlowDialogs.AskAsync(
                    ws, parent, "Filter models", "Substring to match (blank shows all):", query, ct);
                continue;
            }
            if (picked.StartsWith("(no matches")) continue;
            return new ModelPick(picked, Cancelled: false);
        }
    }
}
