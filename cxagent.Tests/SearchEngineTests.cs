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
}
