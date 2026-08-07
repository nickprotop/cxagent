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
    /// <summary>
    /// Columns the panel occupies. 24 rather than opencode's 28 because the responsive threshold is
    /// 100 columns, and at 100 the transcript keeps 76 — enough for code and diffs to stay readable,
    /// which is what the user is actually here to read.
    /// </summary>
    public const int Width = 24;

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

    public SessionPanel()
    {
        _host = Ctl.ScrollablePanel()
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Build();
        _host.BackgroundColor = ColorScheme.PanelSurface;
        _host.AddControl(_body);
    }

    public IWindowControl Control => _host;

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
    }

    /// <summary>
    /// Repaints the panel from current state.
    /// </summary>
    /// <param name="tokens">Tokens used this session.</param>
    /// <param name="contextWindow">The provider's context window, when configured.</param>
    /// <param name="model">Model identifier, e.g. <c>qwen3.6-35b-a3b</c>.</param>
    /// <param name="endpoint">Where it runs, e.g. <c>local :8771</c>.</param>
    /// <param name="rules">Count of always-allow rules live for this folder.</param>
    public void Refresh(int tokens, int? contextWindow, string model, string endpoint, int rules,
        int maxTurns = 0, int? goalTokenBudget = null, int inputTokens = 0, int outputTokens = 0,
        string sessionId = "")
    {
        var lines = new List<string>();

        // CONTEXT first: the one number that decides whether the next turn fits, and the reason the
        // panel exists at all. It was a cramped "ctx 46% · 94,102" in a status-bar corner.
        Section(lines, "Context");
        lines.Add(Value($"{tokens:N0} tokens"));
        if (contextWindow is > 0)
        {
            var percent = 100.0 * tokens / contextWindow.Value;
            lines.Add($"[{ColorScheme.ThresholdMarkup(percent)}]{percent:N0}% used[/]");
        }

        // IN / OUT, because the two behave nothing alike and a single total hides which is growing.
        // Input dominates a long session — every turn re-sends the whole conversation — while output
        // is what the model actually produced. They also have different remedies: compress the
        // history, or ask for less. One number cannot tell you which you need.
        if (inputTokens > 0 || outputTokens > 0)
            lines.Add(Muted($"↑{Compact(inputTokens)} ↓{Compact(outputTokens)}"));

        // MODEL, because it was printed once in a startup line that scrolls away — twenty minutes
        // into a session there was no way to tell which model was answering.
        Section(lines, "Model");
        lines.Add(Value(model));
        if (endpoint.Length > 0) lines.Add(Muted(endpoint));

        Section(lines, "Session");
        lines.Add(Value($"{Elapsed()} · {_turns} turn{(_turns == 1 ? "" : "s")}"));
        lines.Add(Muted($"{_toolCalls} tool call{(_toolCalls == 1 ? "" : "s")}"));

        // WHERE, which prevents the worst class of mistake there is: editing the wrong checkout.
        Section(lines, "Location");
        lines.Add(Value(ShortPath(Directory.GetCurrentDirectory())));
        if (GitBranch() is { Length: > 0 } branch) lines.Add(Muted(branch));

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
        lines.Add(Value($"{_turns}/{maxTurns} turns"));
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

        // PERMISSIONS as a COUNT. What was granted is a security surface, and it was invisible
        // unless the user opened Settings — you granted them, you should be able to see that you
        // did. The detail stays in Settings; this is the reminder that there is detail.
        Section(lines, "Permissions");
        lines.Add(rules == 0
            ? Muted("none granted")
            : Value($"{rules} always-allow rule{(rules == 1 ? "" : "s")}"));

        _body.SetContent(lines);
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

        var max = Width - 2;
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
