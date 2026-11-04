namespace CxAgent.Core.Jobs.Builtin.Search;

/// <summary>
/// The engines a session can search with: Tavily when a key is configured, DuckDuckGo always.
/// </summary>
internal sealed record SearchEngines(ISearchEngine? Tavily, ISearchEngine DuckDuckGo)
{
    /// <summary>The engines, in the order an omitted <c>engine</c> tries them.</summary>
    public IReadOnlyList<ISearchEngine> All => Tavily is { } t ? [t, DuckDuckGo] : [DuckDuckGo];

    public static SearchEngines From(WebSearchSettings settings, HttpClient http) =>
        new(settings.TavilyApiKey is { Length: > 0 } key ? new TavilyEngine(key, http) : null,
            new DuckDuckGoEngine(http));
}
