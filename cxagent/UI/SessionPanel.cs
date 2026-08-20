using CxAgent.Core.Commands;
using System.Diagnostics;
using CxAgent.Core.Permissions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The right-hand session panel: where you stand, as opposed to what happened.
///
/// <para>The transcript answers "what happened" and answers it well. Everything here is a question
/// you would otherwise have to INTERRUPT to ask — how much context is left, which model is
/// answering, how long this has been running, which checkout is being edited, what has been granted.
/// Each was previously either crammed into a status-bar corner, printed once in a startup line that
/// scrolled away, or invisible until you opened Settings.</para>
///
/// <para>GLANCEABLE, not readable: every block is a label and one or two values. Anything needing a
/// sentence belongs in the transcript.</para>
/// </summary>
public sealed class SessionPanel
{
    /// <summary>Narrowest the panel goes. At the 100-column threshold this leaves 76 for the
    /// transcript — enough for code and diffs to stay readable, which is what the user is here
    /// for.</summary>
    public const int MinWidth = 24;

    /// <summary>
    /// Widest it goes, however large the terminal. Past this the panel stops gaining anything: its
    /// content is short labels and numbers, and a 60-column column of them is mostly empty space
    /// taken from the one pane that can always use more.
    /// </summary>
    public const int MaxWidth = 40;

    /// <summary>
    /// The panel's width for a given terminal width — a fixed SHARE, clamped at both ends.
    ///
    /// <para>A constant 24 is right at 100 columns and wrong at 200: model ids and paths wrap for
    /// no reason while a third of the screen sits unused. A share keeps the proportion the layout
    /// was designed around instead of freezing one terminal's answer.</para>
    ///
    /// <para>A FIFTH, widened from a sixth: at 160 columns a sixth gave 26, which still wrapped a
    /// long gguf model id and a nested path. The transcript keeps 80 columns at 100 and 128 at 160 —
    /// past what code needs to stay readable either way.</para>
    /// </summary>
    public static int WidthFor(int terminalWidth) =>
        Math.Clamp(terminalWidth / 5, MinWidth, MaxWidth);

    /// <summary>
    /// Terminal width at or above which the panel appears on its own. Below this the transcript
    /// needs every column it can get, and a panel would be taking a third of a narrow screen to
    /// show six numbers.
    /// </summary>
    public const int ResponsiveThreshold = 100;

    /// <summary>Mirrors WorkerToolset.MaxToolResultChars. Duplicated rather than referenced because
    /// the panel is UI and that constant is core plumbing; if it moves, this line is wrong in a way
    /// a reader can see, where a reference would silently follow it somewhere meaningless.</summary>
    private const int MaxToolResultChars = 65536;


    private readonly MarkupControl _body = Ctl.Markup().WithMargin(1, 1, 1, 0).Build();

    /// <summary>
    /// The panel's host. A SCROLLABLE panel because its content is not bounded: the caps block grows
    /// with configuration, and a short terminal would otherwise clip the blocks at the bottom with
    /// no way to reach them.
    ///
    /// <para>Its own BACKGROUND, one step off the window's. The panel is a different KIND of surface
    /// from the transcript — reference rather than conversation — and sharing a background made the
    /// column read as more transcript that happened to be narrow. One step is enough: this is a
    /// quiet edge, not a border.</para>
    /// </summary>
    private readonly ScrollablePanelControl _host;

    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    private int _turns;
    private int _toolCalls;

    /// <summary>
    /// The git block, pinned to the foot of the panel.
    ///
    /// <para>ITS OWN CONTROL, outside the scrollable body. Appended to the body it simply followed
    /// the last section, so it sat wherever the sections above happened to end — halfway up an empty
    /// panel, and scrolled out of sight on a full one. It is the block you check before committing,
    /// which means it wants a fixed place to look rather than a position that moves with whatever
    /// else the session has accumulated.</para>
    ///
    /// <para>A one-row bottom margin so it clears the pane's edge, matching the inset the composer
    /// and transcript already use.</para>
    /// </summary>
    /// <para>The SAME left margin as the body, measured rather than reasoned about: the headings
    /// above land at column 161 and this must too. I first assumed the ScrollablePanel added an
    /// inset of its own that this block bypassed, and doubled the margin to compensate — it does
    /// not, and "Git" ended up a column right of everything above it.</para>
    private readonly MarkupControl _gitBlock = Ctl.Markup().WithMargin(1, 0, 1, 1).Build();

