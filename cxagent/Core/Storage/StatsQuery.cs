namespace CxAgent.Core.Storage;

/// <summary>Totals for one window of time.</summary>
public sealed record StatsTotals(
    int Sessions, int InputTokens, int OutputTokens, int SubAgentTokens, int Turns,
    int CachedInputTokens = 0, int CacheReportingInputTokens = 0,
    int CacheWrittenTokens = 0)
{
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>What fraction of the spend went to workers. Null when nothing was spent — a zero
    /// would read as "no delegation" rather than "no data".</summary>
    public double? WorkerShare =>
        TotalTokens > 0 ? (double)SubAgentTokens / TotalTokens : null;

    /// <summary>
    /// Share of input served from the provider's prefix cache, over the sessions that reported it.
    ///
    /// <para>THE DENOMINATOR IS ONLY THE REPORTING SESSIONS, not all input. Mixing a provider that
    /// reports with one that does not would divide real cache hits by a total including sessions
    /// that never measured, and report a hit rate lower than any session actually had.</para>
    ///
    /// <para>Null when nothing reported — see TokenLedger.CacheHitRate.</para>
    /// </summary>
    public double? CacheHitRate =>
        CacheReportingInputTokens > 0
            ? (double)CachedInputTokens / CacheReportingInputTokens
            : null;
}

/// <summary>One project's usage.</summary>
public sealed record ProjectStat(string Project, int Sessions, int Tokens, int SubAgentTokens);

/// <summary>One model's usage, split.</summary>
public sealed record ModelStat(string Model, int InputTokens, int OutputTokens, int Sessions)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// One agent type's record across every run of it.
/// </summary>
/// <param name="Capped">
/// Runs that exhausted their turn cap. SEPARATE FROM <paramref name="Failed"/>, because a capped run
/// did not fail — it ran out of room. Seen live: an explore child spent all 30 turns hunting a JSON
/// schema that is not published anywhere, then filled its last turns re-reading local files. Filed
/// under "completed" that is an invisible waste; named, it is the signal that a briefing needs a
/// give-up clause.
/// </param>
public sealed record TypeStat(
    string Type, int Runs, int Tokens, double AvgTurns, int Failed, int Capped, long AvgDurationMs);

/// <summary>
/// One tool's record. The answer to "what is actually filling my context".
/// </summary>
/// <param name="ResultChars">
/// Total characters this tool returned INTO a context. The figure that explains a 5.5M-token session:
/// a turn re-sends everything before it, so a tool that returns large results does not cost its own
/// size once — it costs its size again on every subsequent turn.
/// </param>
public sealed record ToolStat(
    string Tool, int Calls, long ResultChars, int Failed, long AvgDurationMs);

/// <summary>
/// Reads usage history into the shapes a dashboard renders.
///
/// <para>PURE FUNCTIONS OVER RECORDS, deliberately: every method here takes lists and returns lists,
/// so the whole of /stats is testable without a terminal, a database or a session. The store does the
/// SQL; this does the arithmetic.</para>
/// </summary>
public static class StatsQuery
{
    public static StatsTotals Totals(IReadOnlyList<SessionRecord> sessions) =>
        new(sessions.Count,
            sessions.Sum(s => s.InputTokens),
            sessions.Sum(s => s.OutputTokens),
            sessions.Sum(s => s.SubAgentTokens),
            sessions.Sum(s => s.Turns),
            sessions.Sum(s => s.CachedInputTokens),
            sessions.Where(s => s.CacheReported).Sum(s => s.InputTokens),
            sessions.Sum(s => s.CacheWrittenTokens));

    /// <summary>Busiest project first. Unattributed sessions are grouped rather than dropped: a
    /// session with no working directory is still spend, and hiding it would make the parts stop
    /// summing to the whole.</summary>
    public static IReadOnlyList<ProjectStat> ByProject(IReadOnlyList<SessionRecord> sessions) =>
        [.. sessions
            .GroupBy(s => s.WorkingDir ?? "(unknown)")
            .Select(g => new ProjectStat(
                g.Key, g.Count(),
                g.Sum(s => s.InputTokens + s.OutputTokens),
                g.Sum(s => s.SubAgentTokens)))
            .OrderByDescending(p => p.Tokens)];

