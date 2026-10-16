using CxAgent.Core.Llm;

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
/// Asks an instance that is not saved yet — or is being edited — which models it serves.
///
/// <para>A THROWAWAY PROVIDER, built from the values on screen rather than the registry's: the point is
/// to list what the edited endpoint and key reach, before anything is written.</para>
/// </summary>
public static class ModelCatalogQuery
{
    public static async Task<ModelCatalogResult> ListAsync(string name, ProviderInstanceConfig cfg, CancellationToken ct)
    {
        try
        {
            var settings = new ProviderSettings(
                new Dictionary<string, ProviderInstanceConfig> { [name] = cfg }, name,
                Array.Empty<string>(), new Dictionary<string, RoutingTarget>());
            var provider = ProviderRegistry.Build(settings).Default;
            if (provider is IModelCatalogDetail detail) return await detail.ListModelDetailsAsync(ct);
            if (provider is IModelCatalog catalog)
                return new ModelCatalogResult([.. (await catalog.ListModelsAsync(ct)).Select(id => new ModelInfo(id))]);
            return new ModelCatalogResult([], "this provider cannot list its models");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A PROVIDER THAT CANNOT EVEN BE BUILT — a bad URL, an unknown kind — is an answer here, not
            // a failure of the dialog: the user is mid-edit, and the reason is what they need.
            return new ModelCatalogResult([], ex.Message);
        }
    }
}
