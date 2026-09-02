using System.Diagnostics;
using System.Runtime.Versioning;
using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Events;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;

namespace CxAgent.UI;

/// <summary>
/// A shell that lives in a tab, for a bare <c>/shell</c>.
///
/// <para>A WINDOW IS TRANSIENT AND A TAB IS A PLACE. <c>/shell &lt;command&gt;</c> keeps its window:
/// it appears, shows a result, and you dismiss it. A bare shell is a workspace you leave running and
/// come back to, and switching away from a tab is not closing it — so the command survives you
/// reading the transcript, which is the whole reason it wants one.</para>
///
/// <para>ITS OUTPUT IS NOT SENT BY DEFAULT. You did not open it FOR the agent, so nothing goes back
/// until you press the button — a decision made after reading rather than a checkbox ticked before
/// you knew what would happen.</para>
/// </summary>
internal static class ShellTab
{
    /// <summary>
    /// How many shell tabs have been opened, for naming them apart.
    ///
    /// <para>SEVERAL ARE ALLOWED. The window refuses a second because two transcripts would land in
    /// an order nobody controls — a tab strip does not have that problem: the tabs are named and
    /// ordered, and each decides its own copy-back.</para>
    ///
    /// <para>IT ONLY GOES UP. Reusing a freed number would put "Shell 2" beside a "Shell 2" the user
    /// still remembers closing, and the number is identity rather than a count of what is open.</para>
    /// </summary>
    private static int _opened;

