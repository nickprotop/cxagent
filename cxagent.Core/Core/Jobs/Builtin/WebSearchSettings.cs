namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// What web_search may use, from config's <c>webSearch</c> block or <c>AgentConfig.WebSearch</c>.
/// </summary>
/// <param name="TavilyApiKey">
/// A Tavily key, or null. With one, searches go to Tavily (1,000 free a month, no card); without one
/// they go to DuckDuckGo, so a machine with nothing configured can still search.
/// </param>
public sealed record WebSearchSettings(string? TavilyApiKey)
{
    /// <summary>No key: DuckDuckGo only.</summary>
    public static readonly WebSearchSettings None = new((string?)null);
}