    private readonly GridControl _layout;

    public SessionPanel()
    {
        _host = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();
        _host.BackgroundColor = ColorScheme.PanelSurface;
        _host.AddControl(_body);

        // Star over Auto: the scrollable body takes everything the git block does not, so the block
        // is pinned to the bottom edge however tall the panel is.
        _layout = Ctl.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Star(1), GridLength.Auto())
            .Place(_host, 0, 0)
            .Place(_gitBlock, 1, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
        _layout.BackgroundColor = ColorScheme.PanelSurface;
    }

    public IWindowControl Control => _layout;

    /// <summary>The rendered block text, for assertions. The body is nested inside the scrollable
    /// host now, so reading Control as a MarkupControl no longer reaches it.</summary>
    public string RenderedText => _body.Text;

    /// <summary>Counts one completed turn and its tool calls. The panel updates BETWEEN turns, not
    /// during streaming — a token counter climbing beside prose you are reading is motion competing
    /// with the thing it is meant to inform.</summary>
    public void RecordTurn(int toolCalls)
    {
        _turns++;
        _toolCalls += toolCalls;

        // A TURN IS WHEN THE TREE MAY HAVE MOVED. Tool calls are how the agent writes, so a completed
        // turn is the one moment worth paying for a fresh git reading — and it makes the five-second
        // cache invisible in the case that matters: a file written in this turn shows up immediately
        // rather than after a wait, while an idle session stops shelling out twice a second.
        if (toolCalls > 0) InvalidateGit();
    }

    /// <summary>
    /// Repaints the panel from current state.
    /// </summary>
    /// <param name="contextUsed">
    /// How full the context is RIGHT NOW, in tokens, as the provider last reported it — or null when
    /// no turn has reported usage yet.
    ///
    /// <para>TWO PARAMETERS, NOT ONE. This was a single <c>tokens</c> argument, and the caller passed
    /// the cumulative ledger total into it: the panel then divided a SUM by the window and reported
    /// the result as occupancy. Measured live, that read "19,559 tokens · 9%" on a context holding
    /// 4,441 tokens — 4.4x over, and rising quadratically because every turn re-sends the whole
    /// conversation. A slot that accepts either number is how the wrong one gets in.</para>
    /// </param>
    /// <param name="spentTokens">Cumulative tokens billed this session. A cost, never a size.</param>
    /// <param name="contextWindow">The provider's context window, when configured.</param>
    /// <param name="model">Model identifier, e.g. <c>qwen3.6-35b-a3b</c>.</param>
    /// <param name="endpoint">Where it runs, e.g. <c>local :8771</c>.</param>
    /// <param name="rules">Count of always-allow rules live for this folder.</param>
    /// <summary>
    /// Everything the panel draws, in one object.
    ///
    /// <para>IT WAS FOURTEEN PARAMETERS, most of them optional and several the same type. Callers
    /// had to count positions to add one, a named argument in the middle stopped compiling as soon
    /// as anything followed it positionally, and two adjacent <c>int</c>s could be swapped without
    /// the compiler noticing. A record makes every value named at the call site and lets new ones
    /// arrive without touching a single existing caller.</para>
    ///
    /// <para>Defaults are on the properties rather than the constructor, so a caller supplies what
    /// it knows and says nothing about the rest — a test that cares only about caps writes two
    /// lines instead of counting commas to reach the right slot.</para>
    /// </summary>
    public sealed record SessionPanelState
    {
        // --- what the session is talking to ---
        //
        // NO Model FIELD. The panel does not name the model — the banner, the composer line and the
        // status bar all do. The spend breakdown below is keyed by INSTANCE:MODEL, because two
        // `providers` entries can serve the same model on different endpoints and merging them into
        // one row answers nothing.
        public string Endpoint { get; init; } = "";
        public string? WorkingDirectory { get; init; }
        public string SessionId { get; init; } = "";

