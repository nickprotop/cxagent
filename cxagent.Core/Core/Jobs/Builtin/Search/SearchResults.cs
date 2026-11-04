using System.Text;

namespace CxAgent.Core.Jobs.Builtin.Search;

/// <summary>A search answer as the model reads it.</summary>
internal static class SearchResults
{
    /// <summary>
    /// How much of each snippet is kept. A tool result is re-sent on every later turn of the run, so
    /// twenty results with whole paragraphs each would be paid for again and again — the same reason
    /// web_fetch strips markup.
    /// </summary>
    public const int MaxSnippet = 300;

    /// <summary>Numbered entries — title, URL, snippet — or a line saying nothing matched.</summary>
    public static string Format(string engine, string query, SearchOutcome outcome)
    {
        var sb = new StringBuilder();
        if (outcome.Hits.Count == 0)
            sb.Append($"No results for \"{query}\" on {engine}.");
        else
        {
            sb.Append($"{outcome.Hits.Count} results for \"{query}\" from {engine}. Read one with web_fetch.\n");
            for (var i = 0; i < outcome.Hits.Count; i++)
            {
                var hit = outcome.Hits[i];
                sb.Append($"\n{i + 1}. {Clean(hit.Title)}\n   {hit.Url}\n");
                if (Trim(Clean(hit.Snippet)) is { Length: > 0 } snippet) sb.Append($"   {snippet}\n");
            }
        }
        if (outcome.Note is { } note) sb.Append($"\n{note}");
        return sb.ToString().TrimEnd();
    }

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // AT A WORD BOUNDARY, so a snippet never ends mid-word in a way that reads as a different word.
    private static string Trim(string text)
    {
        if (text.Length <= MaxSnippet) return text;
        var cut = text.LastIndexOf(' ', MaxSnippet);
        return text[..(cut > MaxSnippet / 2 ? cut : MaxSnippet)] + "…";
    }
}
