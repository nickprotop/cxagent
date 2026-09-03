using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Highlighting;
using SharpConsoleUI.Layout;
using SharpConsoleUI.Parsing;
using SharpConsoleUI.Themes;

namespace CxAgent.UI;

/// <summary>
/// What a file tab needs from the app around it.
///
/// <para>A RECORD RATHER THAN THREE PARAMETERS. <c>ShellTab.Open</c> takes exactly these three
/// (<c>:55</c>) and adding the file makes four, which is where a group wants a name. Every entry
/// point here takes the host, so the list cannot grow again one reasonable argument at a time.</para>
/// </summary>
public sealed record EditorHost(ConsoleWindowSystem System, MainWindow Main, Session Session);

/// <summary>
/// A file that lives in a tab: readable, editable, and saved back with the conventions it arrived
/// with.
///
/// <para>WHY A TAB RATHER THAN A PAGER. Leaving to read a file leaves the model believing something
/// about a file you may then change. A tab that saves through the app tells the model it happened —
/// the stale-read hazard is removed rather than made faster to walk into.</para>
/// </summary>
public static class FileTab
{
    /// <summary>
    /// The open files and their tabs, per main window.
    ///
    /// <para>KEYED ON THE WINDOW, NOT GLOBAL. These hold live controls belonging to one screen, and a
    /// static dictionary would make a second window inherit the first's idea of what is open — every
    /// tab title colliding with a tab it cannot see. A counter can be global because a number is not
    /// attached to anything; a control is.</para>
    ///
    /// <para>A CONDITIONAL TABLE so a window that is closed takes its entry with it rather than
    /// pinning every editor it ever opened.</para>
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MainWindow, Workspace>
        Workspaces = new();

    /// <summary>One window's open files.</summary>
    private sealed class Workspace
    {
        public OpenFiles Open { get; } = new();
        public Dictionary<string, TabState> States { get; } = new(StringComparer.Ordinal);

        /// <summary>Started with the first open file, and shared by every tab after it.</summary>
        public OpenFileWatcher? Watcher { get; set; }

        /// <summary>The question on this window's screen: how to take it back, and its answers.</summary>
        public (string Title, IReadOnlyList<Choice> Choices, Action Dismiss)? OpenQuestion { get; set; }
    }

    private static Workspace For(MainWindow main) => Workspaces.GetOrCreateValue(main);

    /// <summary>
    /// Suppresses the watcher while we are the ones writing.
    ///
    /// <para>WITHOUT IT EVERY SAVE ANNOUNCES ITSELF BACK as an external change, and the tab reports
    /// the user's own edit as the agent's.</para>
    ///
    /// <para>PUBLIC because this assembly grants no InternalsVisibleTo and the watcher's tests set
    /// it; see ColorScheme.cs:184 for the convention.</para>
    /// </summary>
    public static bool SuppressWatch { get; set; }

    /// <summary>What one open file's tab is holding.</summary>
    private sealed class TabState
    {
        public required string Path { get; init; }
        public required MultilineEditControl Editor { get; init; }
        public required MarkupControl Status { get; init; }
        public required ButtonControl Reload { get; init; }
        public required ButtonControl SeeTheirs { get; init; }
        public required LoadedFile File { get; set; }

        /// <summary>The text as it was last read or written, for deciding "modified".</summary>
        public required string Baseline { get; set; }

        /// <summary>Something changed the file under us and the tab has not caught up.</summary>
        public bool ExternallyChanged { get; set; }

        /// <summary>The file is gone from disk; the buffer is all that is left of it.</summary>
        public bool Deleted { get; set; }

        /// <summary>One question at a time. See RequestClose.</summary>
        public bool Asking { get; set; }

        /// <summary>The read-only on-disk view, when one is open beside this tab.</summary>
        public MultilineEditControl? TheirsView { get; set; }