        // --- context and spend ---
        public int? ContextUsed { get; init; }
        public int? ContextWindow { get; init; }
        public int SpentTokens { get; init; }
        public int InputTokens { get; init; }
        public int OutputTokens { get; init; }
        public int SubAgentTokens { get; init; }

        /// <summary>
        /// What THIS agent spent, excluding its children — measured, never derived.
        ///
        /// <para>IT USED TO BE <c>SpentTokens - SubAgentTokens</c>, and that subtraction was wrong on
        /// both terms. SpentTokens is the parent's OWN spend (the status bar is this agent's readout,
        /// so it is fed OwnSpend), while SubAgentTokens is the SESSION-wide worker total from the
        /// shared ledger. Subtracting a session figure from an own figure drove "this agent" to zero
        /// — clamped by Math.Max — the moment any worker spent anything.</para>
        /// </summary>
        public int OwnTokens { get; init; }
        public IReadOnlyDictionary<string, int>? SpendByModel { get; init; }

        /// <summary>Share of input served from the provider's prefix cache, or null when no provider
        /// reported it — see TokenLedger.CacheHitRate for why the distinction is not cosmetic.</summary>
        public double? CacheHitRate { get; init; }

        /// <summary>Input tokens written into the provider's cache — zero where warming is free,
        /// which is every local endpoint. See TokenLedger.CacheWrittenTokens.</summary>
        public int CacheWrittenTokens { get; init; }

        /// <summary>What each instance has cost, for those that reported. An instance that reported
        /// nothing is ABSENT — see TokenLedger.CostByInstance.</summary>
        public IReadOnlyDictionary<string, decimal>? CostByInstance { get; init; }

        /// <summary>The session's total, or null when nothing reported.</summary>
        public decimal? TotalCost { get; init; }

        /// <summary>The same rate for THIS agent and for its workers, separately. Null for either
        /// when nothing reported — see TokenLedger.CacheHitRateByAgent.</summary>
        public double? OwnCacheHitRate { get; init; }
        public double? WorkerCacheHitRate { get; init; }
        public IReadOnlyDictionary<string, (int Input, int Output)>? SplitByModel { get; init; }

        // --- what bounds the run ---
        public int MaxTurns { get; init; }

        // --- what the session can reach ---
        public int Rules { get; init; }
        public IReadOnlyList<Core.Mcp.McpServerStatus>? McpServers { get; init; }
        public IReadOnlyList<string>? AgentTypes { get; init; }
        public int SkillCount { get; init; }
        public IReadOnlyList<string>? LoadedSkills { get; init; }

        /// <summary>
        /// The folder to show — what the session was given, or the process's own when it was given
        /// nothing.
        ///
        /// <para>ON THE STATE RATHER THAN IN THE RENDERING, because it is a decision about what is
        /// true, not about how to draw it. The rendering asks one question and gets one answer.</para>
        /// </summary>
        public string Folder =>
            WorkingDirectory is { Length: > 0 } given ? given : SafeCurrentDirectory();

        /// <summary>
        /// Is there a turn ceiling worth showing?
        ///
        /// <para>Zero reaches here only for an EXPLICIT opt-out — an unconfigured session resolves
        /// to the default before the panel sees it — so "no cap" now means what it says.</para>
        /// </summary>
        public bool HasTurnCap => MaxTurns > 0;
    }

