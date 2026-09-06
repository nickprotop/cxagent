using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;
using Ctl = SharpConsoleUI.Builders.Controls;

namespace CxAgent.UI;

/// <summary>
/// One conversation's own surface: the transcript, the composer under it, and the panel beside it.
///
/// <para>WHAT SEPARATES THIS FROM <see cref="MainWindow"/> IS THE QUESTION "would a second session
/// need its own?". A transcript would, a composer would, a job panel would, the session's model and
/// mode would. The window, the tab strip, the status bar, the theme and the keymap would not — there
/// is one of each however many conversations are open, and duplicating them would mean two status
/// bars in one terminal.</para>
///
/// <para>MainWindow's own summary anticipated this split and declined it at the time: the UI wiring
/// "still live[s] in the composition root, because [it is] about a WINDOW rather than a conversation
/// and a second session in one window is a different question from a second session in one process."
/// This type is that question answered.</para>
///
/// <para>THE TRANSCRIPT AND THE COMPOSER ARE ONE GRID, not two things a layout happens to stack.
/// They are one surface — a conversation you read and the place you answer it — which is why a shell
/// tab takes the full height instead: a prompt hanging under someone else's terminal belongs to
/// neither clearly.</para>
/// </summary>
public sealed class SessionTab
{
    /// <summary>Rows the composer draws. Star above it, cells for it — an Auto row here would hand
    /// the transcript's ScrollablePanel an intrinsic measure, the shape that once painted nothing.</summary>
    private const int ComposerRows = 3;

    /// <summary>
    /// What this tab is a view of, or null before one is wired.
    ///
    /// <para>THE WINDOW IS BUILT BEFORE THE SESSION EXISTS — the composition root needs a window to
    /// hand the session's ports — so the first tab spends a moment as controls with no conversation
    /// behind them. Null says so rather than a half-built session pretending otherwise.</para>
    /// </summary>
    public Core.Sessions.Session? Session { get; private set; }

    /// <summary>Records the session this tab shows, once the composition root has one.</summary>
    public void NoteSession(Core.Sessions.Session session) => Session = session;

    /// <summary>The conversation.</summary>
    public ChatTranscriptControl Chat { get; }

    /// <summary>Where the user answers it.</summary>
    public PromptControl Input { get; }

    /// <summary>This session's tool rows.</summary>
    public JobPanelControl JobPanel { get; }

    /// <summary>The transcript over the composer — what the tab shows.</summary>
    public GridControl Content { get; private set; } = null!;

    /// <summary>
    /// What the tab strip calls this session.
    ///
    /// <para>NOT FIXED AT CREATION. A label is what distinguishes this tab from the others, so it
    /// depends on them: opening a second `src` has to qualify BOTH as `myapp/src` and `other/src`.
    /// <see cref="TabLabels"/> computes the set and this carries the answer.</para>
    /// </summary>
    public string Label { get; set; } = "Chat";

    public SessionTab(Core.Sessions.Session? session, ChatTranscriptControl chat,
                      PromptControl input, JobPanelControl jobPanel)
    {
        Session = session;
        Chat = chat;
        Input = input;
        JobPanel = jobPanel;
    }

    /// <summary>Assembles the tab's own grid over a composer built by the window.</summary>
    public void Compose(GridControl composer)
    {
        Content = Ctl.Grid()
            .Columns(GridLength.Star(1))
            .Rows(GridLength.Star(1), GridLength.Cells(ComposerRows))
            .Place(Chat, 0, 0)
            .Place(composer, 1, 0)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .WithAlignment(HorizontalAlignment.Stretch)
            .Build();
    }
}

/// <summary>
/// What to call each session tab, given all of them.
///
/// <para>A LABEL IS RELATIVE, so this takes the whole set rather than one path. Opening `other/src`
/// beside an existing `src` has to relabel the first tab too — leaving it as `src` would make the
/// unqualified one read as the main session rather than as the one that was there first.</para>
/// </summary>
public static class TabLabels
{
    /// <summary>
    /// Labels for these working directories, in the order given.
    ///
    /// <para>THREE TIERS, EACH REACHED ONLY WHEN THE ONE ABOVE FAILS: the folder's own name; the
    /// parent when two names collide; a counter when even the paths are equal, which happens only
    /// when somebody deliberately opened one folder twice.</para>
    /// </summary>
    public static IReadOnlyList<string> For(IReadOnlyList<string> workingDirectories)
    {
        var names = workingDirectories.Select(Basename).ToList();
        var labels = new string[names.Count];

        for (var i = 0; i < names.Count; i++)
        {
            // UNIQUE BY NAME IS ENOUGH, and it is the common case: two projects rarely share a
            // basename, and qualifying every tab against that possibility would make every label
            // longer to no purpose.
            if (names.Count(n => n == names[i]) == 1)
            {
                labels[i] = names[i];
                continue;
            }

            labels[i] = Qualified(workingDirectories[i]);
        }

        // THE COUNTER IS LAST, over labels rather than paths: two tabs on the SAME folder produce
        // the same qualified label, and nothing about either path can tell them apart.
        //
        // COUNTED FROM A SNAPSHOT, NOT FROM THE ARRAY BEING WRITTEN. Renaming the first of two
        // collisions removes it from the array, so the second then finds itself unique and keeps the
        // bare label — "myapp/src (1)" beside "myapp/src", which reads as two different folders.
        var qualified = labels.ToArray();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < labels.Length; i++)
        {
            if (qualified.Count(l => l == qualified[i]) == 1) continue;

            var ordinal = seen.TryGetValue(qualified[i], out var previous) ? previous + 1 : 1;
            seen[qualified[i]] = ordinal;
            labels[i] = $"{qualified[i]} ({ordinal})";
        }

        return labels;
    }

    /// <summary>The folder's own name, with a trailing separator ignored.</summary>
    private static string Basename(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        // A ROOT HAS NO NAME. "/" trims to empty and GetFileName answers empty; the path itself is
        // the only thing left to call it.
        return name.Length > 0 ? name : trimmed.Length > 0 ? trimmed : path;
    }

    /// <summary>The folder qualified by its parent — `myapp/src` rather than `src`.</summary>
    private static string Qualified(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);

        return parent.Length > 0 ? $"{parent}/{Basename(path)}" : Basename(path);
    }
}
