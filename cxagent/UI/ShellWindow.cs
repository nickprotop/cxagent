using System.Diagnostics;
using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Dialogs;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Themes;
using SharpConsoleUI.Core;

namespace CxAgent.UI;

/// <summary>
/// A real terminal, in a window, for the commands a tool cannot run.
///
/// <para>WHY THIS EXISTS AT ALL: <c>run_shell</c> runs a command and reads what it printed, which
/// covers everything except what needs a person — a sudo password, <c>gcloud auth login</c>,
/// <c>git rebase -i</c>, an installer that paints a screen. There is nothing to type into behind a
/// captured stream. Here the user is at the keyboard and the agent is told what happened.</para>
///
/// <para>NOT A TAKEOVER. The comparable feature elsewhere suspends the whole interface and hands the
/// raw terminal to the child; cxagent has a window system, so the session stays on screen behind a
/// window that can be closed.</para>
/// </summary>
internal static class ShellWindow
{
    /// <summary>
    /// Whether a terminal can be opened here at all.
    ///
    /// <para>LINUX AND WINDOWS ONLY, because <c>TerminalControl</c> throws
    /// <c>PlatformNotSupportedException</c> on macOS. The command is not registered where this is
    /// false, and since the system prompt renders only what was registered, the model never hears of
    /// a command that could only fail.</para>
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    /// <summary>
    /// The window that is open, or null.
    ///
    /// <para>ONE AT A TIME. Two transcripts would land in an order nobody controls, and the feature's
    /// whole shape is one command and one outcome. A second /shell is refused rather than queued —
    /// queueing would open a window later, attached to a moment that has passed.</para>
    /// </summary>
    private static Window? _open;

    /// <summary>
    /// The confirmation currently on screen, or null.
    ///
    /// <para>ONE QUESTION AT A TIME. <c>OnClosing</c> fires on EVERY close attempt — the title-bar
    /// X, the toolbar button, a forced close — and each one that finds a running command would open
    /// its own dialog. Clicking Stop on the second closes the second; the first is still there,
    /// still live, still asking, which reads as a dialog that refuses to close.</para>
    /// </summary>
    private static Window? _asking;

