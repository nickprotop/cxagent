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
    public void Refresh(int? contextUsed, int spentTokens, int? contextWindow, string model,
        string endpoint, int rules,
        int maxTurns = 0, int? goalTokenBudget = null, int inputTokens = 0, int outputTokens = 0,
        string sessionId = "",
        IReadOnlyList<Core.Mcp.McpServerStatus>? mcpServers = null)
    {
        var lines = new List<string>();

        // CONTEXT first: the one number that decides whether the next turn fits, and the reason the
        // panel exists at all. It was a cramped "ctx 46% · 94,102" in a status-bar corner.
        Section(lines, "Context");

        // OCCUPANCY, or nothing. Until a provider reports usage there is no measurement, and 0 reads
        // as "empty" rather than "not known yet" — the one state where saying nothing is honest.
        if (contextUsed is { } used)
        {
            lines.Add(Value($"{used:N0} tokens"));

            if (contextWindow is > 0)
            {
                // THE CEILING, not just the fraction. "1% used" does not say what of, and the window
                // is the number that makes a climbing count meaningful — 40k is nothing against 208k
                // and most of the budget against 64k. Shown beside the percentage so one line answers
                // both "how full" and "how big".
                var percent = 100.0 * used / contextWindow.Value;
                lines.Add($"[{ColorScheme.ThresholdMarkup(percent)}]{percent:N0}% of {Compact(contextWindow.Value)}[/]");
            }
        }
        else if (contextWindow is > 0)
        {
            // The window is worth stating even with nothing in it yet — it is the size the session
            // has to work within, and it does not depend on a measurement.
            lines.Add(Muted($"window {Compact(contextWindow.Value)}"));
        }

        // THE SPEND, kept but demoted. It is a real figure answering a real question, and it used to
        // be the one shown above as occupancy. Labelled, so it can never be read as "how full".
        if (spentTokens > 0)
            lines.Add(Muted($"{spentTokens:N0} spent"));

        // IN / OUT, because the two behave nothing alike and a single total hides which is growing.
        // Input dominates a long session — every turn re-sends the whole conversation — while output
        // is what the model actually produced. They also have different remedies: compress the
        // history, or ask for less. One number cannot tell you which you need.
        if (inputTokens > 0 || outputTokens > 0)
            lines.Add(Muted($"↑{Compact(inputTokens)} ↓{Compact(outputTokens)}"));

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
        lines.Add(Value(ShortPath(Directory.GetCurrentDirectory())));

        // CAPS, because they are the invisible thing that ends a run: a goal that stops "for no
        // reason" has almost always hit one.
        //
        // SHOWN UNCONDITIONALLY, which is the correction. The first version gated this on the
        // orchestrator CONFIG block, so with no such block — the common case, and the sandbox's —
        // it rendered nothing at all, and the caps stayed exactly as invisible as before. But the
        // limits still APPLY: MaxWorkerTurns falls back to 200 (`?? 200` at the call site) whether
        // or not it is configured, and the tool-result cap is a const that no config touches. A cap
        // you cannot see is one you cannot plan around.
        Section(lines, "Limits");

        // A TURN CEILING ONLY WHEN ONE EXISTS. Single-agent runs unbounded unless configured, so
        // printing "3/200" there would advertise a limit that was removed precisely because it
        // ended real work at an arbitrary number.
        lines.Add(maxTurns > 0
            ? Value($"{_turns}/{maxTurns} turns")
            : Muted($"{_turns} turns · no cap"));

        lines.Add(Muted($"{Compact(MaxToolResultChars)} tool result"));
        if (goalTokenBudget is > 0)
            lines.Add(Muted($"{Compact(goalTokenBudget.Value)} token budget"));

        // THE SESSION ID, last and muted. It is not glanceable information — nobody reads a ULID —
        // but it is the ONE string that connects what is on screen to the logs on disk, and without
        // it a user who wants to look at a session afterwards has to guess which directory by
        // timestamp.
        if (sessionId.Length > 0)
        {
            Section(lines, "Session id");
            lines.Add(Muted(sessionId));
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
        var servers = (mcpServers ?? []).Where(s => s.Enabled).ToList();
        if (servers.Count > 0)
        {
            Section(lines, "MCP");
            foreach (var server in servers)
                lines.Add(server.IsConnected
                    ? Value($"{server.Name} · {server.ToolCount} tool{(server.ToolCount == 1 ? "" : "s")}")
                    : Muted($"{server.Name} · failed"));
        }

        // PERMISSIONS as a COUNT. What was granted is a security surface, and it was invisible
        // unless the user opened Settings — you granted them, you should be able to see that you
        // did. The detail stays in Settings; this is the reminder that there is detail.
        Section(lines, "Permissions");
        lines.Add(rules == 0
            ? Muted("none granted")
            : Value($"{rules} always-allow rule{(rules == 1 ? "" : "s")}"));

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
