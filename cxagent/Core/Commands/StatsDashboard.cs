using System.Text;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Commands;

/// <summary>
/// Renders usage history as a dashboard, in markup, for the chat transcript.
///
/// <para>MARKUP RATHER THAN A TABLE OF NUMBERS. The transcript renders the framework's markup, so a
/// bar can be a bar and a gradient can carry magnitude — a column of digits makes the reader do the
/// comparison that a bar does for them. Everything here is a pure function of records: no store, no
/// terminal, no session, so the whole dashboard is testable as a string.</para>
///
/// <para>PROPORTION, NOT PRECISION. Bars are scaled to the largest row rather than to any absolute
/// ceiling: what a reader wants from "by project" is which one dominates, and an absolute scale makes
/// every bar tiny on a quiet week and every bar full on a busy one.</para>
/// </summary>
public static class StatsDashboard
{
    // A GRADIENT ACROSS THE BAR, cool where a value is small and hot where it is large. The steps are
    // deliberately few: a smooth ramp over 24 columns is invisible, while four bands read as a scale.
    private static readonly string[] Ramp = ["deepskyblue1", "cyan1", "springgreen1", "yellow1", "orange1", "red1"];

    private const char Full = '█';
    private const char Half = '▌';

    /// <summary>
    /// One proportional bar. <paramref name="fraction"/> is clamped to 0..1; the colour comes from
    /// the same fraction, so a long bar is also a hot one and the two encodings agree.
    /// </summary>
    public static string Bar(double fraction, int width = 22)
    {
        fraction = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var exact = fraction * width;
        var full = (int)exact;
        var half = exact - full >= 0.5 && full < width;

        // A SMALL VALUE IS A HALF CELL, NEVER NOTHING. A row that exists but rounds below one cell
        // rendered as bare track — indistinguishable from a row worth zero, when in fact it is a
        // project that ran, a model that was used, a tool that was called. Only a true zero is blank.
        if (!half && full == 0 && fraction > 0) half = true;

        var colour = Ramp[Math.Clamp((int)(fraction * Ramp.Length), 0, Ramp.Length - 1)];
        var bar = new string(Full, full) + (half ? Half.ToString() : "");

        // The empty remainder is drawn, muted, rather than left blank: a row of ragged-right bars
        // reads as missing data, while a visible track reads as a scale.
        var restWidth = width - full - (half ? 1 : 0);
        var rest = restWidth > 0 ? new string('─', restWidth) : "";

        return $"[{colour}]{bar}[/][{Markup.Muted}]{rest}[/]";
    }