    /// <summary>
    /// Opens a terminal running <paramref name="command"/>, or an interactive shell when it is empty.
    /// </summary>
    public static void Open(ConsoleWindowSystem system, MainWindow main, Session session,
                            string command)
    {
        if (_open is not null)
        {
            ChatTranscriptSink.Post(main.Chat, ChatTranscriptSink.Row(new Message(
                "A terminal is already open. Close it before opening another.", Severity.Warning)));
            return;
        }

        var child = ShellCommandLine.For(command);
        var interactive = string.IsNullOrWhiteSpace(command);

        var terminal = new TerminalBuilder()
            .WithExe(child.Exe)
            .WithArgs(child.Args)
            .WithWorkingDirectory(session.WorkingDirectory)
            // THE LAST SCREEN IS THE RESULT. Closing on exit would destroy what the user opened the
            // window to read, and race their eye to do it.
            .KeepOpenOnExit()
            .Build();

        // SEND-BACK IS THE DEFAULT ONLY FOR A COMMAND. A bare /shell is a convenience for the user,
        // not a channel to the model: there is no scoped command whose outcome is worth reporting,
        // and a session's whole shell history is not something to post uninvited.
        var sendBack = new CheckboxBuilder()
            .WithLabel("send output back")
            .Checked(!interactive)
            .Build();

        var status = Controls.Label(interactive ? "  shell  " : "  running…  ");
        var close = new ButtonBuilder().WithText(" Close ").Build();

        // PINNED TO THE TOP, above a rule that separates it from the terminal. Left to scroll with
        // the content it would be pushed out of view by the first screenful of output — and the
        // send-back toggle is worthless if it cannot be reached once there is output worth deciding
        // about. Above rather than below because the controls belong to the WINDOW, not to the
        // output: a toolbar under a stream of text reads as the last line the command printed.
        var toolbar = new HorizontalGridBuilder()
            .Column(c => c.Add(sendBack))
            .Column(c => c.Add(status))
            .Column(c => c.Add(close))
            .Build();
        toolbar.StickyPosition = StickyPosition.Top;

        // THE RULE CARRIES THE COMMAND, which is the only place on screen that still says what was
        // run once output has scrolled: the title bar is truncated by the window chrome and the
        // command's own echo is gone with the first screenful. A bare shell has no command, so it
        // gets a plain separator rather than a label saying nothing.
        //
        // ESCAPED, because the title is parsed as markup and a command line is full of brackets —
        // `ls [a-z]*` would lose "[a-z]" to a colour tag that does not exist, or throw.
        //
        // STICKY LIKE THE TOOLBAR: a separator that scrolled away would leave the controls floating
        // on top of the output.
        var rule = interactive
            ? Controls.Separator()
            : Controls.Rule($"[{ColorScheme.MutedMarkup}]{MarkupParser.Escape(command.Trim())}[/]");
        rule.TitleAlignment = TextJustification.Left;
        rule.StickyPosition = StickyPosition.Top;

        var window = new WindowBuilder(system)
            .WithTitle(interactive ? "  Terminal" : $"  Terminal — {command}")
            .WithSize(Math.Max(system.DesktopDimensions.Width - 6, 60),
                      Math.Max(system.DesktopDimensions.Height - 6, 20))
            .Centered()
            .Closable(true)
            // THE THEME'S ACCENT, NOT THE LIBRARY'S. A window that sets no border colour falls
            // through to the theme default — Cyan1 in ModernGrayTheme — which is a bright cyan
            // against cxagent's amber chrome and reads as a window from another application.
            //
            // Inactive stays muted so the terminal recedes when the session has focus, which is the
            // same distinction the border already draws; only the colours are now ours.
            .WithActiveBorderColor(ColorScheme.AccentRgb)
            .WithInactiveBorderColor(ColorScheme.Separator)
            .WithBackgroundColor(ColorScheme.ChatSurface)
            // NOT MINIMIZABLE. A minimised terminal is a running child with no way back to it that
            // the user is likely to find, and the one-window-at-a-time rule then refuses the next
            // /shell because of a window they cannot see. Closing is the way out, and it asks first
            // when something is still running.
            .Minimizable(false)
            .AddControl(toolbar)
            .AddControl(rule)
            .AddControl(terminal)
            .Build();

        // BUILT IS NOT SHOWN. WindowBuilder.Build constructs a window; the window system does not
        // know it exists until it is added, so without this the PTY spawns, the child runs and
        // nothing is ever drawn — a terminal that works perfectly and is invisible.
        //
        // ACTIVATED, which AddWindow does by default: the user opened this to TYPE INTO it, and a
        // terminal that appears behind the session window would take their first keystrokes
        // somewhere else.
        system.AddWindow(window);

        // THE TERMINAL TAKES THE FOCUS, not the toolbar. The window opens with three focusable
        // things in it and the first one wins by default — so without this the user's first
        // keystrokes go to the send-back checkbox instead of the shell they opened this to type
        // into, and a space bar toggles the transcript off rather than reaching the command.
        //
        // AFTER AddWindow, because the focus manager belongs to a window the system knows about.
        window.FocusManager.SetFocus(terminal, FocusReason.Programmatic);

        _open = window;

        // THE BUTTON AND THE WINDOW'S OWN X ARE ONE PATH. Both raise OnClosing, so the confirmation
        // below cannot be skipped by whichever one a user happens to reach for.
        close.Click += (_, _) => window.Close();

        window.OnClosing += (_, e) =>
        {
            // THE QUESTION IS ABOUT LOSING WORK, NOT ABOUT A LIVE PROCESS. Three cases, and only one
            // of them is worth interrupting someone for:
            //
            // - a command that has finished: the shell exited with it, so there is nothing to stop
            // - a BARE /shell: a prompt is waiting, which is "running" only in the sense that bash is
            //   alive. Nothing is in flight, nobody typed anything that could be half-done, and
            //   asking teaches the reflexive approval a confirmation is supposed to be worth reading
            // - a command still going: an apt install killed halfway is a real loss, and this is the
            //   case the confirmation exists for
            //
            // A FORCED CLOSE CANNOT BE CANCELLED: ignoring Force would hang shutdown behind a
            // question nobody can answer.
            if (interactive || terminal.IsDisposed || e.Force) return;

            // ALREADY ASKING. A second close attempt while the question is up must not stack a
            // second copy of it — it must simply be refused, leaving the one the user is looking at
            // to decide the outcome.
            e.Allow = false;
            if (_asking is null) AskThenClose(system, window, terminal, command);
        };

        window.OnClosed += (_, _) =>
        {
            // KILLED WHETHER OR NOT WE ASKED. A bare /shell closes without a question, but bash is
            // still there: closing the master fd hangs up an interactive shell in the usual case and
            // leaves anything it started behind, so the tree goes explicitly.
            if (!terminal.IsDisposed) Kill(terminal);
            _open = null;
            _asking = null;
        };

        terminal.ProcessExited += (_, _) => system.EnqueueOnUIThread(() =>
        {
            // READ AFTER DISPOSAL, WHICH IS WHY THIS IS SAFE: the control disposes before raising,
            // and disposal is where the wait that produces ExitCode happens.
            status.SetContent([terminal.ExitCode is { } c ? $"  exited {c}  " : "  exited  "]);
            Report(session, terminal, command, sendBack.Checked);

            // TYPING `exit` IS ASKING TO LEAVE, so a bare shell closes its window — keeping it would
            // ignore an explicit instruction and leave a dead terminal to dismiss by hand.
            //
            // A COMMAND'S WINDOW STAYS, which is the opposite case rather than an inconsistency: the
            // user never asked to leave, they asked to see a result, and the last screen IS that
            // result. Closing would destroy it and race their eye to do it.
            //
            // AFTER Report, so the transcript is collected before the window goes.
            if (interactive) window.Close(force: true);
        });
    }