    public void Refresh(SessionPanelState state)
    {
        var lines = new List<string>();

        // NO CONTEXT BLOCK, for the same reason the model block went: the status bar already carries
        // `ctx 4% · 9,140/212,992 · 160,084 spent` and is ALWAYS visible, while this panel can be
        // hidden. Occupancy, the window, the percentage and the spend total were all repeated here
        // verbatim — four lines of duplication, and the exact drift risk the note below names.
        //
        // The window alone is still worth a line before any measurement exists, since the status bar
        // shows a fraction it has no numerator for yet.
        if (state.ContextUsed is null && state.ContextWindow is > 0)
        {
            Section(lines, "Context");
            lines.Add(Muted($"window {Compact(state.ContextWindow.Value)}"));
        }

        // NO MODEL BLOCK. It moved to the line under the composer, where opencode puts it and
        // where it sits beside the mode it belongs to. Two places showing one value is how they
        // drift, and the panel is the one that can be hidden.

        Section(lines, "Session");
        lines.Add(Value($"{Elapsed()} · {_turns} turn{(_turns == 1 ? "" : "s")}"));
        lines.Add(Muted($"{_toolCalls} tool call{(_toolCalls == 1 ? "" : "s")}"));

        // WHERE, which prevents the worst class of mistake there is: editing the wrong checkout.
        Section(lines, "Location");
        // The branch used to sit here too. It moved to the Git block at the foot of the panel, where
        // it reads with the working-tree state it belongs to rather than as a footnote to the path.
        // GIVEN, NOT READ. The panel used to call Directory.GetCurrentDirectory() here, which is
        // the session's directory only while there is one session per process — the same ambient
        // read the agent stopped making. Falling back to the process keeps a caller that has no
        // opinion working, exactly as the agent's own fallback does.
        lines.Add(Value(ShortPath(state.Folder)));

        // CAPS, because they are the invisible thing that ends a run: a goal that stops "for no
        // reason" has almost always hit one.
        //
        // SHOWN UNCONDITIONALLY, which is the correction. The first version gated this on the
        // orchestrator CONFIG block, so with no such block — the common case — it rendered nothing
        // and the caps stayed as invisible as before. The limits apply either way.
        Section(lines, "Limits");

        // THE CEILING THAT BINDS, resolved by the caller — the default when nothing was configured,
        // and 0 only for an explicit opt-out. This used to receive the raw configured value, so an
        // unconfigured session read "no cap" while a real ceiling was in force.
        //
        // THE CAP IS PER GOAL; THE COUNTER ABOVE IS PER SESSION. It used to render "{_turns}/{max}",
        // pairing a session-lifetime count with a limit that resets on every prompt — so a long
        // session read "290/300 turns" and looked one prompt from death while the current goal had
        // taken three. Two different denominators sharing one slash.
        //
        // The cap is now stated ALONE, as the rule it is. The session's own turn count already has a
        // home in the Session block above, where it is not standing next to a limit it is not
        // measured against.
        lines.Add(state.HasTurnCap
            ? Value($"{state.MaxTurns} turns per goal")
            : Muted("no turn cap"));

        lines.Add(Muted($"{Compact(MaxToolResultChars)} tool result"));

        // THE SESSION ID, last and muted. It is not glanceable information — nobody reads a ULID —
        // but it is the ONE string that connects what is on screen to the logs on disk, and without
        // it a user who wants to look at a session afterwards has to guess which directory by
        // timestamp.
        if (state.SessionId.Length > 0)
        {
            Section(lines, "Session id");
            lines.Add(Muted(state.SessionId));
        }

        // MCP SERVERS, when any are configured and switched on.
        //
        // A DISABLED server is absent: it is off on purpose, and a line reporting a decision the
        // user already made is noise. A FAILED one is shown, because hiding it is indistinguishable
        // from never having configured it — and they did configure it, so silence is the one outcome
        // that leaves them nothing to act on. The reason itself needs more room than 24 columns, so
        // this says THAT it failed and /mcp says why.
        //
        // Absent entirely when there is nothing to say, like the session-id block above rather than
        // a heading over an empty list.
        var servers = (state.McpServers ?? []).Where(s => s.Enabled).ToList();
        if (servers.Count > 0)
        {
            Section(lines, "MCP");
            foreach (var server in servers)
                lines.Add(server.IsConnected
                    ? Value($"{server.Name} · {server.ToolCount} tool{(server.ToolCount == 1 ? "" : "s")}")
                    : Muted($"{server.Name} · failed"));
        }

        // SKILLS: HOW MANY EXIST, AND WHICH ARE IN FORCE RIGHT NOW.
        //
        // THE LOADED ONES ARE THE POINT. A skill loaded ten turns ago is still shaping every answer
        // with nothing on screen saying so — and when compaction removes its body it silently stops,
        // which is a behaviour change with no visible cause. This is the only surface that reports
        // that, and it reports it by DERIVING from the window rather than remembering, so the line
        // disappears exactly when the skill stops applying.
        //
        // THE COUNT IS MUTED AND THE NAMES ARE VALUED, following the MCP rows above: otherwise a
        // reader sees five lines and cannot tell which are available from which are active.
        //
        // THE PARENT'S ONLY. A child's skills live and die inside its own row, which already names
        // them, and a child is gone by the next turn while this panel persists — attributing one to
        // the session would be worse than omitting it, because a skill is not a quantity to total.
        if (state.SkillCount > 0)
        {
            Section(lines, "Skills");
            lines.Add(Muted($"{state.SkillCount} available"));
            foreach (var skill in state.LoadedSkills ?? [])
                lines.Add(Value(skill));
        }

        // THE MODEL'S PLAN IS NOT HERE, DELIBERATELY. It was, briefly, and the panel is the wrong
        // surface for it three ways: this column is 24 characters wide and a plan item is a
        // sentence; the panel shows NOW while the interesting thing about a plan is that step three
        // appeared after the model read the code; and a plan is model OUTPUT, which belongs in the
        // transcript with everything else the model produces.
        //
        // It renders as its own tool row instead — expanded, with the whole list in the body — so
        // each revision is visible in the order it happened. See TodoRow.

        // SPEND PER MODEL, when more than one model has spent anything.
        //
        // ONE MODEL NEEDS NO BREAKDOWN: the session total already says it, and a section repeating
        // that number under a heading is a line that costs space to say nothing. It appears the
        // moment a second model is involved — which today means a sub-agent type on another provider
        // instance, the case where "what did that cost" stops having an obvious answer.
        //
        // MODEL ID, NOT INSTANCE NAME. Two instances can serve the same model (a shared endpoint, a
        // second base URL), and what a user is deciding about when they read this is the MODEL —
        // instance names would split one cost across two lines for no reason they could act on.
        var byModel = (state.SpendByModel ?? new Dictionary<string, int>())
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ToList();

        // ONE SPEND BLOCK, AND IT IS AN AGGREGATE OF EVERY MODEL THAT RAN.
        //
        // This panel is the aggregator: the status bar shows the session's running total, and what
        // belongs here is the breakdown of that total across everything the session used. A
        // session-wide ↑/↓ used to sit above a per-model list, which answered two different questions
        // in one block — and the ↑/↓ read as the parent's when it was in fact the sum of every agent,
        // including children on other providers.
        //
        // SPLIT PER MODEL for the same reason the totals are split at all: input dominates a long
        // session (every turn re-sends the whole conversation) while output is what the model
        // produced, and the two have different remedies — compress the history, or ask for less. Per
        // model it is sharper still: a planner that reads a repo and returns a page is almost all
        // input; a model writing code is not.
        //
        // MODEL ID, NOT INSTANCE NAME. Two instances can serve the same model (a shared endpoint, a
        // second base URL), and what a user is deciding about when they read this is the MODEL —
        // instance names would split one cost across two lines for no reason they could act on.
        // THE UNIT IS IN THE HEADING, once. Every figure in both blocks is a token count, and a bare
        // "730" names no unit at all — the status bar's "160,084 spent" gets away with it only
        // because a verb is doing the work there. Repeating "tokens" on each line would say it four
        // times and cost columns this panel does not have; the heading says it once and governs
        // everything indented under it.
        if (byModel.Count > 0)
        {
            Section(lines, "Tokens by instance");
            foreach (var (modelId, spent) in byModel)
            {
                lines.Add(Value($"{Short(modelId)} · {spent:N0}"));

                // The split, indented under its model. Absent when the provider reported no usage
                // breakdown — a local llama.cpp build often does not — rather than showing ↑0 ↓0,
                // which would read as a measurement of nothing rather than the absence of one.
                if (state.SplitByModel is not null
                    && state.SplitByModel.TryGetValue(modelId, out var s)
                    && (s.Input > 0 || s.Output > 0))
                    lines.Add(Muted($"  ↑{Compact(s.Input)} ↓{Compact(s.Output)}"));

                // THIS INSTANCE'S COST, when it reported one. Indented under its row like the ↑/↓
                // split above, because it describes that row rather than the section.
                if (state.CostByInstance is not null
                    && state.CostByInstance.TryGetValue(modelId, out var cost))
                    lines.Add(Muted($"  {Money(cost)}"));
            }

            // CACHE HIT RATE, once, under the list rather than per instance — the ledger tracks it
            // session-wide, and inventing a per-instance figure it does not measure would be worse
            // than one honest line.
            //
            // WHY IT EARNS THE SPACE: input dominates a tool loop and grows every turn, so a big ↑
            // reads as expensive. It usually is not — a re-sent prefix that hits the cache costs a
            // fraction of a cold one (43ms against 1,420ms, measured). Without this number the panel
            // shows the alarming half of the fact and hides the reassuring half.
            //
            // ABSENT, NOT ZERO, when nobody reported: see CacheHitRate.
            if (state.CacheHitRate is { } rate)
            {
                // THE WRITES BESIDE THE HITS, but only where warming was billed. A local endpoint
                // fills its own RAM for free and reports no writes, so this stays a single clean
                // figure there. On a paid provider — OpenAI charges 1.25x normal input to write,
                // Anthropic up to 2x — a hit rate alone reads as pure saving when it is not.
                lines.Add(Muted(state.CacheWrittenTokens > 0
                    ? $"  cache {StatsDashboard.Percent(rate)} · wrote {Compact(state.CacheWrittenTokens)}"
                    : $"  cache {StatsDashboard.Percent(rate)}"));
            }

            // THE SESSION TOTAL, unindented like the cache line — both describe the section rather
            // than any one row. Shown even on a single-instance session: a reader should not have to
            // know whether one row or five produced the figure.
            if (state.TotalCost is { } total)
                lines.Add(Muted($"session {Money(total)}"));
        }

        // WORKERS, WHENEVER THEY SPENT ANYTHING — the one split the model breakdown cannot express.
        // A fan-out session normally runs its children on the PARENT'S provider, so every agent lands
        // under one model id and the list above cannot say which of them spent it. "A worker spent
        // this" is the question a fan-out session asks, and model identity never answered it.
        if (state.SubAgentTokens > 0)
        {
            Section(lines, "Tokens by agent");
            lines.Add(Value($"workers · {state.SubAgentTokens:N0}"));

            // THE CACHE RATE BESIDE THE TOKENS THAT PAID FOR IT. A parent and its children hold
            // different conversations against one endpoint, and whether that is cheap depends on the
            // server: llama.cpp parks an idle slot's KV state in host memory (--cache-ram, 8 GiB by
            // default) and restores it, so children do not thrash — but a server started with
            // -cram 0 pays full price on every switch. The session-wide figure averages the two and
            // hides which one you have: a parent at 95% conceals workers at 20%.
            if (state.WorkerCacheHitRate is { } workerRate)
                lines.Add(Muted($"  cache {StatsDashboard.Percent(workerRate)}"));

            lines.Add(Value($"this agent · {state.OwnTokens:N0}"));

            if (state.OwnCacheHitRate is { } ownRate)
                lines.Add(Muted($"  cache {StatsDashboard.Percent(ownRate)}"));
        }

        // EVERY TYPE THE MODEL CAN SPAWN, INCLUDING `general`.
        //
        // This used to filter `general` out, on the reasoning that a permanent one-line section is
        // noise on sessions that never spawn. That reads the panel as a summary of what the USER
        // configured; it is a summary of what the SESSION can do. `general` is a real capability —
        // it is what a bare spawn uses — and hiding it made delegation look unavailable to anyone
        // who had not written a type of their own.
        //
        // NAMES ONLY. The briefing is what the MODEL reads; here it would not fit 24 columns and
        // would push the sections below it off screen. Anyone who wants the text has the config file.
        var types = state.AgentTypes ?? [];
        if (types.Count > 0)
        {
            Section(lines, "Agent types");
            lines.Add(Value(string.Join(", ", types)));
        }

        // PERMISSIONS as a COUNT. What was granted is a security surface, and it was invisible
        // unless the user opened Settings — you granted them, you should be able to see that you
        // did. The detail stays in Settings; this is the reminder that there is detail.
        Section(lines, "Permissions");
        lines.Add(state.Rules == 0
            ? Muted("none granted")
            : Value($"{state.Rules} always-allow rule{(state.Rules == 1 ? "" : "s")}"));

        // GIT LAST, at the foot of the panel. It is the one block about the REPOSITORY rather than
        // the session, and it is what you check before committing rather than while working — so it
        // belongs where the eye lands last, not competing with the live counters above.
        //
        // Only inside a repo: outside one the whole block is absent rather than showing "not a
        // repository", which would be a line spent saying nothing.
        _body.SetContent(lines);

        // Rendered separately, into the control pinned at the panel's foot.
        var gitLines = new List<string>(3);
        if (CachedGit() is { } git)
        {
            Section(gitLines, "Git");
            gitLines.Add(Value(git.Branch));
            gitLines.Add(Muted(git.Status ?? "clean"));
        }

        _gitBlock.SetContent(gitLines);
    }

