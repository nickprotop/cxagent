using CxAgent.Core.Jobs;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// The jobs view of the right-hand column: every background job of the session in front, with what
/// it last printed.
///
/// <para>ITS OWN PANEL, NOT A SECTION OF THE INFO PANEL. The info panel is reference — limits, ids,
/// spend — and a section of live jobs at its top pushed all of that down while it ran. F3 and F7 now
/// choose which of the two the column shows, and each keeps its full height.</para>
///
/// <para>THE SAME SURFACE as the info panel, one step off the window's, because it occupies the same
/// column and is the same kind of thing: something you glance at beside the conversation.</para>
/// </summary>
public sealed class JobsPanel
{
    /// <summary>
    /// One job as the panel shows it.
    /// </summary>
    /// <param name="View">The job and how it stands.</param>
    /// <param name="Tail">Its last lines of output, cleaned, oldest first — possibly none.</param>
    /// <param name="Agent">The sub-agent that started it, or null for the session's own agent.</param>
    public sealed record JobRow(JobView View, IReadOnlyList<string> Tail, string? Agent);

    /// <summary>How many of a job's last lines it shows. Three is a result and its context — an error
    /// and the line before it — without one chatty job filling the column.</summary>
    public const int TailLines = 3;

    private readonly MarkupControl _body = Ctl.Markup().WithMargin(1, 1, 1, 0).Build();
    private readonly ScrollablePanelControl _host;
    private string _rendered = "";
    private IReadOnlyList<JobRow> _jobs = [];
    private int _width = SessionPanel.MaxWidth;

    /// <summary>
    /// Which job ↑ ↓ point at, and Enter opens.
    ///
    /// <para>THE PANEL'S OWN, NOT THE MARKUP CONTROL'S FOCUSED LINK. That one is found from the last
    /// paint, and a panel F7 has only just shown has never been painted: focus arrived with no link to
    /// land on, nothing was highlighted, and Enter did nothing until a key had moved it. A selection
    /// kept here exists the moment the rows do.</para>
    /// </summary>
    private int _selected;

    /// <summary>A job's row was chosen, by click or by Enter — the pid, for the window to open its tab.</summary>
    public event Action<int>? JobChosen;

    public JobsPanel()
    {
        _host = Ctl.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        _host.BackgroundColor = ColorScheme.PanelSurface;
        _host.AddControl(_body);

        // A JOB'S FIRST ROW IS A LINK: clicking it, or Enter on it, opens the job. The link machinery is
        // the markup control's own, so keyboard focus, the highlight and Enter come with it.
        _body.LinkClicked += (_, e) =>
        {
            if (e.Url.StartsWith("job:", StringComparison.Ordinal)
                && int.TryParse(e.Url.AsSpan(4), out var pid))
                JobChosen?.Invoke(pid);
        };
    }

    /// <summary>The control the window places in the column.</summary>
    public IWindowControl Control => _host;

    /// <summary>What takes the keyboard when F7 brings the panel up: the job rows.</summary>
    public MarkupControl Rows => _body;

    /// <summary>The panel's markup as last rendered, for tests.</summary>
    public string RenderedText => _body.Text;

    /// <summary>Re-applies the surface after a theme change — see SessionPanel.ReapplyTheme.</summary>
    public void ReapplyTheme() => _host.BackgroundColor = ColorScheme.PanelSurface;

    /// <summary>
    /// Moves the selection one job up or down. False when there is nothing to move through, so the key
    /// is not swallowed.
    ///
    /// <para>FORWARDED BY THE WINDOW only while the rows have the keyboard, so ↑ ↓ in the composer — its
    /// history, the command list — are untouched.</para>
    /// </summary>
    public bool MoveSelection(bool down)
    {
        if (_jobs.Count == 0) return false;
        _selected = Math.Clamp(_selected + (down ? 1 : -1), 0, _jobs.Count - 1);
        Redraw();
        return true;
    }

