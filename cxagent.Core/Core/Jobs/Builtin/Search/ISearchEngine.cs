namespace CxAgent.Core.Jobs.Builtin.Search;

/// <summary>One place web_search can send a query.</summary>
internal interface ISearchEngine
{
    /// <summary>The name the model sees in the schema and may pass as <c>engine</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Where the query goes. Its origin is the permission rule's subject, so "always allow" covers
    /// one engine — allowing one does not send queries to another.
    /// </summary>
    Uri Endpoint { get; }

    /// <summary>
    /// Up to <paramref name="count"/> results. Never throws for a refusal the engine can describe — a
    /// rejected key, a quota, an unreadable answer — so the caller can fall back or report it.
    /// </summary>
    Task<SearchOutcome> SearchAsync(string query, int count, CancellationToken ct);
}

/// <summary>One result: what the page is called, where it is, and a few lines of what it says.</summary>
internal sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>
/// What an engine answered.
/// </summary>
/// <param name="Hits">The results, best first. Empty is a real answer: nothing matched.</param>
/// <param name="Failure">Why the engine did not answer, or null when it did. Written for the model.</param>
/// <param name="Note">Something the model should know about an answer it did get, or null.</param>
internal sealed record SearchOutcome(IReadOnlyList<SearchHit> Hits, string? Failure = null, string? Note = null)
{
    public static SearchOutcome Failed(string why) => new([], Failure: why);
}