    /// <summary>Branch and working-tree summary, as one cached answer.</summary>
    private readonly record struct GitInfo(string Branch, string? Status);

    private GitInfo? _git;
    private DateTimeOffset _gitTakenAt = DateTimeOffset.MinValue;

    /// <summary>
    /// How long a git reading stays good for.
    ///
    /// <para>The panel refreshes every second — the elapsed-time counter needs that — and git does
    /// NOT: a branch and a working tree change when someone does something, not on a clock. Two
    /// subprocesses per second is waste on a local repo and a hazard on a network filesystem, which
    /// is the case the 500ms timeouts below exist for.</para>
    ///
    /// <para>Five seconds is short enough that a change shows up while you are still looking at the
    /// thing that caused it, and long enough to cost nothing. <see cref="InvalidateGit"/> makes it
    /// immediate for the case that matters — the agent writing a file.</para>
    /// </summary>
    private static readonly TimeSpan GitCacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Drops the cached git reading, so the next refresh takes a fresh one.
    ///
    /// <para>Called when the agent writes to disk: that is the moment the working tree changes, and
    /// waiting out the cache would show a stale "clean" beside a file the user just watched being
    /// written.</para>
    /// </summary>
    public void InvalidateGit() => _gitTakenAt = DateTimeOffset.MinValue;