    /// <summary>
    /// Opens the shell tab, or switches to it when it is already there.
    ///
    /// <para>SWITCHES RATHER THAN REFUSES. The window refuses a second <c>/shell</c> because a
    /// second transcript would land in an order nobody controls. A tab is a workspace, and asking
    /// for it again means "take me there".</para>
    ///
    /// <para>A DEAD TAB IS REPLACED. <c>exit</c> leaves the tab in place — the last screen is worth
    /// reading and a tab that vanished under you is worse than one that waits — so asking for a
    /// shell again builds a live terminal where the dead one was. LazyDotIde does the same, for the
    /// same reason.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    public static void Open(ConsoleWindowSystem system, MainWindow main, Session session)
    {
        var title = ++_opened == 1 ? "Shell" : $"Shell {_opened}";

        var child = ShellCommandLine.For("");
        var terminal = new TerminalBuilder()
            .WithExe(child.Exe)
            .WithWorkingDirectory(session.WorkingDirectory)
            // KEPT OPEN SO THE HANDLER DECIDES. The control would close its containing WINDOW on
            // exit, which is not what hosts this — the tab is closed below, after the transcript has
            // been read off the terminal that is about to go.
            .KeepOpenOnExit()
            .Build();
        terminal.HorizontalAlignment = HorizontalAlignment.Stretch;
        terminal.VerticalAlignment = VerticalAlignment.Fill;

        // A CHECKBOX FOR EXIT, A BUTTON FOR NOW. `exit` closes the tab, so a button alone would be
        // unreachable at the moment it matters most — the decision has to be made BEFORE the shell
        // ends. The button covers the other case: sending what has happened so far without leaving.
        //
        // OFF BY DEFAULT. A bare shell is one you opened for yourself, not for the agent; a session
        // that turns out to be worth sharing says so, rather than being shared unless stopped.
        var copyOnExit = new CheckboxBuilder()
            .WithLabel("copy back on exit")
            .Checked(false)
            .Build();

        var copyNow = new ButtonBuilder()
            .WithText(" Copy now ")
            .WithColorRole(ColorScheme.Accent)
            .OnClick((_, _) => Report(session, terminal))
            .Build();

        // AT THE TOP, WITH A RULE UNDER IT, matching the /shell window. The controls belong to the
        // TAB, not to the output: a toolbar under a stream of text reads as the last line the shell
        // printed.
        var bar = new ToolbarControl
        {
            ItemSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bar.AddItem(copyOnExit);
        bar.AddItem(copyNow);

        var content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1), GridLength.Star(1))
            .Place(bar, 0, 0)
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 0)
            .Place(terminal, 2, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        main.AddTab(title, content);
        Focus(main, terminal);

        // EXIT MEANS EXIT. The shell ended because the user typed `exit`, so the tab goes with it —
        // a dead tab left behind is clutter, and with several shells open "replace the dead one" is
        // ambiguous. What they chose to send is sent first, while the terminal still holds it.
        terminal.ProcessExited += (_, _) => system.EnqueueOnUIThread(() =>
        {
            if (copyOnExit.Checked) Report(session, terminal);

            Close(main, title);
        });

        // ADVISORY, NOT A VETO. TabCloseRequested does not remove the tab — the handler does — so the
        // confirmation below is a plain question with no ordering to get wrong. The window's version
        // had to cancel a close already in progress, and that took four attempts.
        //
        // THE TITLE IDENTIFIES THE TAB, because the event carries every closable tab in the control
        // and each shell subscribes its own handler.
        main.Tabs.TabCloseRequested += OnCloseRequested;

        void OnCloseRequested(object? _, TabEventArgs e)
        {
            if (e.TabPage.Title != title) return;

            // NOTHING RUNNING MEANS NOTHING TO ASK — the child is already gone and the tab is only
            // holding a last screen.
            if (terminal.IsDisposed)
            {
                main.Tabs.TabCloseRequested -= OnCloseRequested;
                if (copyOnExit.Checked) Report(session, terminal);
                Close(main, title);
                return;
            }

            AskThenClose(system, main, session, terminal, title, copyOnExit.Checked,
                () => main.Tabs.TabCloseRequested -= OnCloseRequested);
        }
    }

    /// <summary>
    /// Asks before killing a shell that is still running, then closes.
    ///
    /// <para>A CONFIRMATION IS CHEAP AND LOSING A RUNNING COMMAND IS NOT. The question does not name
    /// a command because a bare shell has none — what is at stake is the session itself.</para>
    ///
    /// <para>APP-MODAL, NOT PARENTED to the window it is about to change: a modal whose parent is
    /// destroyed underneath it stays on screen needing a second dismissal, which is the mistake the
    /// terminal window's dialog made and took four attempts to unpick.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private static void AskThenClose(ConsoleWindowSystem system, MainWindow main, Session session,
                                     TerminalControl terminal, string title, bool copyBack,
                                     Action unsubscribe)
    {
        Window? dialog = null;
        void Dismiss()
        {
            if (dialog is not null) system.CloseWindow(dialog, activateParent: true, force: true);
        }

        var body = Controls.Markup()
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.CautionMarkup}]{MarkupParser.Escape(title)} is still running.[/]")
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.MutedMarkup}]Closing it ends the shell and anything it started.[/]")
            .AddEmptyLine()
            .Build();

        var buttons = Controls.Toolbar()
            .WithSpacing(2)
            .WithAlignment(HorizontalAlignment.Center)
            .AddButton(new ButtonBuilder()
                .WithText("Close it")
                .WithColorRole(ColorScheme.Destructive)
                // THE DIALOG GOES FIRST, then the work — closing the tab while its modal is up
                // leaves the modal on screen with nothing able to dismiss it.
                .OnClick((_, _) =>
                {
                    Dismiss();
                    unsubscribe();

                    // READ BEFORE THE KILL. GetTranscript survives disposal, but what the user asked
                    // for is what the shell SHOWED, and reading it first needs no reasoning about
                    // what a dying terminal still holds.
                    if (copyBack) Report(session, terminal);

                    Kill(terminal);
                    Close(main, title);
                }))
            .AddButton(new ButtonBuilder()
                .WithText("Keep running")
                .WithColorRole(ColorScheme.Affirmative)
                .OnClick((_, _) => { Dismiss(); Focus(main, terminal); }));

        dialog = new WindowBuilder(system)
            .WithTitle(" Still running ")
            .WithSize(60, 11)
            .Centered()
            .AsModal()
            .WithBackgroundColor(ColorScheme.ChatSurface)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(ColorScheme.AccentRgb)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            // ESCAPE KEEPS IT RUNNING. The safe answer is the one a reflex reaches.
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;

                Dismiss();
                Focus(main, terminal);
                e.Handled = true;
            })
            .AddControl(body)
            .AddControl(buttons.StickyBottom().Build())
            .Build();

        system.AddWindow(dialog);
    }

    /// <summary>Removes the tab by title, and lands the user back in the conversation.</summary>
    private static void Close(MainWindow main, string title)
    {
        if (Find(main, title) is { } index) main.CloseTab(index);

        main.ShowChatTab();
    }

    /// <summary>This shell's tab index, or null when it has already gone.</summary>
    private static int? Find(MainWindow main, string title)
    {
        for (var i = 0; i < main.Tabs.TabCount; i++)
            if (main.Tabs.GetTab(i)?.Title == title) return i;

        return null;
    }

    /// <summary>Puts the keyboard in the terminal, not on the toolbar above it.</summary>
    private static void Focus(MainWindow main, TerminalControl terminal)
        => main.Window?.FocusManager.SetFocus(terminal, FocusReason.Programmatic);

    /// <summary>
    /// Sends what the shell has shown to the model.
    ///
    /// <para>INJECT, NEVER SUBMIT. It queues and starts no turn: someone who has just read a build
    /// log may be about to say something, or may be reading, or gone.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private static void Report(Session session, TerminalControl terminal)
        => session.Inject(ShellTranscript.Render(
            new ShellOutcome("", terminal.GetTranscript(), terminal.ExitCode)));

    /// <summary>
    /// Stops the child and everything it started.
    ///
    /// <para>DISPOSING IS NOT ENOUGH: the backend closes the master fd and waits half a second,
    /// which hangs up an interactive shell but leaves anything it started alive. The whole tree
    /// goes, because a shell's child is what is usually worth killing.</para>
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private static void Kill(TerminalControl terminal)
    {
        try { Process.GetProcessById(terminal.ProcessId).Kill(entireProcessTree: true); }
        catch { }
    }
}