    /// <summary>Opens the selected job — Enter on the rows. False with no jobs.</summary>
    public bool ChooseSelected()
    {
        if (_jobs.Count == 0) return false;
        JobChosen?.Invoke(_jobs[_selected].View.Job.Pid);
        return true;
    }

    /// <summary>Redraws with the last rows given — for a change of focus or selection between ticks.</summary>
    public void Redraw() => Refresh(_jobs, _width);

    /// <param name="jobs">The jobs, in the order to show them.</param>
    /// <param name="width">The column's width as laid out — see SessionPanel.SessionPanelState.Width.</param>
    public void Refresh(IReadOnlyList<JobRow> jobs, int width)
    {
        _jobs = jobs;
        _width = width;
        _selected = jobs.Count == 0 ? 0 : Math.Clamp(_selected, 0, jobs.Count - 1);

        // THE SELECTION SHOWS ONLY WHILE THE ROWS HAVE THE KEYBOARD: a highlight with nothing able to
        // act on it reads as a row that is somehow different from the others.
        var lines = Render(jobs, width, _body.HasFocus ? _selected : -1);

        // ONLY WHEN IT CHANGED. The clocks change every second while a job runs, but an idle panel
        // re-set on every tick would invalidate layout for nothing.
        var text = string.Join("\n", lines);
        if (text == _rendered) return;
        _rendered = text;
        _body.SetContent(lines);
    }

    private static List<string> Render(IReadOnlyList<JobRow> jobs, int width, int selected)
    {
        var lines = new List<string>();
        var running = jobs.Count(j => j.View.State == BackgroundJobState.Running);
        lines.Add($"[bold {ColorScheme.AccentMarkup}]{(running > 0 ? $"Jobs · {running} running" : "Jobs")}[/]");

        if (jobs.Count == 0)
        {
            lines.Add(Muted("Nothing running in the background."));
            lines.Add("");
            lines.Add(Muted("A command the agent starts with background: true appears here, with what it last printed."));
            return lines;
        }

        // THE BODY'S TWO MARGIN COLUMNS, and the two ScrollablePanelControl reserves for its scrollbar
        // once the column is taller than the screen — a row sized without them wraps its clock onto a
        // line of its own the moment the bar arrives.
        var usable = width - 4;

        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            lines.Add("");
            var view = job.View;
            var glyph = view.State switch
            {
                BackgroundJobState.Running => "[spinner]",
                BackgroundJobState.Exited when view.ExitCode == 0 => $"[{ColorScheme.AffirmativeMarkup}]✓[/]",
                _ => $"[{ColorScheme.DangerMarkup}]✗[/]",
            };

            // THE COMMAND TAKES WHAT THE GLYPH, TWO SPACES AND THE CLOCK LEAVE, padded to it so every
            // clock ends in the same column — a column of times reads at a glance.
            var clock = Clock(view.Elapsed);
            var commandWidth = Math.Max(1, usable - 3 - clock.Length);
            var command = Cut(OutputTail.OneLine(view.Job.Command), commandWidth).PadRight(commandWidth);
            var shown = index == selected ? $"[invert]{Value(command)}[/]" : Value(command);
            lines.Add($"[link=job:{view.Job.Pid}]{glyph} {shown}[/] {Muted(clock)}");

            var state = view.State switch
            {
                BackgroundJobState.Running => "running",
                BackgroundJobState.Killed => "killed",
                _ => $"exit {view.ExitCode}",
            };
            if (job.Agent is { } agent) state += $" · {agent}";
            lines.Add("  " + Muted(Cut(state, usable - 2)));

            foreach (var tail in job.Tail) lines.Add("  " + Value(Cut(tail, usable - 2)));
        }

        return lines;
    }

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    private static string Value(string text) => MarkupParser.Escape(text);

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";

    /// <summary>m:ss under an hour, h:mm:ss past it — what a stopwatch shows. Shared with the job tab, so
    /// the panel and the tab cannot disagree about how long a job has run.</summary>
    internal static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