        public bool IsModified => !string.Equals(Editor.GetContent(), Baseline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opens a file in a tab, or switches to the tab it already has.
    ///
    /// <para>SWITCHES RATHER THAN DUPLICATES. Two buffers over one file is the single state a save
    /// cannot reconcile — whichever wrote last would silently discard the other.</para>
    /// </summary>
    public static void Open(EditorHost host, LoadedFile file)
    {
        if (For(host.Main).Open.TryGetTitle(file.Path, out var existing))
        {
            Show(host.Main, existing);
            return;
        }

        var title = For(host.Main).Open.Add(file.Path);

        // NoWrap: wrapped code stops line numbers corresponding to lines, and it is the only mode
        // with a horizontal scrollbar at all.
        var editorBuilder = new MultilineEditControlBuilder()
            .WithContent(file.Text)
            .WithWrapMode(WrapMode.NoWrap)
            .WithLineNumbers()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);

        // A language TextMate has no grammar for is not an error — the file opens as plain text.
        if (SyntaxHighlighters.For(file.Language) is { } highlighter)
            editorBuilder = editorBuilder.WithSyntaxHighlighter(highlighter);

        // THE SAME GROUND AS EVERYTHING ELSE, and the same when focused as when not. The control
        // defaults to a colour of its own and paints the whole cell with it, which reads as a slab
        // announcing itself rather than a document in the app's window — and the default focus colour
        // makes the tab change shade as focus moves, drawing the eye to a change that means nothing.
        // ChatSurface is the theme's own window background, so the editor follows the active theme
        // without deciding anything itself.
        editorBuilder = editorBuilder
            .IsEditing()
            .WithEscapeExitsEditMode(false)
            .WithColors(ColorScheme.Code, ColorScheme.ChatSurface)
            .WithFocusedColors(ColorScheme.Code, ColorScheme.ChatSurface);

        var editor = editorBuilder.Build();

        // ALWAYS IN EDIT MODE. A buffer you must first activate before it takes a keystroke is a mode
        // nobody asked for and nothing on screen shows; the composer carries the same rule, and
        // "typing silently dies" was how it was found. Escape cannot knock it back out either — it is
        // a global shortcut (AppBootstrap.cs:1515), consulted before the focused control, so it never
        // reaches here to be interpreted as "leave edit mode".

        var status = new MarkupControl(new List<string> { string.Empty })
        {
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var save = new ButtonBuilder()
            .WithText(" Save ")
            .WithColorRole(ColorScheme.Accent)
            .OnClick((_, _) => RequestSave(host, title))
            .Build();

        // BUILT NOW, HIDDEN UNTIL THERE IS SOMETHING TO SEE. These answer the warning the watcher
        // raises; on a clean file they would be two controls that do nothing, and a row of those
        // teaches people to stop reading it.
        var reload = new ButtonBuilder()
            .WithText(" Reload ")
            .OnClick((_, _) => RequestReload(host, title))
            .Build();
        reload.Visible = false;

        var seeTheirs = new ButtonBuilder()
            .WithText(" See theirs ")
            .OnClick((_, _) => ShowTheirs(host, title))
            .Build();
        seeTheirs.Visible = false;

        var bar = new ToolbarControl
        {
            ItemSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        bar.AddItem(save);
        bar.AddItem(reload);
        bar.AddItem(seeTheirs);
        bar.AddItem(status);

        var content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1), GridLength.Star(1))
            .Place(bar, 0, 0)
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 0)
            .Place(editor, 2, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        For(host.Main).States[title] = new TabState
        {
            Path = file.Path,
            Editor = editor,
            Status = status,
            Reload = reload,
            SeeTheirs = seeTheirs,
            File = file,
            Baseline = file.Text,
        };

        // ADVISORY, NOT A VETO. The event does not remove the tab — this handler does — so the
        // question below is a plain one with no ordering to get wrong.
        //
        // THE TITLE IDENTIFIES THE TAB, because the event carries every closable tab in the control
        // and each open file subscribes its own handler.
        void OnCloseRequested(object? _, TabEventArgs e)
        {
            // MATCHED ON THE TAB, NOT ITS TEXT. Refresh rewrites the title to carry the markers — a
            // modified file is "name •" and one changed underneath is "⚠ name •" — so comparing the
            // displayed string to the title this tab opened with stops matching the moment the file
            // is edited, and the close request then belongs to nobody.
            if (!ReferenceEquals(e.TabPage, TabOf(host.Main, title))) return;

            RequestClose(host, title, () => host.Main.Tabs.TabCloseRequested -= OnCloseRequested);
        }

        host.Main.Tabs.TabCloseRequested += OnCloseRequested;

        // THE MARKER FOLLOWS THE BUFFER. Nothing else recomputes it: Refresh runs on open and on a
        // watcher event, so without this the tab says "3 lines" with no bullet while the user is
        // typing into it — and the close confirmation, which reads the same state, would let edits go
        // without asking.
        // THE MARKER FOLLOWS THE BUFFER. Nothing else recomputes it: Refresh runs on open and on a
        // watcher event, so without this the tab says "2 lines" with no bullet while the user is
        // typing into it — and the close confirmation, which reads the same state, would let edits go
        // without asking.
        //
        // GUARDED ON A REAL CHANGE. The control raises ContentChanged while it is still being set up,
        // before the baseline this compares against is stored, so an unguarded handler marks every
        // file modified the moment it opens — a bullet on an untouched file, and a close confirmation
        // for edits nobody made.
        // THE MARKER FOLLOWS THE BUFFER. Nothing else recomputes it: Refresh runs on open and on a
        // watcher event, so without this the tab says "2 lines" with no bullet while the user is
        // typing into it — and the close confirmation, which reads the same state, would let edits go
        // without asking.
        editor.ContentChanged += (_, _) => Refresh(host.Main, title);

        StartWatching(host);

        host.Main.AddTab(title, content);
        Refresh(host.Main, title);
        Show(host.Main, title);
        host.Main.Window?.FocusManager.SetFocus(editor, FocusReason.Programmatic);
    }

