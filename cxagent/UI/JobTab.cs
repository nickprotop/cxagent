using CxAgent.Core.Jobs;
using CxAgent.Core.Sessions;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// One background job's output, followed as it is written.
///
/// <para>A TAB, NOT A WINDOW, for the reason a shell tab is one: switching away is not closing it, so
/// the transcript can be read and the job come back to.</para>
///
/// <para>IT HOLDS ITS ENTRY, not a pid to look up. The board clears a finished job once its session's
/// user speaks again, and a tab being read must not empty itself because someone typed.</para>
/// </summary>
internal sealed class JobTab
{
    /// <summary>run_shell's own limit on what it hands a model, and Copy's for the same reason: the
    /// end is where a command's answer is, and the whole file is a path away.</summary>
    private const int CopyLimit = 8192;

    private readonly BackgroundJobEntry _entry;
    private readonly Session _session;
    private readonly string? _agent;
    private readonly OutputFollower? _follower;
    private readonly OutputLines _lines = new();
    private readonly MarkupControl _header = Controls.Markup().WithMargin(1, 0, 1, 0).Build();
    private readonly MarkupControl _body = Controls.Markup().WithMargin(1, 0, 1, 0).Build();
    private readonly ButtonControl _kill;
    private bool _unavailable;
    private string _headerText = "";

    /// <summary>The tab's content, for the window to add.</summary>
    public GridControl Content { get; }

    /// <summary>What a key press should land on once the tab is in front.</summary>
    public ScrollablePanelControl Scroll { get; }

    public BackgroundJobEntry Entry => _entry;

    /// <summary>The session that owns the job — the one whose jobs the window shows while this tab is
    /// in front.</summary>
    public Session Session => _session;

    /// <param name="entry">The job, held by reference — see the type's summary.</param>
    /// <param name="session">Where Copy queues the output: the session that owns the job.</param>
    /// <param name="agent">The sub-agent that started it, or null for the session's own agent.</param>
    public JobTab(BackgroundJobEntry entry, Session session, string? agent)
    {
        _entry = entry;
        _session = session;
        _agent = agent;
        _follower = entry.Job.OutputPath is { } path ? new OutputFollower(path) : null;

        // NO CONFIRMATION, as /jobs kill has none. OFF THE UI THREAD, because Kill waits up to five
        // seconds for the process to go and then raises the exit report inline — which submits to the
        // session — and neither belongs on the thread that paints the screen.
        _kill = new ButtonBuilder()
            .WithText(" Kill ")
            .WithColorRole(ColorScheme.Destructive)
            .OnClick((_, _) => Task.Run(_entry.Process.Kill))
            .Build();
        var copy = new ButtonBuilder()
            .WithText(" Copy to transcript ")
            .WithColorRole(ColorScheme.Accent)
            .OnClick((_, _) => Copy())
            .Build();

        // AT THE TOP, as the shell tab's: a toolbar under a stream of output reads as its last line.
        var bar = new ToolbarControl { ItemSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        bar.AddItem(_kill);
        bar.AddItem(copy);

        // FOLLOWING IS THE PANEL'S OWN AutoScroll: it lets go when the reader scrolls up, so a line
        // being read does not slide away, and takes hold again once they are back at the bottom.
        Scroll = Controls.ScrollablePanel().WithVerticalAlignment(VerticalAlignment.Fill).Build();
        Scroll.AutoScroll = true;
        Scroll.AddControl(_body);

        // THE HEADER IS PINNED, OUTSIDE THE SCROLL. Inside it, following the output carried the
        // command, its state and its clock off the top of the tab as soon as the output outgrew the
        // screen — the moment a long job's state is most worth seeing. Auto for the header, Star for
        // the output, the shape the session panel uses for its pinned git block.
        Content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1), GridLength.Auto(), GridLength.Star(1))
            .Place(bar, 0, 0)
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 0)
            .Place(_header, 2, 0)
            .Place(Scroll, 3, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        Tick();
    }

    /// <summary>The tab title: the state first, because that is what a glance at the strip wants.</summary>
    public string Title()
    {
        var view = _entry.View();
        var glyph = view.State switch
        {
            BackgroundJobState.Running => "●",
            BackgroundJobState.Exited when view.ExitCode == 0 => "✓",
            _ => "✗",
        };
        var command = OutputTail.OneLine(view.Job.Command);
        return $"{glyph} {(command.Length <= 24 ? command : command[..23] + "…")}";
    }

    /// <summary>One clock tick: the header's elapsed time and state, and whatever the file gained.</summary>
    public void Tick()
    {
        var view = _entry.View();
        if (view.State != BackgroundJobState.Running && _kill.Visible) _kill.Visible = false;

        var chunk = _follower?.ReadNew();
        _unavailable = _follower is null || chunk is null;

        // ONLY WHEN IT CHANGED. Re-setting identical content still invalidates layout, and this runs
        // every second for every open tab.
        var header = string.Join("\n", HeaderLines(view));
        if (header != _headerText)
        {
            _headerText = header;
            _header.SetContent(HeaderLines(view));
        }

        if (string.IsNullOrEmpty(chunk)) return;
        _lines.Append(chunk);
        _body.SetContent([.. _lines.Display().Select(MarkupParser.Escape)]);
    }

    private List<string> HeaderLines(JobView view)
    {
        var state = view.State switch
        {
            BackgroundJobState.Running => "running",
            BackgroundJobState.Killed => "killed",
            _ => $"exit {view.ExitCode}",
        };
        var lines = new List<string>
        {
            // THE WHOLE COMMAND, every line of it — the one place with room for it.
            $"[bold]{MarkupParser.Escape(view.Job.Command)}[/]",
            Muted($"pid {view.Job.Pid} · {_agent ?? "session"} · {JobsPanel.Clock(view.Elapsed)} · {state}"),
        };
        if (_follower?.StartedFromTail == true)
            lines.Add(Muted($"showing the last {OutputFollower.MaxInitialBytes / 1024} KB — "
                            + $"all of it is in {view.Job.OutputPath}"));
        if (_unavailable) lines.Add(Muted("output unavailable"));
        lines.Add("");
        return lines;
    }

    private static string Muted(string text) => $"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(text)}[/]";

    /// <summary>
    /// Queues the output's tail for the model, with the user's next message.
    ///
    /// <para>INJECT, NEVER SUBMIT — as the shell tab's copy: someone who has just read a log may be about
    /// to say something about it, or may not. The path goes with it, so the rest is reachable.</para>
    /// </summary>
    private void Copy()
    {
        var text = string.Join("\n", _lines.Display());
        if (text.Length > CopyLimit) text = text[^CopyLimit..];
        _session.Inject($"Output of the background command `{OutputTail.OneLine(_entry.Job.Command)}` "
            + $"(pid {_entry.Job.Pid}), its last {text.Length} characters — the whole of it is in "
            + $"{_entry.Job.OutputPath}:\n```\n{text}\n```");
    }
}
