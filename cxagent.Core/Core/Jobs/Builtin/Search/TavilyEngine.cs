using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CxAgent.Core.Jobs.Builtin.Search;

/// <summary>
/// Tavily's search API. Its results carry text from the page itself rather than a teaser, which is
/// often enough to answer from without fetching the page at all.
/// </summary>
internal sealed class TavilyEngine(string apiKey, HttpClient http) : ISearchEngine
{
    public string Name => "tavily";
    public Uri Endpoint { get; } = new("https://api.tavily.com/search");

    public async Task<SearchOutcome> SearchAsync(string query, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            // BASIC DEPTH: one credit a search whatever the count, so more results cost context, not money.
            Content = JsonContent.Create(new { query, max_results = count, search_depth = "basic" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { return SearchOutcome.Failed($"Tavily could not be reached: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return SearchOutcome.Failed("Tavily did not answer in time."); }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return SearchOutcome.Failed(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        "Tavily refused the API key — check webSearch.tavilyApiKey.",
                    HttpStatusCode.TooManyRequests or (HttpStatusCode)432 or (HttpStatusCode)433 =>
                        $"Tavily's quota or rate limit was reached ({(int)response.StatusCode}).",
                    _ => $"Tavily answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                });

            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var hits = new List<SearchHit>();
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    foreach (var r in results.EnumerateArray())
                    {
                        var url = Text(r, "url");
                        if (url.Length == 0) continue;
                        hits.Add(new SearchHit(Text(r, "title"), url, Text(r, "content")));
                    }
                return new SearchOutcome(hits);
            }
            catch (JsonException) { return SearchOutcome.Failed("Tavily's answer was not readable JSON."); }
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