    /// <summary>
    /// Opens a tab saying why a file is not shown.
    ///
    /// <para>A TAB RATHER THAN A TRANSCRIPT LINE, because that is where the user just asked to look:
    /// a refusal printed into the conversation is read after they have already switched away.</para>
    /// </summary>
    public static void ShowRefusal(EditorHost host, string path, string refusal)
    {
        // ALREADY SHOWING WHY? Switch to it. Add returns the title a known path already has, so
        // without this a second ask builds a second tab of the same name — and TabControl addresses
        // tabs by title, which makes the pair indistinguishable to everything that looks one up.
        if (For(host.Main).Open.TryGetTitle(path, out var shown))
        {
            Show(host.Main, shown);
            return;
        }

        var title = For(host.Main).Open.Add(path);

        var content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Star(1))
            .Place(new MarkupControl(new List<string> { refusal })
            {
                HorizontalAlignment = HorizontalAlignment.Center,
            }, 0, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        host.Main.AddTab(title, content);
        Show(host.Main, title);
    }

    /// <summary>Brings a tab to the front by title.</summary>
    private static void Show(MainWindow main, string title)
    {
        if (Find(main, title) is { } index) main.Tabs.ActiveTabIndex = index;
    }

    /// <summary>
    /// A tab's index, or null when it has already gone.
    ///
    /// <para>SEARCHES THE DISPLAYED TITLE FOR THE REGISTRY'S ONE, because Refresh decorates it with
    /// markers: an open file's tab reads "⚠ name •" while the registry still calls it "name".</para>
    /// </summary>
    private static int? Find(MainWindow main, string title)
    {
        for (var i = 0; i < main.Tabs.TabCount; i++)
            if (main.Tabs.GetTab(i)?.Title is { } shown && Undecorate(shown) == title) return i;

        return null;
    }

    /// <summary>The tab itself, for identity comparisons.</summary>
    private static TabPage? TabOf(MainWindow main, string title)
        => Find(main, title) is { } index ? main.Tabs.GetTab(index) : null;

    /// <summary>Strips the markers Refresh adds, leaving the registry's title.</summary>
    private static string Undecorate(string shown)
        => shown.TrimStart('⚠', ' ').TrimEnd(' ', '•');

    /// <summary>
    /// Repaints the toolbar and the tab title from the state.
    ///
    /// <para>ONE PLACE THAT DECIDES, so the marker on the title and the words in the toolbar cannot
    /// disagree about what is true of the file.</para>
    /// </summary>
    private static void Refresh(MainWindow main, string title)
    {
        if (!For(main).States.TryGetValue(title, out var state)) return;

        var modified = state.IsModified;

        state.Reload.Visible = state.ExternallyChanged && !state.Deleted;
        state.SeeTheirs.Visible = state.ExternallyChanged && !state.Deleted;

        var lines = LineCountForTest(state.Editor.GetContent());
        var language = state.File.Language ?? "text";

        var text = state.Deleted
            ? "deleted on disk — Save recreates it"
            : state.ExternallyChanged
                ? "changed on disk — reload to see it"
                : $"{language} · {lines} lines" + (modified ? " · modified" : string.Empty);

        state.Status.SetContent(new List<string> { $"[{ColorScheme.MutedMarkup}]{text}[/]" });

        // THE MARKERS COMPOSE. A file the user edited and the agent then changed carries both, and
        // the title is where they go — the toolbar says what happened, the title says it is still
        // true from wherever you are looking.
        if (Find(main, title) is { } index && main.Tabs.GetTab(index) is { } tab)
        {
            var warn = state.ExternallyChanged || state.Deleted ? "⚠ " : string.Empty;
            tab.Title = warn + title + (modified ? " •" : string.Empty);
        }
    }

    /// <summary>
    /// The message a save sends to the model.
    ///
    /// <para>THE PATH AND NOTHING ELSE. A diff would be more useful and more tokens; the model can
    /// read the file if it cares, and it is the one thing here that already knows how.</para>
    ///
    /// <para>RELATIVE WHERE IT CAN BE, so the model sees the paths it uses in its own tool calls
    /// rather than a machine-specific absolute one it cannot match against anything.</para>
    /// </summary>
    public static string SaveMessage(string path, string workingDirectory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(workingDirectory);

        var shown = full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, full)
            : full;