    public static IReadOnlyList<ModelStat> ByModel(IReadOnlyList<SessionRecord> sessions) =>
        [.. sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.ModelId))
            .GroupBy(s => s.ModelId!)
            .Select(g => new ModelStat(
                g.Key, g.Sum(s => s.InputTokens), g.Sum(s => s.OutputTokens), g.Count()))
            .OrderByDescending(m => m.TotalTokens)];

    /// <summary>
    /// Every agent type that ran, costliest first.
    ///
    /// <para>This is the block that cannot be built from one session, and the reason the runs table
    /// exists: whether 41k is typical for a planner or an outlier is a question about many runs.</para>
    /// </summary>
    public static IReadOnlyList<TypeStat> ByType(IReadOnlyList<RunRecord> runs) =>
        [.. runs
            .GroupBy(r => r.TypeName)
            .Select(g => new TypeStat(
                g.Key,
                g.Count(),
                g.Sum(r => r.InputTokens + r.OutputTokens),
                g.Average(r => (double)r.Turns),
                g.Count(r => r.Outcome == "failed" || r.Outcome == "error"),
                g.Count(r => r.Outcome == "capped"),
                (long)g.Average(r => (double)r.DurationMs)))
            .OrderByDescending(t => t.Tokens)];

    /// <summary>
    /// Tools by what they put into contexts, heaviest first.
    ///
    /// <para>ORDERED BY CHARACTERS, NOT CALL COUNT. Forty cheap calls are not the problem; one tool
    /// returning 16k chars forty times is, because every one of those characters is re-sent on every
    /// later turn of that conversation.</para>
    /// </summary>
    public static IReadOnlyList<ToolStat> ByTool(IReadOnlyList<ToolCallRecord> calls) =>
        [.. calls
            .GroupBy(c => c.ToolName)
            .Select(g => new ToolStat(
                g.Key,
                g.Count(),
                g.Sum(c => (long)c.ResultChars),
                g.Count(c => c.Outcome == "failed"),
                (long)g.Average(c => (double)c.DurationMs)))
            .OrderByDescending(t => t.ResultChars)];

    /// <summary>
    /// Tokens per day, oldest first, with empty days filled in.
    ///
    /// <para>GAPS ARE ZEROES, not absences: a bar chart that silently omits quiet days compresses the
    /// timeline and makes a week of light use look like a week of heavy use.</para>
    /// </summary>
    public static IReadOnlyList<(DateOnly Day, int Tokens)> ByDay(
        IReadOnlyList<SessionRecord> sessions, DateOnly from, DateOnly to)
    {
        var byDay = sessions
            .GroupBy(s => DateOnly.FromDateTime(s.UpdatedAt.LocalDateTime))
            .ToDictionary(g => g.Key, g => g.Sum(s => s.InputTokens + s.OutputTokens));

        var days = new List<(DateOnly, int)>();
        for (var d = from; d <= to; d = d.AddDays(1))
            days.Add((d, byDay.GetValueOrDefault(d)));
        return days;
    }

    /// <summary>
    /// How much compaction reclaimed, and how often it ran.
    ///
    /// <para>Split by trigger, because "the app decided" and "the user asked" answer different
    /// questions — a threshold that never fires and one that fires constantly are indistinguishable
    /// from a single count.</para>
    /// </summary>
    public static (int Runs, int Reclaimed, int Manual) Compaction(
        IReadOnlyList<CompactionRecord> rows) =>
        (rows.Count,
         rows.Sum(r => Math.Max(0, r.BeforeTokens - r.AfterTokens)),
         rows.Count(r => r.Trigger == "manual"));

    /// <summary>
    /// Permission decisions, counted by outcome.
    ///
    /// <para><c>silent</c> is kept apart from <c>allowed</c>: one is a rule the user set once, the
    /// other is a question they answered. A session of stored rules looking like a session of choices
    /// would misrepresent how much was actually reviewed.</para>
    /// </summary>
    public static (int Asked, int Allowed, int Denied, int Silent) Permissions(
        IReadOnlyList<PermissionRecord> rows) =>
        (rows.Count(r => r.Decision is "allowed" or "denied"),
         rows.Count(r => r.Decision == "allowed"),
         rows.Count(r => r.Decision == "denied"),
         rows.Count(r => r.Decision == "silent"));
}
