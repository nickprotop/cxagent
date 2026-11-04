using System.Net;
using System.Text;
using System.Text.Json;
using CxAgent.Core.Jobs.Builtin.Search;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The search engines against canned answers. No test here touches the network.</summary>
public class SearchEngineTests
{
    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private const string TavilyAnswer = """
        { "query": "q", "results": [
            { "title": "SharpConsoleUI", "url": "https://github.com/nickprotop/ConsoleEx", "content": "A TUI framework for .NET." },
            { "title": "No url", "url": "", "content": "dropped" },
            { "title": "Second", "url": "https://example.com/2", "content": "Two." } ] }
        """;

    [Fact]
    public async Task Tavily_maps_title_url_and_content()
    {
        var outcome = await new TavilyEngine("k", new HttpClient(new Canned(HttpStatusCode.OK, TavilyAnswer)))
            .SearchAsync("q", 5, CancellationToken.None);
        Assert.Null(outcome.Failure);
        Assert.Equal(2, outcome.Hits.Count);
        Assert.Equal(new SearchHit("SharpConsoleUI", "https://github.com/nickprotop/ConsoleEx", "A TUI framework for .NET."), outcome.Hits[0]);
    }

    [Fact]
    public async Task Tavily_sends_count_and_bearer_key()
    {
        var handler = new Canned(HttpStatusCode.OK, TavilyAnswer);
        await new TavilyEngine("tvly-secret", new HttpClient(handler)).SearchAsync("dotnet tui", 7, CancellationToken.None);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("tvly-secret", request.Headers.Authorization?.Parameter);
        var sent = JsonDocument.Parse(body).RootElement;
        Assert.Equal("dotnet tui", sent.GetProperty("query").GetString());
        Assert.Equal(7, sent.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task Tavily_401_is_a_failure_naming_the_key()
    {
        var outcome = await new TavilyEngine("bad", new HttpClient(new Canned(HttpStatusCode.Unauthorized, "{}")))
            .SearchAsync("q", 5, CancellationToken.None);
        Assert.Empty(outcome.Hits);
        Assert.Contains("tavilyApiKey", outcome.Failure);
    }

    [Fact]
    public async Task Tavily_quota_is_a_failure_saying_so()
    {
        var outcome = await new TavilyEngine("k", new HttpClient(new Canned((HttpStatusCode)432, "{}")))
            .SearchAsync("q", 5, CancellationToken.None);
        Assert.Contains("quota", outcome.Failure);
    }

    [Fact]
    public void Format_numbers_and_trims_snippets()
    {
        var longSnippet = string.Join(' ', Enumerable.Repeat("word", 200));
        var text = SearchResults.Format("tavily", "q", new SearchOutcome(
            [new("One", "https://a", "short"), new("Two", "https://b", longSnippet)]));
        Assert.Contains("1. One\n   https://a\n   short", text);
        Assert.Contains("2. Two\n   https://b", text);
        var second = text[text.IndexOf("2. Two", StringComparison.Ordinal)..];
        Assert.True(second.Length < SearchResults.MaxSnippet + 40, second.Length.ToString());
        Assert.EndsWith("…", second);
    }

    [Fact]
    public void Empty_results_say_so() =>
        Assert.Equal("No results for \"q\" on tavily.", SearchResults.Format("tavily", "q", new SearchOutcome([])));

    [Fact]
    public void A_note_is_appended() =>
        Assert.EndsWith("returned 1 of 5 requested",
            SearchResults.Format("x", "q", new SearchOutcome([new("T", "https://u", "")], Note: "returned 1 of 5 requested")));

    // ---- DuckDuckGo ---------------------------------------------------------------------------

    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SearchFixtures", name));

    /// <summary>Answers each request with the next page in the list; the last one repeats.</summary>
    private sealed class Pages(params (HttpStatusCode Status, string Body)[] pages) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var (status, body) = pages[Math.Min(Seen.Count - 1, pages.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
        }
    }

    [Fact]
    public async Task Ddg_parses_titles_urls_and_snippets_and_skips_ads()
    {
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages((HttpStatusCode.OK, Page("ddg-results.html")))))
            .SearchAsync("terminal ui", 2, CancellationToken.None);
        Assert.Null(outcome.Failure);
        Assert.Equal(2, outcome.Hits.Count);
        Assert.Equal("SharpConsoleUI & ConsoleEx", outcome.Hits[0].Title);
        Assert.Equal("A terminal UI framework for .NET.", outcome.Hits[0].Snippet);
        Assert.DoesNotContain(outcome.Hits, h => h.Title.Contains("advert"));
    }

