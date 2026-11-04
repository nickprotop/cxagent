using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Jobs.Builtin.Search;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The search executor: which engine a call reaches, what it asks first, and what the model is told.
/// Engines are fakes; nothing here touches the network.
/// </summary>
public class WebSearchExecutorTests
{
    private sealed class FakeEngine(string name, string endpoint, SearchOutcome answer) : ISearchEngine
    {
        public string Name => name;
        public Uri Endpoint { get; } = new(endpoint);
        public List<(string Query, int Count)> Calls { get; } = [];
        public Task<SearchOutcome> SearchAsync(string query, int count, CancellationToken ct)
        {
            Calls.Add((query, count));
            return Task.FromResult(answer);
        }
    }

    private sealed class RecordingGate(params PermissionOutcome[] outcomes) : IPermissionGate
    {
        private readonly Queue<PermissionOutcome> _outcomes = new(outcomes);
        public List<PermissionRequest> Asked { get; } = [];
        public Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
        {
            Asked.Add(request);
            return Task.FromResult(_outcomes.Count > 0 ? _outcomes.Dequeue() : PermissionOutcome.Allow);
        }
    }

    private static readonly SearchOutcome Found = new([new SearchHit("T", "https://example.com/t", "snippet")]);

    private static FakeEngine Tavily(SearchOutcome? answer = null) =>
        new("tavily", "https://api.tavily.com/search", answer ?? Found);

    private static FakeEngine Ddg(SearchOutcome? answer = null) =>
        new("duckduckgo", "https://html.duckduckgo.com/html/", answer ?? Found);

    private static (WebSearchJobExecutor Executor, RecordingGate Gate) Make(ISearchEngine? tavily, ISearchEngine ddg,
        params PermissionOutcome[] outcomes)
    {
        var gate = new RecordingGate(outcomes);
        return (new WebSearchJobExecutor(new SearchEngines(tavily, ddg), new PermissionAsker(gate, null)), gate);
    }

    private static JobParameters P(params (string k, object? v)[] kv) => new(kv.ToDictionary(x => x.k, x => x.v));

    private static Task<JobResult> Run(WebSearchJobExecutor e, params (string k, object? v)[] kv) =>
        e.ExecuteAsync(P(kv), new CollectingContext(), CancellationToken.None);

    [Fact]
    public async Task Omitted_engine_uses_tavily_when_keyed()
    {
        var (tavily, ddg) = (Tavily(), Ddg());
        var (exec, gate) = Make(tavily, ddg);
        var r = await Run(exec, ("query", "tui"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Single(tavily.Calls);
        Assert.Empty(ddg.Calls);
        Assert.Equal("https://api.tavily.com", Assert.Single(gate.Asked).Subject);
    }

    [Fact]
    public async Task Omitted_engine_uses_duckduckgo_without_a_key()
    {
        var ddg = Ddg();
        var (exec, _) = Make(null, ddg);
        var r = await Run(exec, ("query", "tui"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Single(ddg.Calls);
    }

    [Fact]
    public async Task Omitted_engine_falls_back_when_tavily_fails()
    {
        var (tavily, ddg) = (Tavily(SearchOutcome.Failed("Tavily refused the API key.")), Ddg());
        var (exec, gate) = Make(tavily, ddg);
        var r = await Run(exec, ("query", "tui"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Single(ddg.Calls);
        Assert.Equal(["https://api.tavily.com", "https://html.duckduckgo.com"], gate.Asked.Select(a => a.Subject));
        var content = r.Output["content"]!.ToString()!;
        Assert.Contains("Tavily refused the API key.", content);
        Assert.Contains("duckduckgo", content);
    }

    [Fact]
    public async Task Named_engine_failure_is_reported_not_switched()
    {
        var (tavily, ddg) = (Tavily(SearchOutcome.Failed("Tavily's quota or rate limit was reached (432).")), Ddg());
        var (exec, _) = Make(tavily, ddg);
        var r = await Run(exec, ("query", "tui"), ("engine", "tavily"));
        Assert.False(r.Success);
        Assert.Contains("quota", r.ErrorMessage);
        Assert.Empty(ddg.Calls);
    }

    [Fact]
    public void Schema_offers_only_working_engines()
    {
        var (exec, _) = Make(null, Ddg());
        var engine = exec.GetSchema().Params.Single(p => p.Name == "engine");
        Assert.Equal(["duckduckgo"], engine.AllowedValues);

        var (keyed, _) = Make(Tavily(), Ddg());
        Assert.Equal(["tavily", "duckduckgo"], keyed.GetSchema().Params.Single(p => p.Name == "engine").AllowedValues);
    }

    [Fact]
    public void Unknown_engine_is_refused_naming_the_choices()
    {
        var (exec, _) = Make(null, Ddg());
        var v = exec.Validate(P(("query", "tui"), ("engine", "bing")));
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("duckduckgo"));
    }

    [Fact]
    public void Tavily_named_without_a_key_is_refused()
    {
        var (exec, _) = Make(null, Ddg());
        Assert.False(exec.Validate(P(("query", "tui"), ("engine", "tavily"))).IsValid);
    }

    [Fact]
    public async Task Denied_search_does_not_call_the_engine()
    {
        var ddg = Ddg();
        var (exec, _) = Make(null, ddg, PermissionOutcome.ByUser);
        var r = await Run(exec, ("query", "tui"));
        Assert.False(r.Success);
        Assert.True(r.PermissionDenied);
        Assert.Empty(ddg.Calls);
    }

    [Fact]
    public async Task Always_rule_is_per_engine()
    {
        var (exec, gate) = Make(Tavily(SearchOutcome.Failed("down")), Ddg());
        await Run(exec, ("query", "tui"));
        Assert.Equal(2, gate.Asked.Count);
        Assert.All(gate.Asked, a => Assert.Equal(PermissionKind.Http, a.Kind));
        Assert.NotEqual(gate.Asked[0].AlwaysRule, gate.Asked[1].AlwaysRule);
        Assert.Contains("\"tui\"", gate.Asked[0].Display);
        Assert.Contains("tavily", gate.Asked[0].Display);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(50, 20)]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(12, 12)]
    public async Task Max_results_defaults_clamps_and_floors(int? asked, int sent)
    {
        var ddg = Ddg();
        var (exec, _) = Make(null, ddg);
        await Run(exec, asked is { } n ? [("query", "tui"), ("max_results", n)] : [("query", "tui")]);
        Assert.Equal(sent, Assert.Single(ddg.Calls).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_query_is_refused_before_asking(string query)
    {
        var (exec, gate) = Make(null, Ddg());
        Assert.False(exec.Validate(P(("query", query))).IsValid);
        // AND EXECUTE DOES NOT ASK EITHER, should a caller skip Validate.
        var r = await Run(exec, ("query", query));
        Assert.False(r.Success);
        Assert.Empty(gate.Asked);
    }

    [Fact]
    public async Task Max_results_sent_as_a_json_string_is_read()
    {
        var ddg = Ddg();
        var (exec, _) = Make(null, ddg);
        await Run(exec, ("query", "tui"), ("max_results", JsonSerializer.SerializeToElement("8")));
        Assert.Equal(8, Assert.Single(ddg.Calls).Count);
    }
}