    private GitInfo? CachedGit()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _gitTakenAt < GitCacheFor) return _git;

        _gitTakenAt = now;
        _git = GitBranch() is { Length: > 0 } branch ? new GitInfo(branch, GitStatus()) : null;
        return _git;
    }

    /// <summary>
    /// The working tree's shape — "3 modified · 1 untracked" — or null when it is clean.
    ///
    /// <para>COUNTS, NOT NAMES. A panel this narrow cannot list files, and the question it answers is
    /// "do I have uncommitted work?", which a count answers and a truncated filename does not.</para>
    ///
    /// <para><c>--porcelain</c> because its format is a stability promise; the human-readable output
    /// is explicitly not. Same bounded, never-throwing shape as <see cref="GitBranch"/>: git may be
    /// absent, the tree may be huge, and neither is worth a frozen panel.</para>
    /// </summary>
    private static string? GitStatus()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "status --porcelain")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;

            if (!p.WaitForExit(500)) { try { p.Kill(true); } catch (Exception) { } return null; }
            if (p.ExitCode != 0) return null;

            int changed = 0, untracked = 0;
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                if (line.Length < 2) continue;
                if (line.StartsWith("??", StringComparison.Ordinal)) untracked++;
                else changed++;
            }

            if (changed == 0 && untracked == 0) return null;

            var parts = new List<string>(2);
            if (changed > 0) parts.Add($"{changed} changed");
            if (untracked > 0) parts.Add($"{untracked} untracked");
            return string.Join(" · ", parts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Short form for a token count: 94,102 becomes "94.1k". The panel is 24 columns and
    /// two full counts on one line would not fit — and at these magnitudes the exact digits are
    /// never what the number is read for.</summary>
    private static string Compact(int n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.0}k"
        : n.ToString();

    /// <summary>
    /// Money, at the precision the figure deserves.
    ///
    /// <para>A REAL DRIVE COST $0.0147 — rendered as "$0.01" that is a number telling the reader
    /// almost nothing, and as "$0.00" it would be a lie. Four decimals while the figure is
    /// fractions of a cent, two once it is not. THE THRESHOLD IS $1, NOT $0.01: $0.0147 is itself
    /// still under a cent's worth of resolution at two decimals, so cutting over at $0.01 would
    /// have rendered exactly this example as "$0.01" — the two-decimal branch never actually
    /// engages until the figure has grown past pocket change.</para>
    /// </summary>
    private static string Money(decimal amount) =>
        amount < 1.00m ? $"${amount:0.0000}" : $"${amount:0.00}";

    private static void Section(List<string> lines, string title)
    {
        if (lines.Count > 0) lines.Add(string.Empty);   // one blank line between blocks
        lines.Add($"[bold {ColorScheme.AccentMarkup}]{title}[/]");
    }

    private static string Value(string text) =>
        SharpConsoleUI.Parsing.MarkupParser.Escape(text);

    private static string Muted(string text) =>
        $"[{ColorScheme.MutedMarkup}]{SharpConsoleUI.Parsing.MarkupParser.Escape(text)}[/]";

    private string Elapsed()
    {
        var d = DateTimeOffset.UtcNow - _started;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m {d.Seconds}s";
    }

    /// <summary>
    /// <c>~</c>-relative path, trimmed from the LEFT when too long: the tail of a path identifies it
    /// and the head repeats for every project on the machine.
    /// </summary>
    /// <summary>
    /// A model id trimmed to fit a 24-column panel.
    ///
    /// <para>Real ids run long — "qwen3.6-35b-a3b-ud-iq4_xs.gguf" is 30 characters before the panel
    /// adds a bullet and a number — and a wrapped id costs three lines to say one thing. The FRONT is
    /// what distinguishes two models; the quantisation suffix rarely does, so the tail is what goes.
    /// </para>
    /// </summary>
    private static string Short(string label)
    {
        // THE INSTANCE IS KEPT WHOLE, and the model is what gets shortened. The label is
        // `instance:model`, and the instance is the part a user chose and the part that
        // distinguishes two rows serving the SAME model — trimming it would undo the whole reason
        // the breakdown is keyed this way.
        var cut = label.IndexOf(':');
        if (cut > 0)
        {
            var instance = label[..cut];
            return $"{instance}:{Short(label[(cut + 1)..])}";
        }

        // A trailing file extension is noise here: every local model has one and none of them tells
        // a user which model they are looking at.
        var name = label.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? label[..^5] : label;
        if (name.Length <= 17) return name;

        // KEEP BOTH ENDS. Trimming only the tail was tried and is wrong on exactly the ids this
        // section exists for: "qwen3.6-35b-a3b-ud-iq4_xs" and "…-iq4_xs-alt" share their first
        // sixteen characters, so two rows rendered IDENTICALLY and the breakdown told the reader
        // nothing. What distinguishes local model ids is usually the suffix — the quantisation, a
        // variant tag — which is precisely what a tail-trim throws away.
        return name[..9] + "…" + name[^7..];
    }

    /// <summary>The process's directory, or "?" when it cannot be read — never a throw from a
    /// panel refresh.</summary>
    private static string SafeCurrentDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception) { return "?"; }
    }

    private static string ShortPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal))
            path = "~" + path[home.Length..];

        var max = MaxWidth - 2;
        return path.Length <= max ? path : "…" + path[^(max - 1)..];
    }

    /// <summary>
    /// The current git branch, or null outside a repository.
    ///
    /// <para>Shelled out rather than taking a git DEPENDENCY. LibGit2Sharp is a native library per
    /// platform for one string that `git rev-parse` returns in milliseconds, and cxagent already
    /// runs shell commands as its core function — adding a package to avoid one is the wrong
    /// trade.</para>
    ///
    /// <para>Never throws: git may be absent, the directory may not be a repo, and neither is worth
    /// a blank panel.</para>
    /// </summary>
    private static string? GitBranch()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;

            // Bounded, because a git call on a network filesystem can hang and this runs on the UI
            // thread's cadence. A missing branch line is a far smaller cost than a frozen panel.
            if (!p.WaitForExit(500)) { try { p.Kill(true); } catch (Exception) { } return null; }

            var branch = p.StandardOutput.ReadToEnd().Trim();
            return p.ExitCode == 0 && branch.Length > 0 ? branch : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
