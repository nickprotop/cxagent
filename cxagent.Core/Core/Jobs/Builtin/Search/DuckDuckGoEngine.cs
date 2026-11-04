using System.Net;
using System.Text.RegularExpressions;

namespace CxAgent.Core.Jobs.Builtin.Search;

/// <summary>
/// DuckDuckGo's HTML results page — the engine that needs no account, so a machine with nothing
/// configured can still search.
///
/// <para>BEST EFFORT, AND SAYS SO. DuckDuckGo has no search API, and it answers automated requests with
/// a bot check whenever it decides to — the very first plain request made while building this got one.
/// This engine makes one ordinary request, names itself in the User-Agent, and when the bot check comes
/// back it reports that and points at a Tavily key. It never tries to get past the check: the endpoint
/// is DuckDuckGo's, and deciding who may use it is theirs.</para>
///
/// <para>WHAT THE PARSER RELIES ON, for the day the markup changes: each result is a
/// <c>div class="result …"</c> (ads add <c>result--ad</c>), its title the <c>a.result__a</c>, its text the
/// <c>.result__snippet</c>; result links go through <c>//duckduckgo.com/l/?uddg=&lt;the real URL&gt;</c>;
/// the next page is a form whose submit says <c>Next</c>; nothing matched is <c>div.no-results</c>; and the
/// bot check carries <c>anomaly-modal</c>. A page with none of these is reported as unreadable rather than
/// as "no results", which would tell the model nothing exists.</para>
/// </summary>
internal sealed partial class DuckDuckGoEngine(HttpClient http) : ISearchEngine
{
    /// <summary>Results per page on the HTML endpoint; a larger count needs the next page.</summary>
    private const int PerPage = 10;

    /// <summary>At most this many requests for one search, whatever the count asked.</summary>
    private const int MaxPages = 2;

    public string Name => "duckduckgo";
    public Uri Endpoint { get; } = new("https://html.duckduckgo.com/html/");

    private static readonly string UserAgent =
        $"cxagent/{typeof(DuckDuckGoEngine).Assembly.GetName().Version?.ToString(3) ?? "0"} (+https://github.com/nickprotop/cxagent)";

    public async Task<SearchOutcome> SearchAsync(string query, int count, CancellationToken ct)
    {
        var first = await PageAsync([new("q", query)], ct);
        if (first.Failure is not null) return SearchOutcome.Failed(first.Failure);

        var hits = new List<SearchHit>(first.Hits);
        var next = first.Next;
        string? note = null;
        for (var page = 1; hits.Count < count && next is not null && page < MaxPages; page++)
        {
            var more = await PageAsync(next, ct);
            if (more.Failure is not null)
            {
                // THE FIRST PAGE STILL ANSWERS. A second request is where a bot check is likeliest, and
                // losing the results already in hand to it would turn "fewer" into "none".
                note = $"Returned {hits.Count} of {count} requested; DuckDuckGo did not return more.";
                break;
            }
            hits.AddRange(more.Hits);
            next = more.Next;
        }
        return new SearchOutcome([.. hits.Take(count)], Note: note);
    }

    private sealed record Page(IReadOnlyList<SearchHit> Hits, IReadOnlyList<KeyValuePair<string, string>>? Next, string? Failure);

    private async Task<Page> PageAsync(IReadOnlyList<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.UserAgent.ParseAdd(UserAgent);

        string html;
        HttpStatusCode status;
        try
        {
            using var response = await http.SendAsync(request, ct);
            status = response.StatusCode;
            html = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex) { return new([], null, $"DuckDuckGo could not be reached: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new([], null, "DuckDuckGo did not answer in time."); }

        if (html.Contains("anomaly-modal", StringComparison.Ordinal) || html.Contains("challenge-form", StringComparison.Ordinal))
            return new([], null,
                "DuckDuckGo answered with a bot check instead of results, so this search found nothing. "
                + "It does this to automated requests at times of its choosing. For reliable search, set a "
                + "Tavily key (free, 1,000 searches a month): webSearch.tavilyApiKey in config, or F8's General tab.");

        if ((int)status is < 200 or > 299)
            return new([], null, $"DuckDuckGo answered {(int)status}.");

        var hits = new List<SearchHit>();
        foreach (Match block in ResultBlock().Matches(html))
        {
            if (block.Groups["class"].Value.Contains("result--ad", StringComparison.Ordinal)) continue;
            var body = block.Groups["body"].Value;
            if (TitleLink().Match(body) is not { Success: true } title) continue;
            var url = RealUrl(WebUtility.HtmlDecode(title.Groups["href"].Value));
            if (url is null) continue;
            var snippet = Snippet().Match(body);
            hits.Add(new SearchHit(Text(title.Groups["text"].Value), url, snippet.Success ? Text(snippet.Groups["text"].Value) : ""));
        }

        // THE ELEMENT, not the word: "no-results" in a stylesheet or a script says nothing about this page.
        if (hits.Count == 0 && !NoResults().IsMatch(html))
            return new([], null, "DuckDuckGo's page could not be read — its layout may have changed. No results were taken from it.");

        return new(hits, NextPageForm(html), null);
    }

    /// <summary>The real address behind DuckDuckGo's redirect link, or the link itself when it is direct.</summary>
    private static string? RealUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("uddg=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(part["uddg=".Length..]);
            // A DuckDuckGo link with no destination in it is one of its own pages — an ad or a tracker.
            return null;
        }
        return uri.ToString();
    }

    /// <summary>The next page's form fields, or null when there is no next page.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>>? NextPageForm(string html)
    {
        foreach (Match form in Form().Matches(html))
        {
            var body = form.Groups["body"].Value;
            if (!body.Contains("value=\"Next\"", StringComparison.Ordinal)) continue;
            return [.. Hidden().Matches(body).Select(m =>
                new KeyValuePair<string, string>(m.Groups["name"].Value, WebUtility.HtmlDecode(m.Groups["value"].Value)))];
        }
        return null;
    }

    private static string Text(string html) =>
        string.Join(' ', WebUtility.HtmlDecode(Tag().Replace(html, "")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // A result runs from its opening div to the next result's, or the end of the page.
    [GeneratedRegex("""<div class="(?<class>result [^"]*)">(?<body>.*?)(?=<div class="result |<div class="nav-link|</body>|\z)""", RegexOptions.Singleline)]
    private static partial Regex ResultBlock();

    [GeneratedRegex("""<a[^>]*class="result__a"[^>]*href="(?<href>[^"]*)"[^>]*>(?<text>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex TitleLink();

    [GeneratedRegex("""class="result__snippet"[^>]*>(?<text>.*?)</(?:a|div|td)>""", RegexOptions.Singleline)]
    private static partial Regex Snippet();

    [GeneratedRegex("""<form[^>]*>(?<body>.*?)</form>""", RegexOptions.Singleline)]
    private static partial Regex Form();

    [GeneratedRegex("""<input type="hidden" name="(?<name>[^"]*)" value="(?<value>[^"]*)"\s*/?>""")]
    private static partial Regex Hidden();

    [GeneratedRegex("""<div[^>]*class="[^"]*\bno-results\b[^"]*"[^>]*>""")]
    private static partial Regex NoResults();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();
}
