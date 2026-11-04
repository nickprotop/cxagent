using CxAgent.Core.Jobs.Builtin.Search;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Jobs.Builtin;

/// <summary>
/// web_search: sends a query to one engine and returns a numbered list of results.
///
/// <para>IT ASKS FOR ITSELF, unlike the executors <see cref="PermissionGatedExecutor"/> wraps. What to
/// ask is only known once it runs: with no engine named, a failed Tavily search falls back to
/// DuckDuckGo, a second host the query has not been allowed to reach yet. A wrapper decides before the
/// executor starts and could ask only about the first, or about both — sending a query to DuckDuckGo
/// the user never needed to approve.</para>
/// </summary>
internal sealed class WebSearchJobExecutor(SearchEngines engines, PermissionAsker asker) : IJobExecutor
{
    /// <summary>Results when the model asks for none in particular.</summary>
    public const int DefaultCount = 5;

    /// <summary>The most a call may ask for: Tavily's own maximum, and past the point a list helps.</summary>
    public const int MaxCount = 20;

    public string TypeName => "search";
    public string DisplayName => "Web search";

    public JobSchema GetSchema() => new(TypeName, DisplayName,
    [
        new JobParamSpec("query", "string", Required: true,
            "What to search for, as you would type it into a search engine."),
        // ONLY THE ENGINES THAT WORK RIGHT NOW. Without a key the model is offered duckduckgo alone,
        // so it cannot choose an engine that would only be refused.
        new JobParamSpec("engine", "string", Required: false,
            "Which engine to use. Omit it to use the best one available.")
            { AllowedValues = [.. engines.All.Select(e => e.Name)] },
        new JobParamSpec("max_results", "integer", Required: false,
            $"How many results, default {DefaultCount}, at most {MaxCount}. Ask for more only when the "
            + "list itself is the answer; each result costs context on every later turn."),
    ]);

    public JobValidation Validate(JobParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Get("query", "")))
            return JobValidation.Invalid("'query' is required: what to search for.");
        if (parameters.Get<string?>("engine", null) is { } name && Named(name) is null)
            return JobValidation.Invalid(
                $"'engine' must be one of: {string.Join(", ", engines.All.Select(e => e.Name))}; got '{name}'.");
        return JobValidation.Valid();
    }

    public async Task<JobResult> ExecuteAsync(JobParameters parameters, IJobContext context, CancellationToken ct)
    {
        if (Validate(parameters) is { IsValid: false } invalid)
            return new JobResult { Success = false, ExitCode = -1, ErrorMessage = string.Join(" ", invalid.Errors) };

        var query = parameters.Get<string>("query").Trim();
        var count = Count(parameters);

        // A NAMED ENGINE IS THE ONLY ONE TRIED: the model chose it, and quietly answering from another
        // would hide why its choice failed. Left to us, Tavily first and DuckDuckGo behind it.
        IReadOnlyList<ISearchEngine> order = parameters.Get<string?>("engine", null) is { } named
            ? [Named(named)!]
            : engines.All;

        string? decidedBy = null;
        var earlier = new List<string>();
        var started = false;
        foreach (var engine in order)
        {
            var outcome = await asker.AskAsync(PermissionPolicy.SearchRequest(engine, query), context, ct);
            decidedBy = outcome.DeniedBy ?? decidedBy;
            context.SetDecidedBy(decidedBy);
            if (!outcome.Allowed)
            {
                // THE FIRST REFUSAL IS THE ANSWER. A refused fallback after a failed first engine
                // reports both, so the model knows nothing was searched.
                var denial = DenialMessage.For(outcome, $"search {engine.Name}: \"{query}\"");
                return new JobResult
                {
                    Success = false, ExitCode = -1, PermissionDenied = true, DecidedBy = decidedBy,
                    ErrorMessage = earlier.Count == 0 ? denial : $"{string.Join(" ", earlier)} {denial}",
                };
            }

            if (!started) { context.WorkStarting(); started = true; }
            var answer = await engine.SearchAsync(query, count, ct);
            if (answer.Failure is null)
            {
                var text = SearchResults.Format(engine.Name, query, answer);
                if (earlier.Count > 0) text = $"{string.Join(" ", earlier)} Searched {engine.Name} instead.\n\n{text}";
                return new JobResult { Success = true, DecidedBy = decidedBy, Output = { ["content"] = text } };
            }
            earlier.Add(answer.Failure);
        }

        return new JobResult { Success = false, ExitCode = -1, DecidedBy = decidedBy, ErrorMessage = string.Join(" ", earlier) };
    }

    private ISearchEngine? Named(string name) =>
        engines.All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int Count(JobParameters parameters)
    {
        int? asked;
        try { asked = parameters.Get<int?>("max_results", null); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or System.Text.Json.JsonException) { asked = null; }
        return asked is not { } n || n <= 0 ? DefaultCount : Math.Min(n, MaxCount);
    }
}