    [Fact]
    public async Task Ddg_unwraps_redirect_urls()
    {
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages((HttpStatusCode.OK, Page("ddg-results.html")))))
            .SearchAsync("terminal ui", 2, CancellationToken.None);
        Assert.Equal("https://github.com/nickprotop/ConsoleEx", outcome.Hits[0].Url);
        Assert.Equal("https://example.com/direct", outcome.Hits[1].Url);
    }

    /// <summary>ONE PLAIN REQUEST, SAYING WHO IS ASKING. The User-Agent names cxagent rather than
    /// passing for a browser: the endpoint is DuckDuckGo's, and its bot check is theirs to apply.</summary>
    [Fact]
    public async Task Ddg_sends_one_honest_request_with_the_query()
    {
        var handler = new Pages((HttpStatusCode.OK, Page("ddg-results.html")));
        await new DuckDuckGoEngine(new HttpClient(handler)).SearchAsync("terminal ui", 2, CancellationToken.None);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("cxagent", request.Headers.UserAgent.ToString());
        Assert.DoesNotContain("Mozilla", request.Headers.UserAgent.ToString());
        Assert.Contains("q=terminal+ui", body);
    }

    [Fact]
    public async Task Ddg_fetches_a_second_page_only_when_more_are_asked()
    {
        var few = new Pages((HttpStatusCode.OK, Page("ddg-results.html")));
        await new DuckDuckGoEngine(new HttpClient(few)).SearchAsync("terminal ui", 2, CancellationToken.None);
        Assert.Single(few.Seen);

        var many = new Pages((HttpStatusCode.OK, Page("ddg-results.html")), (HttpStatusCode.OK, Page("ddg-results.html")));
        var outcome = await new DuckDuckGoEngine(new HttpClient(many)).SearchAsync("terminal ui", 4, CancellationToken.None);
        Assert.Equal(2, many.Seen.Count);
        Assert.Contains("s=10", many.Seen[1].Body);
        Assert.Contains("vqd=4-123", many.Seen[1].Body);
        Assert.Equal(4, outcome.Hits.Count);
    }

    [Fact]
    public async Task Ddg_second_page_failure_keeps_the_first_with_a_note()
    {
        var handler = new Pages((HttpStatusCode.OK, Page("ddg-results.html")), ((HttpStatusCode)202, Page("ddg-anomaly.html")));
        var outcome = await new DuckDuckGoEngine(new HttpClient(handler)).SearchAsync("terminal ui", 5, CancellationToken.None);
        Assert.Null(outcome.Failure);
        Assert.Equal(2, outcome.Hits.Count);
        Assert.Contains("2 of 5", outcome.Note);
    }

    [Fact]
    public async Task Anomaly_page_is_reported_not_parsed()
    {
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages(((HttpStatusCode)202, Page("ddg-anomaly.html")))))
            .SearchAsync("terminal ui", 5, CancellationToken.None);
        Assert.Empty(outcome.Hits);
        Assert.Contains("bot check", outcome.Failure);
        Assert.Contains("Tavily", outcome.Failure);
    }

    [Fact]
    public async Task Empty_results_page_says_so()
    {
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages((HttpStatusCode.OK, Page("ddg-empty.html")))))
            .SearchAsync("zzqq", 5, CancellationToken.None);
        Assert.Null(outcome.Failure);
        Assert.Empty(outcome.Hits);
    }

    [Fact]
    public async Task Unrecognised_page_is_a_failure_not_an_empty_success()
    {
        // MARKUP CHANGED: neither results, nor "no results", nor the bot check. Saying "no results"
        // here would tell the model nothing exists when the parser simply stopped understanding.
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages((HttpStatusCode.OK, "<html><body>something new</body></html>"))))
            .SearchAsync("q", 5, CancellationToken.None);
        Assert.Contains("could not be read", outcome.Failure);
    }

    /// <summary>"no-results" IN A SCRIPT OR STYLE is not the no-results element: a changed layout that
    /// happens to mention it must still read as unreadable, never as an empty answer.</summary>
    [Fact]
    public async Task No_results_text_outside_the_element_is_not_an_empty_answer()
    {
        var page = "<html><head><style>.no-results{color:red}</style></head><body>changed layout</body></html>";
        var outcome = await new DuckDuckGoEngine(new HttpClient(new Pages((HttpStatusCode.OK, page))))
            .SearchAsync("q", 5, CancellationToken.None);
        Assert.Contains("could not be read", outcome.Failure);
    }

    private sealed class BrokenBody : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() });
        private sealed class BrokenContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
                throw new IOException("connection reset");
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
        }
    }

    /// <summary>A BODY THAT BREAKS MID-READ is a failure the executor can fall back from, not an exception.</summary>
    [Fact]
    public async Task Tavily_body_read_failure_is_a_failure()
    {
        var outcome = await new TavilyEngine("k", new HttpClient(new BrokenBody())).SearchAsync("q", 5, CancellationToken.None);
        Assert.NotNull(outcome.Failure);
    }
}