        return $"[cxagent] the user edited {shown}";
    }

    /// <summary>
    /// Whether a save has to ask before it writes.
    ///
    /// <para>THE MIRROR OF THE WATCHER'S RULE. A modified buffer is never overwritten by a program,
    /// and equally a program's file is never silently overwritten by a stale buffer: the agent wrote
    /// at turn 15, the user was mid-edit so their buffer survived under a warning, and their next
    /// save would quietly discard what the agent wrote.</para>
    ///
    /// <para>THE EXCEPTION, NOT A STEP ON EVERY WRITE. A confirmation on every save trains the user
    /// to dismiss it, which is how the one that matters gets clicked through.</para>
    /// </summary>
    public static bool SaveNeedsConfirmationForTest(bool externallyChanged) => externallyChanged;

    /// <summary>
    /// Closes the tab, asking first when that would discard edits.
    ///
    /// <para>A CLEAN BUFFER CLOSES WITHOUT A QUESTION — nothing is lost, and a dialog that appears
    /// when nothing is at stake is what teaches people to dismiss them unread.</para>
    /// </summary>
    private static void RequestClose(EditorHost host, string title, Action unsubscribe)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;

        if (!state.IsModified)
        {
            unsubscribe();
            Close(host.Main, title);
            return;
        }

        Ask(host, title,
            $"{title} has unsaved changes.",
            "Closing it discards them.",
            [
                // SAVE WRITES, INJECTS, CLOSES — through the save path, so its own gate still applies.
                new Choice("Save", ColorScheme.Affirmative, () =>
                {
                    RequestSave(host, title);
                    unsubscribe();
                    Close(host.Main, title);
                }),
                // DISCARD INJECTS NOTHING: nothing happened to the file.
                new Choice("Discard", ColorScheme.Destructive, () =>
                {
                    unsubscribe();
                    Close(host.Main, title);
                }),
                new Choice("Cancel", ColorScheme.Accent, () => { }),
            ]);
    }

    /// <summary>Removes the tab and forgets the file, landing the user back in the conversation.</summary>
    private static void Close(MainWindow main, string title)
    {
        var workspace = For(main);
        if (workspace.States.Remove(title, out var state)) workspace.Open.Remove(state.Path);
        if (Find(main, title) is { } index) main.CloseTab(index);

        main.ShowChatTab();
    }

    /// <summary>Test seam: asks the close question as a close request would.</summary>
    public static void RequestCloseForTest(EditorHost host, string title)
        => RequestClose(host, title, () => { });

    /// <summary>Test seam: asks the save question as the toolbar button would.</summary>
    public static void RequestSaveForTest(EditorHost host, string title)
    {
        if (For(host.Main).States.TryGetValue(title, out var state)
            && SaveNeedsConfirmationForTest(state.ExternallyChanged))
            Ask(host, title, "changed underneath", "saving overwrites it",
                [new Choice("Save anyway", ColorScheme.Destructive, () => { })]);
    }

    /// <summary>Test seam: marks a buffer edited without simulating keystrokes.</summary>
    public static void MarkModifiedForTest(EditorHost host, string title)
    {
        if (For(host.Main).States.TryGetValue(title, out var state))
            state.Editor.SetContent(state.Baseline + "edit\n");
    }

    /// <summary>Test seam: marks the file as changed under the tab.</summary>
    public static void MarkExternallyChangedForTest(EditorHost host, string title)
    {
        if (For(host.Main).States.TryGetValue(title, out var state)) state.ExternallyChanged = true;
    }

    /// <summary>Test seam: how many tabs are holding an open question.</summary>
    public static int PendingConfirmationsForTest(EditorHost host)
        => For(host.Main).States.Values.Count(s => s.Asking);

    /// <summary>Test seam: presses one of the open question's buttons by its label.</summary>
    public static void AnswerForTest(EditorHost host, string title, string label)
    {
        if (For(host.Main).OpenQuestion is not { } open || open.Title != title) return;
        if (open.Choices.FirstOrDefault(c => c.Label == label) is not { } choice) return;

        open.Dismiss();
        choice.Take();
    }

    /// <summary>Test seam: presses Reload as the toolbar button does.</summary>
    public static void RequestReloadForTest(EditorHost host, string title)
        => RequestReload(host, title);

    /// <summary>Test seam: opens the on-disk view as the button does.</summary>
    public static void ShowTheirsForTest(EditorHost host, string title) => ShowTheirs(host, title);

    /// <summary>Test seam: presses Save as the toolbar button does, gate included.</summary>
    public static void RequestSaveForRealTest(EditorHost host, string title)
        => RequestSave(host, title);

    /// <summary>Test seam: drives a save to completion, which RequestSave cannot be awaited for.</summary>
    public static Task SaveForTest(EditorHost host, string title) => SaveAsync(host, title);

    /// <summary>Test seam: types into a tab's editor without simulating keystrokes.</summary>
    public static void SetContentForTest(EditorHost host, string title, string content)
    {
        if (For(host.Main).States.TryGetValue(title, out var state)) state.Editor.SetContent(content);
    }

    /// <summary>
    /// Test seam: whether a tab's editor is in edit mode.
    ///
    /// <para>Public with a ForTest suffix because this assembly grants no InternalsVisibleTo — see
    /// ColorScheme.cs:184.</para>
    /// </summary>
    public static bool EditorIsEditingForTest(EditorHost host, string title)
        => For(host.Main).States.TryGetValue(title, out var state) && state.Editor.IsEditing;

    /// <summary>
    /// Writes the buffer, then tells the model it happened.
    ///
    /// <para>ASKS FIRST WHEN THE FILE CHANGED UNDER US — see SaveNeedsConfirmationForTest. The
    /// dialog belongs to the confirmation work; until it exists the safe branch is taken and nothing
    /// is written, because the failure this guards against is silent.</para>
    /// </summary>
    private static void RequestSave(EditorHost host, string title)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;

        // THE GATE, AND ONLY WHEN IT IS ARMED. A confirmation on every save trains the user to
        // dismiss it, which is how the one that matters gets clicked through.
        if (SaveNeedsConfirmationForTest(state.ExternallyChanged))
        {
            Ask(host, title,
                $"The agent changed {title} after you opened it.",
                "Saving writes your version over its changes.",
                [
                    new Choice("Save anyway", ColorScheme.Destructive, () => Write(host, title)),
                    // SEEING BEFORE CHOOSING. Asking someone to pick between two versions they cannot
                    // read is not really asking them.
                    new Choice("See theirs", ColorScheme.Accent, () => ShowTheirs(host, title)),
                    new Choice("Cancel", ColorScheme.Affirmative, () => { }),
                ]);
            return;
        }

        Write(host, title);
    }

    private static async void Write(EditorHost host, string title)
    {
        // EVERYTHING INSIDE THE TRY. An async void method's exception reaches no caller — it goes
        // straight to the thread pool and takes the app with it — so nothing here may throw past this
        // point, the injection and the repaint included.
        try
        {
            await SaveAsync(host, title);
        }
        catch (Exception ex)
        {
            if (For(host.Main).States.TryGetValue(title, out var s))
                s.Status.SetContent(new List<string> { $"[{ColorScheme.MutedMarkup}]{ex.Message}[/]" });
        }
    }

    private static async Task SaveAsync(EditorHost host, string title)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;

        var content = state.Editor.GetContent();

        // SUPPRESSED AROUND THE WRITE so the watcher does not report our own save back as somebody
        // else's change. Cleared in a finally: a throwing write that left it set would silence the
        // watcher for the rest of the session, and nothing would say why.
        SuppressWatch = true;
        try
        {
            await FileMutation.WriteAsync(state.Path, content, state.File.Snapshot,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // THE BUFFER STAYS MODIFIED and nothing is injected: the file did not change, so there is
            // nothing to tell the model about.
            state.Status.SetContent(new List<string> { $"[{ColorScheme.MutedMarkup}]{ex.Message}[/]" });
            return;
        }
        finally
        {
            SuppressWatch = false;
        }

        // THE SNAPSHOT MOVES WITH THE FILE. Written bytes are the new baseline for both "modified"
        // and the conventions the next save restores.
        state.Baseline = content;
        state.File = state.File with
        {
            Text = content,
            Snapshot = state.File.Snapshot with { Text = content, Existed = true },
        };
        state.Deleted = false;

        // THE WARNING IS SPENT. What was on disk has just been overwritten by this buffer, so there
        // is no longer another version to lose — leaving the flag set would gate the next save over a
        // conflict that no longer exists, and leave a warning on a tab that matches its file exactly.
        state.ExternallyChanged = false;

        Refresh(host.Main, title);
        host.Session.Inject(SaveMessage(state.Path, host.Session.WorkingDirectory));
    }
    /// <summary>
    /// Starts the workspace's watcher, once.
    ///
    /// <para>ON THE SESSION'S WORKING DIRECTORY, because that is where the agent edits — a watcher
    /// rooted at each open file's own folder would miss nothing but cost a handle per directory, and
    /// files outside the working directory are ones the agent is not going to touch.</para>
    /// </summary>
    private static void StartWatching(EditorHost host)
    {
        var workspace = For(host.Main);
        if (workspace.Watcher is not null) return;

        try
        {
            workspace.Watcher = new OpenFileWatcher(host.Session.WorkingDirectory, workspace.Open,
                path => OnChangedOffThread(host, path));
        }
        catch (Exception)
        {
            // A DIRECTORY THAT CANNOT BE WATCHED IS NOT A REASON TO REFUSE THE FILE. The editor still
            // works; what is lost is being told when the agent writes underneath it, and a tab that
            // opens is better than an exception where a file was asked for.
        }
    }

    /// <summary>
    /// A file changed under a tab. Arrives on a threadpool thread.
    ///
    /// <para>MARSHALLED BEFORE ANYTHING IS TOUCHED. Every control here belongs to the UI thread, and
    /// a watcher callback is the classic place that rule is broken because nothing about the call
    /// site says which thread it is on.</para>
    /// </summary>
    private static void OnChangedOffThread(EditorHost host, string path)
        => host.System.EnqueueOnUIThread(() => OnChanged(host, path));

    private static void OnChanged(EditorHost host, string path)
    {
        var workspace = For(host.Main);
        if (!workspace.Open.TryGetTitle(path, out var title)) return;
        if (!workspace.States.TryGetValue(title, out var state)) return;

        // GONE IS NOT A QUESTION. Nothing is lost at the moment it happens — the buffer simply is the
        // file now, and Save writes it back. A dialog raised by something the user did not do is an
        // interruption rather than a question.
        if (!File.Exists(state.Path))
        {
            state.Deleted = true;

            // NOT ExternallyChanged. That flag arms the save gate, which exists to stop a stale
            // buffer overwriting a version on disk — and there is no version on disk to protect.
            // Arming it here makes Save ask about a conflict that cannot exist, and the toolbar
            // promises "Save recreates it" while nothing happens.
            state.ExternallyChanged = false;

            Refresh(host.Main, title);
            return;
        }

        state.Deleted = false;

        // A CLEAN BUFFER SHOWING STALE CONTENT IS A LIE WITH NO COST TO FIXING, so it just catches up.
        // A modified one must never be overwritten by a program: the edits stay and the reload becomes
        // something the user asks for.
        if (!state.IsModified)
        {
            Reload(host, title);
            state.Status.SetContent(
                new List<string> { $"[{ColorScheme.MutedMarkup}]reloaded from disk[/]" });
            return;
        }

        state.ExternallyChanged = true;
        Refresh(host.Main, title);
    }

    /// <summary>
    /// How many lines a buffer has.
    ///
    /// <para>A TRAILING NEWLINE TERMINATES THE LAST LINE RATHER THAN STARTING ANOTHER. Splitting
    /// "a\nb\n" on newlines gives three pieces, the last empty — so every well-formed text file, which
    /// ends in a newline, would report one line more than it has.</para>
    /// </summary>
    public static int LineCountForTest(string content)
    {
        if (content.Length == 0) return 0;

        var trimmed = content.EndsWith('\n') ? content[..^1] : content;
        return trimmed.Split('\n').Length;
    }

    /// <summary>
    /// Re-colours every open editor after a theme switch.
    ///
    /// <para>THE COLOURS WERE CAPTURED BY VALUE when each editor was built, so without this a switch
    /// leaves every open file painted in the outgoing theme — the one surface still showing colours
    /// that are no longer active. The grips and the mode line in MainWindow.ReapplyTheme carry the
    /// same note for the same reason.</para>
    ///
    /// <para>THE STATUS LINE TOO, because its markup names a colour and markup cannot be re-coloured
    /// by assignment; Refresh regenerates the text.</para>
    /// </summary>
    public static void ReapplyTheme(MainWindow main)
    {
        foreach (var (title, state) in For(main).States)
        {
            Recolour(state.Editor);
            if (state.TheirsView is { } view) Recolour(view);

            Refresh(main, title);
        }

        static void Recolour(MultilineEditControl editor)
        {
            editor.BackgroundColor = ColorScheme.ChatSurface;
            editor.FocusedBackgroundColor = ColorScheme.ChatSurface;
            editor.ForegroundColor = ColorScheme.Code;
            editor.FocusedForegroundColor = ColorScheme.Code;
        }
    }

    /// <summary>
    /// Saves the file tab on screen, if the active tab is one.
    ///
    /// <para>A KEY, BECAUSE THE BUTTON IS UNREACHABLE FROM THE KEYBOARD. The editor consumes Tab as
    /// indent — which is right for an editor and is why F4 is the way back to the composer — so
    /// nothing moves focus from the buffer to the toolbar above it. Save is not a convenience like
    /// the shell tab's copy button; it is the reason the tab is editable, and a control you can only
    /// reach with a mouse is one a keyboard user does not have.</para>
    ///
    /// <para>A FUNCTION KEY, NOT CTRL+S. A terminal sends Ctrl+letter as a single control byte and
    /// Ctrl+S is XOFF on many of them, which stops the display rather than reaching the app — the
    /// same reasoning that put every other action in this app on a function key.</para>
    ///
    /// <para>A NO-OP ON ANY OTHER TAB, so the key means nothing where there is nothing to save
    /// rather than acting on a file the user is not looking at.</para>
    /// </summary>
    public static void SaveActiveTab(EditorHost host)
    {
        var index = host.Main.Tabs.ActiveTabIndex;
        if (host.Main.Tabs.GetTab(index)?.Title is not { } shown) return;

        var title = Undecorate(shown);
        if (!For(host.Main).States.ContainsKey(title)) return;

        RequestSave(host, title);
    }

    /// <summary>Test seam: an editor's painted background.</summary>
    public static SharpConsoleUI.Color? EditorBackgroundForTest(EditorHost host, string title)
        => For(host.Main).States.TryGetValue(title, out var state)
            ? state.Editor.BackgroundColor
            : null;

    /// <summary>Test seam: a tab's current buffer text.</summary>
    public static string? ContentForTest(EditorHost host, string title)
        => For(host.Main).States.TryGetValue(title, out var state) ? state.Editor.GetContent() : null;

    /// <summary>Test seam: whether the save gate is armed for a tab.</summary>
    public static bool ExternallyChangedForTest(EditorHost host, string title)
        => For(host.Main).States.TryGetValue(title, out var state) && state.ExternallyChanged;

    /// <summary>Test seam: raises a change as the watcher would, on this thread.</summary>
    public static void RaiseChangedForTest(EditorHost host, string path) => OnChanged(host, path);

    /// <summary>
    /// Closes the question on this window, if one is up.
    ///
    /// <para>ESCAPE IS A GLOBAL SHORTCUT, consulted before the active window
    /// (InputCoordinator.cs:130-134), so a dialog's own OnKeyPressed never sees the key — the
    /// plugin manager carries the same note and the same remedy. Without this, Escape on one of
    /// these questions falls through to the branches below it and cancels the running turn instead
    /// of dismissing what is on screen.</para>
    ///
    /// <para>PER WINDOW, NOT GLOBAL, for the reason the workspaces are: one screen shows one modal,
    /// but two windows are two screens — and a global would let one window's Escape dismiss the
    /// other's question. Test classes run in parallel and are exactly that shape.</para>
    /// </summary>

    /// <summary>
    /// Closes the question on screen, if there is one. Called from the global Escape handler before
    /// anything else it might mean.
    /// </summary>
    public static bool CloseQuestionIfOpen(MainWindow main)
    {
        if (For(main).OpenQuestion is not { } open) return false;

        open.Dismiss();
        return true;
    }

    /// <summary>One answer to one question, and what it does.</summary>
    private sealed record Choice(string Label, ColorRole Role, Action Take);

    /// <summary>
    /// Asks one question about one tab, and does what the answer says.
    ///
    /// <para>ONE DIALOG AT A TIME PER TAB. Close, save-over and reload are all raised from paths the
    /// watcher can fire during, and a close request arrives on every attempt — without the guard a
    /// second question stacks behind the first and looks exactly like a button that does nothing.
    /// That took four attempts to unpick in the shell window.</para>
    ///
    /// <para>APP-MODAL, NOT PARENTED to the tab it is about to change: a modal whose parent goes out
    /// from under it stays on screen needing a second dismissal.</para>
    /// </summary>
    private static void Ask(EditorHost host, string title, string headline, string detail,
                            IReadOnlyList<Choice> choices)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state) || state.Asking) return;
        state.Asking = true;

        Window? dialog = null;
        void Dismiss()
        {
            state.Asking = false;
            For(host.Main).OpenQuestion = null;
            if (dialog is not null)
                host.System.CloseWindow(dialog, activateParent: true, force: true);
        }

        For(host.Main).OpenQuestion = (title, choices, Dismiss);

        var body = Controls.Markup()
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.CautionMarkup}]{MarkupParser.Escape(headline)}[/]")
            .AddEmptyLine()
            .AddLine($"  [{ColorScheme.MutedMarkup}]{MarkupParser.Escape(detail)}[/]")
            .AddEmptyLine()
            .Build();

        var buttons = Controls.Toolbar()
            .WithSpacing(2)
            .WithAlignment(HorizontalAlignment.Center);

        foreach (var choice in choices)
        {
            var take = choice.Take;
            buttons = buttons.AddButton(new ButtonBuilder()
                .WithText(choice.Label)
                .WithColorRole(choice.Role)
                // THE DIALOG GOES FIRST, then the work: acting on a tab while its modal is up leaves
                // the modal on screen with nothing able to dismiss it.
                .OnClick((_, _) => { Dismiss(); take(); }));
        }

        dialog = new WindowBuilder(host.System)
            .WithTitle($" {title} ")
            .WithSize(64, 11)
            .Centered()
            .AsModal()
            .WithBackgroundColor(ColorScheme.ChatSurface)
            .WithBorderStyle(BorderStyle.Rounded)
            .WithBorderColor(ColorScheme.AccentRgb)
            .Resizable(false)
            .Minimizable(false)
            .Maximizable(false)
            // ESCAPE CANCELS. The safe answer is the one a reflex reaches, and every one of these
            // questions has "leave everything as it was" among its answers.
            .OnKeyPressed((_, e) =>
            {
                if (e.KeyInfo.Key != ConsoleKey.Escape) return;

                Dismiss();
                e.Handled = true;
            })
            .AddControl(body)
            .AddControl(buttons.StickyBottom().Build())
            .Build();

        host.System.AddWindow(dialog);
    }

    /// <summary>
    /// Re-reads the file, asking first when that would discard edits.
    ///
    /// <para>THE SAME LOSS AS CLOSING, so it asks the same way. Save first routes back through the
    /// save path INCLUDING its own gate, rather than writing by a side door that skips it.</para>
    /// </summary>
    private static void RequestReload(EditorHost host, string title)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;

        if (!state.IsModified)
        {
            Reload(host, title);
            return;
        }

        Ask(host, title,
            $"{title} has unsaved changes.",
            "Reloading replaces them with what is on disk.",
            [
                new Choice("Save first", ColorScheme.Affirmative, () => RequestSave(host, title)),
                new Choice("Discard", ColorScheme.Destructive, () => Reload(host, title)),
                new Choice("Cancel", ColorScheme.Accent, () => { }),
            ]);
    }

    /// <summary>Re-reads the file into the buffer, unconditionally.</summary>
    private static void Reload(EditorHost host, string title)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;
        if (FileLoad.TryLoad(state.Path, out _) is not { } fresh) return;

        state.Editor.SetContent(fresh.Text);
        state.File = fresh;
        state.Baseline = fresh.Text;
        state.ExternallyChanged = false;
        state.Deleted = false;

        Refresh(host.Main, title);
    }

    /// <summary>
    /// Opens what is on disk beside the buffer, read-only.
    ///
    /// <para>TWO WAYS IN, ONE IMPLEMENTATION: the toolbar button and the save dialog both land here,
    /// so there is one place to keep the read-only rule rather than two to forget it in.</para>
    ///
    /// <para>NOT HELD IN EDIT MODE, unlike the editor: that rule exists so typing works, and this
    /// buffer is not for typing. The current-line highlight is gated on the same flag, so a read-only
    /// editor shows none — do not set a colour for one expecting it to appear.</para>
    /// </summary>
    private static void ShowTheirs(EditorHost host, string title)
    {
        if (!For(host.Main).States.TryGetValue(title, out var state)) return;
        if (FileLoad.TryLoad(state.Path, out _) is not { } theirs) return;

        var theirTitle = title + " (on disk)";
        if (Find(host.Main, theirTitle) is { } already)
        {
            host.Main.Tabs.ActiveTabIndex = already;
            return;
        }

        var view = new MultilineEditControlBuilder()
            .WithContent(theirs.Text)
            .WithWrapMode(WrapMode.NoWrap)
            .WithLineNumbers()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithColors(ColorScheme.Code, ColorScheme.ChatSurface)
            .WithFocusedColors(ColorScheme.Code, ColorScheme.ChatSurface)
            .Build();
        view.ReadOnly = true;

        state.TheirsView = view;

        var content = Controls.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Cells(1), GridLength.Cells(1), GridLength.Star(1))
            .Place(new MarkupControl(new List<string>
                { $"[{ColorScheme.MutedMarkup}]on disk · read-only[/]" }), 0, 0)
            .Place(new RuleControl { Color = ColorScheme.MutedRgb }, 1, 0)
            .Place(view, 2, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();

        host.Main.AddTab(theirTitle, content);
        Show(host.Main, theirTitle);
    }
}