    /// <summary>Compact magnitudes — a dashboard is read for scale, never for digits.</summary>
    public static string Compact(long n) =>
        n >= 1_000_000_000 ? $"{n / 1_000_000_000.0:0.0}B"
        : n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.0}k"
        : n.ToString();

    private static string Head(string text) =>
        $"[bold {Markup.Accent}]{text}[/]";

    private static string Muted(string text) =>
        $"[{Markup.Muted}]{text}[/]";

    /// <summary>Right-pads to a column width, for the label gutter every section shares.</summary>
    private static string Pad(string s, int w) =>
        s.Length >= w ? s[..w] : s + new string(' ', w - s.Length);

    /// <summary>Trims a project path to its last two segments — the parent folder and the project.
    /// A full path eats the width a bar needs, and the leading segments are identical across rows.
    /// </summary>
    private static string ShortProject(string path)
    {
        if (path == "(unknown)") return path;
        var parts = path.TrimEnd('/', '\\').Split('/', '\\');
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : parts[^1];
    }

    /// <summary>
    /// Everything the dashboard draws, as one value.
    ///
    /// <para>A RECORD RATHER THAN NINE PARAMETERS. Five of them are <c>IReadOnlyList</c>, and
    /// positional arguments of the same type are the shape where transposing two compiles cleanly
    /// and renders the wrong section under the wrong heading. Named members make that a build
    /// error.</para>
    ///
    /// <para>It grew this way one field at a time — the cache hit rate, then the per-agent split,
    /// then the write count — which is exactly how the long-parameter methods this codebase has
    /// already collapsed got long in the first place.</para>
    /// </summary>
    public sealed record StatsView
    {
        /// <summary>How many days the window covers, for the heading.</summary>
        public required int Days { get; init; }
        public required StatsTotals Totals { get; init; }
        public required IReadOnlyList<ProjectStat> Projects { get; init; }
        public required IReadOnlyList<ModelStat> Models { get; init; }
        public required IReadOnlyList<TypeStat> Types { get; init; }
        public required IReadOnlyList<ToolStat> Tools { get; init; }
        public required IReadOnlyList<(DateOnly Day, int Tokens)> Daily { get; init; }
        public (int Runs, int Reclaimed, int Manual) Compaction { get; init; }
        public (int Asked, int Allowed, int Denied, int Silent) Permissions { get; init; }
    }

    /// <summary>The whole dashboard.</summary>
    public static string Render(StatsView view)
    {
        var (days, totals, projects, models, types, tools, daily, compaction, permissions) =
            (view.Days, view.Totals, view.Projects, view.Models, view.Types, view.Tools,
             view.Daily, view.Compaction, view.Permissions);

        var sb = new StringBuilder();

        // NOTHING RECORDED IS ITS OWN ANSWER, not an empty dashboard. Empty sections would read as
        // "you did nothing"; this says the history is new, which is the actual state.
        if (totals.Sessions == 0)
        {
            sb.AppendLine(Head("Usage"));
            sb.AppendLine();
            sb.AppendLine(Muted($"No sessions recorded in the last {days} days."));
            sb.AppendLine(Muted("History starts recording from this version — earlier sessions were not kept."));
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine(Head($"Usage · last {days} day{(days == 1 ? "" : "s")}"));
        sb.AppendLine();

        // --- the headline ------------------------------------------------------------------------
        sb.AppendLine($"  [bold]{totals.TotalTokens:N0}[/] tokens  "
                    + Muted($"↑{Compact(totals.InputTokens)} ↓{Compact(totals.OutputTokens)}"));
        sb.AppendLine($"  [bold]{totals.Sessions}[/] session{(totals.Sessions == 1 ? "" : "s")}  "
                    + Muted($"· {totals.Turns} turns"));

        // THE WORKER SHARE, on its own line with a bar. It is the single most informative ratio a
        // fan-out user has, and it was invisible before this feature existed.
        if (totals.WorkerShare is { } share && totals.SubAgentTokens > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Bar(share)} [bold]{share:P0}[/] to workers  "
                        + Muted($"({Compact(totals.SubAgentTokens)} of {Compact(totals.TotalTokens)})"));
        }

        // THE CACHE HIT RATE, which decides what that ↑ actually COST. Input dominates every tool
        // loop — a turn re-sends everything before it — so a large ↑ looks alarming and usually is
        // not: a re-sent prefix that hits the cache is served in a fraction of the time a cold one
        // takes (43ms against 1,420ms, measured on a local endpoint). A user tuning a slow session
        // needs to know WHICH of those they have, and until now the app never asked the question.
        //
        // OMITTED ENTIRELY when no provider reported — a 0% here would send someone hunting for a
        // cache problem they do not have.
        if (totals.CacheHitRate is { } hit)
        {
            sb.AppendLine();
            sb.AppendLine($"  {Bar(hit)} [bold]{hit:P0}[/] input cached  "
                        + Muted($"({Compact(totals.CachedInputTokens)} of "
                              + $"{Compact(totals.CacheReportingInputTokens)} sent)"));

            // WHAT THE WARMING COST, where it cost anything. A local endpoint fills its own RAM for
            // free and reports no writes, so this line never appears there. On a provider that bills
            // for them — OpenAI 1.25x normal input, Anthropic up to 2x — a hit rate on its own reads
            // as pure saving, and that is the number someone would plan against.
            if (totals.CacheWrittenTokens > 0)
                sb.AppendLine(Muted($"      {Compact(totals.CacheWrittenTokens)} written to cache "
                                  + "— billed above normal input by most paid providers"));
        }

        // WHAT IT COST, when anyone said. Omitted entirely otherwise — local-only history has no
        // cost to report, and "$0.00" would claim a measurement never made.
        if (totals.Cost is { } cost)
        {
            sb.AppendLine();
            // FOUR DECIMALS BELOW A DOLLAR, matching SessionPanel.Money exactly. The panel and /stats
            // report the same figure, and a threshold that differs between them renders one $0.45
            // and the other $0.4500 — the reader is left wondering which readout is rounding.
            sb.AppendLine($"  [bold]{(cost < 1.00m ? $"${cost:0.0000}" : $"${cost:0.00}")}[/] spent  "
                        + Muted($"across {totals.CostReportingSessions} session"
                              + $"{(totals.CostReportingSessions == 1 ? "" : "s")} that reported"));
        }

        // --- daily sparkline ---------------------------------------------------------------------
        if (daily.Count > 1 && daily.Any(d => d.Tokens > 0))
        {
            sb.AppendLine();
            sb.AppendLine(Head("Daily"));
            sb.AppendLine();
            sb.AppendLine("  " + Sparkline(daily));
            sb.AppendLine("  " + Muted($"{daily[0].Day:MMM d}"
                        + new string(' ', Math.Max(1, daily.Count - 12))
                        + $"{daily[^1].Day:MMM d}"));
        }

        // --- by project --------------------------------------------------------------------------
        if (projects.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Head("By project"));
            sb.AppendLine();
            var max = (double)projects.Max(p => p.Tokens);
            foreach (var p in projects.Take(8))
                sb.AppendLine($"  {Pad(ShortProject(p.Project), 22)} {Bar(p.Tokens / max, 18)} "
                            + $"{Pad(Compact(p.Tokens), 7)}"
                            + Muted($"{p.Sessions} session{(p.Sessions == 1 ? "" : "s")}"));
        }

        // --- by instance:model ---------------------------------------------------------------------
        if (models.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Head("By instance"));
            sb.AppendLine();
            var max = (double)models.Max(m => m.TotalTokens);
            foreach (var m in models.Take(6))
                sb.AppendLine($"  {Pad(Short(m.Model), 22)} {Bar(m.TotalTokens / max, 18)} "
                            + $"{Pad(Compact(m.TotalTokens), 7)}"
                            + Muted($"↑{Compact(m.InputTokens)} ↓{Compact(m.OutputTokens)}"));
        }

        // --- by agent type -----------------------------------------------------------------------
        //
        // THE BLOCK THAT NEEDS HISTORY. Whether 41k is typical for a planner or an outlier is a
        // question about many runs, and one session holds one.
        if (types.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Head("By agent type"));
            sb.AppendLine();
            sb.AppendLine("  " + Muted(Pad("type", 12) + Pad("runs", 6) + Pad("tokens", 9)
                                     + Pad("avg turns", 11) + "outcome"));
            var max = (double)types.Max(t => t.Tokens);
            foreach (var t in types)
            {
                // CAPPED IS NAMED, not folded into failures. A capped run did not fail — it ran out
                // of room, which is a fact about the briefing rather than about the work.
                var flags = new List<string>();
                if (t.Failed > 0) flags.Add($"[{Markup.Danger}]{t.Failed} failed[/]");
                if (t.Capped > 0) flags.Add($"[yellow1]{t.Capped} capped[/]");
                var outcome = flags.Count > 0 ? string.Join(" ", flags) : Muted("all clean");

                sb.AppendLine($"  {Pad(t.Type, 12)}{Pad(t.Runs.ToString(), 6)}"
                            + $"{Pad(Compact(t.Tokens), 9)}{Pad(t.AvgTurns.ToString("0.0"), 11)}{outcome}");
                sb.AppendLine($"  {new string(' ', 12)}{Bar(t.Tokens / max, 18)}");
            }
        }

        // --- what fills the context --------------------------------------------------------------
        //
        // ORDERED BY CHARACTERS RETURNED. A turn re-sends everything before it, so a tool that
        // returns large results does not cost its size once — it costs it again every later turn.
        // This is the section that explains a session that ate millions of tokens.
        if (tools.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Head("What fills the context"));
            sb.AppendLine();
            var max = (double)tools.Max(t => t.ResultChars);
            foreach (var t in tools.Take(8))
            {
                var failed = t.Failed > 0 ? $"[{Markup.Danger}]{t.Failed} failed[/]" : "";
                sb.AppendLine($"  {Pad(t.Tool, 16)} {Bar(t.ResultChars / max, 18)} "
                            + $"{Pad(Compact(t.ResultChars) + "ch", 9)}"
                            + Muted($"{t.Calls} call{(t.Calls == 1 ? "" : "s")}")
                            + (failed.Length > 0 ? "  " + failed : ""));
            }
        }

        // --- housekeeping ------------------------------------------------------------------------
        var lines = new List<string>();
        if (compaction.Runs > 0)
            lines.Add($"  compaction  {compaction.Runs} run{(compaction.Runs == 1 ? "" : "s")}, "
                    + $"{Compact(compaction.Reclaimed)} tokens reclaimed"
                    + (compaction.Manual > 0 ? Muted($" ({compaction.Manual} manual)") : ""));

        if (permissions.Asked + permissions.Silent > 0)
            lines.Add($"  permission  {permissions.Asked} asked "
                    + Muted($"({permissions.Allowed} allowed, {permissions.Denied} denied)")
                    + (permissions.Silent > 0 ? Muted($" · {permissions.Silent} by rule") : ""));

        if (lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Head("Housekeeping"));
            sb.AppendLine();
            foreach (var l in lines) sb.AppendLine(l);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A block sparkline of daily totals, coloured by magnitude.
    ///
    /// <para>Scaled to the busiest day in the window, so the shape shows RHYTHM — which days were
    /// heavy — rather than absolute size, which the headline already gives.</para>
    /// </summary>
    public static string Sparkline(IReadOnlyList<(DateOnly Day, int Tokens)> daily)
    {
        const string blocks = " ▁▂▃▄▅▆▇█";
        var max = daily.Max(d => d.Tokens);
        if (max <= 0) return Muted(new string('─', daily.Count));

        var sb = new StringBuilder();
        foreach (var (_, tokens) in daily)
        {
            // A day with NO activity is a muted rule rather than a blank: an empty column and a
            // missing column look identical, and one of them is a fact.
            if (tokens == 0) { sb.Append($"[{Markup.Muted}]─[/]"); continue; }

            var f = (double)tokens / max;

            // FLOORED TO THE FIRST VISIBLE BLOCK, never to the space at blocks[0]. A day that used a
            // thousandth of the busiest day still HAPPENED, and rounding it to a blank makes it
            // indistinguishable from a day that did not — the very distinction the line above draws.
            var step = Math.Clamp((int)Math.Round(f * (blocks.Length - 1)), 1, blocks.Length - 1);
            var ch = blocks[step];
            var colour = Ramp[Math.Clamp((int)(f * Ramp.Length), 0, Ramp.Length - 1)];
            sb.Append($"[{colour}]{ch}[/]");
        }
        return sb.ToString();
    }

    /// <summary>Model ids are long and their tails carry the distinguishing part (a quantisation, a
    /// variant tag), so both ends are kept — the same rule the session panel follows.</summary>
    private static string Short(string modelId)
    {
        var name = modelId.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? modelId[..^5] : modelId;
        return name.Length <= 21 ? name : name[..11] + "…" + name[^9..];
    }
}
