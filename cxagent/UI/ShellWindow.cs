using System.Diagnostics;
using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Controls.Terminal;
using SharpConsoleUI.Dialogs;
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

        // PINNED TO THE BOTTOM so the terminal above it takes the remaining height. Left to scroll
        // with the content, the toolbar is pushed out of view by the first screenful of output —
        // and the send-back toggle is worthless if it cannot be reached once there is output to
        // decide about.
        var toolbar = new HorizontalGridBuilder()
            .Column(c => c.Add(sendBack))
            .Column(c => c.Add(status))
            .Column(c => c.Add(close))
            .Build();
        toolbar.StickyPosition = StickyPosition.Bottom;

        var window = new WindowBuilder(system)
            .WithTitle(interactive ? "  Terminal" : $"  Terminal — {command}")
            .WithSize(Math.Max(system.DesktopDimensions.Width - 6, 60),
                      Math.Max(system.DesktopDimensions.Height - 6, 20))
            .Centered()
            .Closable(true)
            .AddControl(terminal)
            .AddControl(toolbar)
            .Build();

        // BUILT IS NOT SHOWN. WindowBuilder.Build constructs a window; the window system does not
        // know it exists until it is added, so without this the PTY spawns, the child runs and
        // nothing is ever drawn — a terminal that works perfectly and is invisible.
        //
        // ACTIVATED, which AddWindow does by default: the user opened this to TYPE INTO it, and a
        // terminal that appears behind the session window would take their first keystrokes
        // somewhere else.
        system.AddWindow(window);

        _open = window;

        // THE BUTTON AND THE WINDOW'S OWN X ARE ONE PATH. Both raise OnClosing, so the confirmation
        // below cannot be skipped by whichever one a user happens to reach for.
        close.Click += (_, _) => window.Close();

        window.OnClosing += (_, e) =>
        {
            // NOTHING RUNNING MEANS NOTHING TO ASK. After `-c` finishes the shell has already exited,
            // so closing is unconditional and the real exit code is what gets reported.
            if (terminal.IsDisposed || e.Force) return;

            // A FORCED CLOSE CANNOT BE CANCELLED and is handled above: ignoring Force here would hang
            // shutdown behind a question nobody can answer.
            e.Allow = false;
            AskThenClose(system, window, terminal, command);
        };

        window.OnClosed += (_, _) => _open = null;

        terminal.ProcessExited += (_, _) => system.EnqueueOnUIThread(() =>
        {
            // READ AFTER DISPOSAL, WHICH IS WHY THIS IS SAFE: the control disposes before raising,
            // and disposal is where the wait that produces ExitCode happens.
            status.SetContent([terminal.ExitCode is { } c ? $"  exited {c}  " : "  exited  "]);
            Report(session, terminal, command, sendBack.Checked);
        });
    }

    /// <summary>
    /// Asks before killing a command that is still running, then closes.
    ///
    /// <para>A CONFIRMATION IS CHEAP AND AN ACCIDENTAL CLOSE MID-INSTALL IS NOT. The question names
    /// the command, because a window that has scrolled is not self-evidently the one the user
    /// started.</para>
    /// </summary>
    private static void AskThenClose(ConsoleWindowSystem system, Window window,
                                     TerminalControl terminal, string command)
    {
        var what = string.IsNullOrWhiteSpace(command) ? "The shell" : $"`{command}`";

        _ = Ask();

        async Task Ask()
        {
            if (!await Dialogs.ConfirmAsync(system, "Still running",
                    $"{what} is still running. Stop it?", ok: "Stop it", cancel: "Keep running",
                    severity: NotificationSeverityEnum.Warning, parent: window))
                return;

            Kill(terminal);
            window.Close(force: true);
        }
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