    /// <summary>
    /// Asks before killing a command that is still running, then closes.
    ///
    /// <para>A CONFIRMATION IS CHEAP AND AN ACCIDENTAL CLOSE MID-INSTALL IS NOT. The question names
    /// the command, because a window that has scrolled is not self-evidently the one the user
    /// started.</para>
    ///
    /// <para>BUILT HERE RATHER THAN THROUGH <c>Dialogs.ConfirmAsync</c>, following what
    /// <see cref="PluginManagerDialog"/> and cratis-cli's workbench both do. The shared helper owns
    /// a window lifecycle this caller does not control: it resolves through a TaskCompletionSource
    /// and closes its own modal on a schedule anything else closing a window has to race. Two
    /// buttons whose handlers do the work directly have no ordering to get wrong.</para>
    ///
    /// <para>THE DIALOG CLOSES BEFORE THE ACTION RUNS, which is the rule that makes this correct
    /// rather than merely simpler — the same contract cratis names <c>CloseOnAction</c>. Acting
    /// first means closing the terminal window while its modal is still up, and a modal whose
    /// window goes out from under it stays on screen needing a second dismissal.</para>
    ///
    /// <para>APP-MODAL, NOT PARENTED, for the same reason.</para>
    /// </summary>
    private static void AskThenClose(ConsoleWindowSystem system, Window window,
                                     TerminalControl terminal, string command)
    {
        var what = string.IsNullOrWhiteSpace(command) ? "The shell" : command.Trim();

        Window? dialog = null;
        void Close()
        {
            // CLEARED FIRST, so a close attempt arriving while this runs is not refused by a guard
            // pointing at a window that is already going.
            _asking = null;
            if (dialog is not null) system.CloseWindow(dialog, activateParent: true, force: true);
        }

        // ESCAPED: the command is the user's own text in a markup-rendered control, and a bracket
        // in it would be eaten as a colour tag.
        var body = Controls.Markup()
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.CautionMarkup}]{MarkupParser.Escape(what)}[/] is still running.")
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.MutedMarkup}]Stopping it ends the command and closes the terminal.[/]")
            .AddEmptyLine()
            .Build();

        var rule = Controls.RuleBuilder()
            .WithColorRole(ColorScheme.Caution)
            .WithBorderStyle(BorderStyle.Single)
            .StickyBottom()
            .Build();

        var toolbar = Controls.Toolbar()
            .WithSpacing(2)
            .WithAlignment(HorizontalAlignment.Center)
            .AddButton(new ButtonBuilder()
                .WithText("Stop it")
                .WithColorRole(ColorScheme.Destructive)
                .OnClick((_, _) => { Close(); Kill(terminal); window.Close(force: true); }))
            .AddButton(new ButtonBuilder()
                .WithText("Keep running")
                // AFFIRMATIVE. This is the safe answer — the command keeps working — so it reads
                // as the good outcome beside a red Stop rather than as inert chrome.
                .WithColorRole(ColorScheme.Affirmative)
                // BACK TO TYPING. Dismissing returns focus to the window, not to the control the
                // user was working in — so a password prompt they nearly abandoned would sit there
                // ignoring the keyboard.
                .OnClick((_, _) =>
                {
                    Close();
                    window.FocusManager.SetFocus(terminal, FocusReason.Programmatic);
                }));

        dialog = new WindowBuilder(system)
            .WithTitle(" Still running ")
            .WithSize(64, 11)
            .Centered()
            .AsModal()
            .WithBackgroundColor(ColorScheme.ChatSurface)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(ColorScheme.AccentRgb)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            // ESCAPE CANCELS, never stops. The safe answer is the one a reflex reaches, so a key
            // pressed to dismiss a surprise cannot kill a running install.
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;

                Close();
                window.FocusManager.SetFocus(terminal, FocusReason.Programmatic);
                e.Handled = true;
            })
            .AddControl(body)
            .AddControl(rule)
            .AddControl(toolbar.StickyBottom().Build())
            .Build();

        _asking = dialog;
        system.AddWindow(dialog);
    }

    /// <summary>
    /// Stops the child, and everything it started.
    ///
    /// <para>DISPOSING THE CONTROL IS NOT ENOUGH. The backend closes the master fd and waits half a
    /// second; that hangs up an interactive shell, but <c>sleep 30</c> or a running <c>apt
    /// install</c> is not reading the terminal and does not notice it has gone — leaving an orphan
    /// still holding the working directory.</para>
    ///
    /// <para>THE WHOLE TREE, because <c>$SHELL -c "apt install foo"</c> means the process worth
    /// killing is the shell's CHILD; killing only the shell leaves the install running.</para>
    ///
    /// <para>SWALLOWED, because the child may have exited between the question and the answer, and
    /// a race that the user resolved by waiting is not an error to report.</para>
    /// </summary>
    private static void Kill(TerminalControl terminal)
    {
        try { Process.GetProcessById(terminal.ProcessId).Kill(entireProcessTree: true); }
        catch { }
    }

    /// <summary>
    /// Gives the model the transcript, if the user is sending it.
    ///
    /// <para>INJECT, NEVER SUBMIT. This starts no turn: the person who just closed a terminal may be
    /// reading it, thinking, or gone, and an agent that begins talking into that — or begins fixing
    /// a failure nobody asked it to fix — is worse than one that waits. The model sees it when they
    /// next say something.</para>
    /// </summary>
    private static void Report(Session session, TerminalControl terminal, string command, bool send)
    {
        if (!send) return;

        session.Inject(ShellTranscript.Render(
            new ShellOutcome(command, terminal.GetTranscript(), terminal.ExitCode)));
    }
}
