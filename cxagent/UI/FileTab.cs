using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Sessions;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Events;
using SharpConsoleUI.Highlighting;
using SharpConsoleUI.Layout;

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
    /// <summary>Every open file, so a second /open switches rather than opening a rival buffer.</summary>
    private static readonly OpenFiles Open_ = new();

    /// <summary>Per-tab state, keyed by the tab title the registry handed out.</summary>
    private static readonly Dictionary<string, TabState> States = new(StringComparer.Ordinal);

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
        if (Open_.TryGetTitle(file.Path, out var existing))
        {
            Show(host.Main, existing);
            return;
        }

        var title = Open_.Add(file.Path);

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

        var editor = editorBuilder.Build();

        // ALWAYS IN EDIT MODE. A buffer you must first activate before it takes a keystroke is a mode
        // nobody asked for and nothing on screen shows; the composer carries the same rule, and
        // "typing silently dies" was how it was found. Escape cannot knock it back out either — it is
        // a global shortcut (AppBootstrap.cs:1515), consulted before the focused control, so it never
        // reaches here to be interpreted as "leave edit mode".
        editor.IsEditing = true;
        editor.EscapeExitsEditMode = false;

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

        States[title] = new TabState
        {
            Path = file.Path,
            Editor = editor,
            Status = status,
            Reload = reload,
            SeeTheirs = seeTheirs,
            File = file,
            Baseline = file.Text,
        };

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
        var title = Open_.Add(path);

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

    /// <summary>A tab's index, or null when it has already gone.</summary>
    private static int? Find(MainWindow main, string title)
    {
        for (var i = 0; i < main.Tabs.TabCount; i++)
            if (main.Tabs.GetTab(i)?.Title == title) return i;

        return null;
    }

    /// <summary>
    /// Repaints the toolbar and the tab title from the state.
    ///
    /// <para>ONE PLACE THAT DECIDES, so the marker on the title and the words in the toolbar cannot
    /// disagree about what is true of the file.</para>
    /// </summary>
    private static void Refresh(MainWindow main, string title)
    {
        if (!States.TryGetValue(title, out var state)) return;

        var modified = state.IsModified;

        state.Reload.Visible = state.ExternallyChanged && !state.Deleted;
        state.SeeTheirs.Visible = state.ExternallyChanged && !state.Deleted;

        var lines = state.Editor.GetContent().Split('\n').Length;
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

    /// <summary>Test seam: drives a save to completion, which RequestSave cannot be awaited for.</summary>
    public static Task SaveForTest(EditorHost host, string title) => SaveAsync(host, title);

    /// <summary>Test seam: types into a tab's editor without simulating keystrokes.</summary>
    public static void SetContentForTest(string title, string content)
    {
        if (States.TryGetValue(title, out var state)) state.Editor.SetContent(content);
    }

    /// <summary>
    /// Test seam: whether a tab's editor is in edit mode.
    ///
    /// <para>Public with a ForTest suffix because this assembly grants no InternalsVisibleTo — see
    /// ColorScheme.cs:184.</para>
    /// </summary>
    public static bool EditorIsEditingForTest(string title)
        => States.TryGetValue(title, out var state) && state.Editor.IsEditing;

    // The behaviour below arrives with the tasks that own it; the buttons exist now so the toolbar is
    // built once rather than three times.
    /// <summary>
    /// Writes the buffer, then tells the model it happened.
    ///
    /// <para>ASKS FIRST WHEN THE FILE CHANGED UNDER US — see SaveNeedsConfirmationForTest. The
    /// dialog belongs to the confirmation work; until it exists the safe branch is taken and nothing
    /// is written, because the failure this guards against is silent.</para>
    /// </summary>
    private static async void RequestSave(EditorHost host, string title)
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
            if (States.TryGetValue(title, out var s))
                s.Status.SetContent(new List<string> { $"[{ColorScheme.MutedMarkup}]{ex.Message}[/]" });
        }
    }

    private static async Task SaveAsync(EditorHost host, string title)
    {
        if (!States.TryGetValue(title, out var state)) return;

        if (SaveNeedsConfirmationForTest(state.ExternallyChanged)) return;

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

        Refresh(host.Main, title);
        host.Session.Inject(SaveMessage(state.Path, host.Session.WorkingDirectory));
    }
    private static void RequestReload(EditorHost host, string title) { }
    private static void ShowTheirs(EditorHost host, string title) { }
}
