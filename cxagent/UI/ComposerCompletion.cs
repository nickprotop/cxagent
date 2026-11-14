using CxAgent.Core.Commands;

namespace CxAgent.UI;

/// <summary>Where a composer's completions come from.</summary>
/// <param name="Registry">The commands this host runs, its verbs included.</param>
/// <param name="Values">A named value set — instances, sessions, notes — as arguments, or null for none.</param>
/// <param name="Custom">The session's command files and typeable skills.</param>
/// <param name="Root">What a relative <c>@</c> path is relative to, or null to leave <c>@</c> as text.</param>
public sealed record ComposerSources(CommandRegistry? Registry, Func<string, IReadOnlyList<CommandArgument>>? Values,
    IReadOnlyList<SessionCommand> Custom, string? Root)
{
    /// <summary>True while commands are not offered — a btw aside takes the composer's text as a question.</summary>
    public bool CommandsOff { get; init; }

    /// <summary>Most paths an <c>@</c> collects. Bounded, because a two-character prefix walked with no cap reads a
    /// whole disk; large, because the list scrolls and a file left out of it cannot be chosen.</summary>
    public int MaxPaths { get; init; } = ComposerCompletion.DefaultMaxPaths;
}

/// <summary>The command rows for a line, and whether the line is already a finished command.</summary>
/// <param name="Rows">What is offered.</param>
/// <param name="LineIsComplete">True when a row completing to exactly what is typed was dropped — Enter should run the
/// line rather than choose.</param>
public sealed record ComposerCommandRows(IReadOnlyList<ComposerRow> Rows, bool LineIsComplete);

/// <summary>One thing a composer offers.</summary>
/// <param name="Label">What the list shows.</param>
/// <param name="Summary">What it is, beside the label.</param>
/// <param name="Text">The composer's whole text once this is chosen, or null for a row shown but not chosen — a
/// value the user types themselves.</param>
/// <param name="Caret">Where the caret goes in <paramref name="Text"/>.</param>
public sealed record ComposerRow(string Label, string Summary, string? Text, int Caret);

/// <summary>
/// What a composer offers as it is typed into: commands at the start of the line, then their verbs and values, and
/// paths after an <c>@</c> — each row carrying the text choosing it gives.
///
/// <para>ONE PLACE FOR BOTH FRONT ENDS. The terminal's menu draws these rows and the browser's list is sent them; a
/// second copy of the rules would drift, and the two composers would offer different things for the same keystrokes.
/// No window here, so a server can call it.</para>
/// </summary>
public static class ComposerCompletion
{
    /// <summary>The path cap both front ends use.</summary>
    public const int DefaultMaxPaths = 200;

    /// <summary>The rows for <paramref name="text"/> with the caret at <paramref name="caret"/>; empty when nothing is
    /// offered.</summary>
    public static IReadOnlyList<ComposerRow> For(string text, int caret, ComposerSources sources)
    {
        // THE @ BRANCH IS ANCHORED TO THE CARET, the slash branch to the start of the text, so the two cannot both
        // match and their order is not a precedence question.
        if (sources.Root is { Length: > 0 } root && AtToken.At(text, caret) is { } at)
            return Paths(text, at, root, sources.MaxPaths);

        if (!text.StartsWith('/') || text.Contains('\n') || sources.CommandsOff) return [];
        return Commands(text, sources).Rows;
    }

    /// <summary>The command rows for a line that starts with <c>/</c>, and whether the line is already a finished
    /// command — a row completing to what is typed was dropped, and Enter should run it.</summary>
    public static ComposerCommandRows Commands(string text, ComposerSources sources)
    {
        // A SPACE DESCENDS INTO THE COMMAND'S ARGUMENTS rather than ending the list: that is exactly when a user has
        // committed to a command and needs to know what may follow it.
        if (text.Contains(' '))
        {
            var args = SessionCommands.ArgumentsFor(text, sources.Values, sources.Registry, sources.Custom);

            // THE PREFIX IS EVERYTHING ALREADY COMMITTED TO, not just the command name: two levels down —
            // "/sessions resume 3" — completing to "/sessions 3" would give a command the dispatcher rejects.
            var prefix = text[..text.LastIndexOf(' ')];
            var rows = args.Select(a => Row(a.Name, a.Summary, CommandMenu.CompletionFor(a, prefix))).ToList();

            // NOTHING LEFT TO OFFER ONCE A COMPLETE ARGUMENT IS TYPED: a row completing to what is already there is
            // not a suggestion, and it would take the Enter meant to run the line.
            var kept = rows.Where(r => !string.Equals(r.Text?.TrimEnd(), text.TrimEnd(), StringComparison.Ordinal)).ToList();
            return new(kept, kept.Count < rows.Count);
        }

        // THE HINT RIDES WITH THE SUMMARY, so a command that takes arguments does not read the same as one that does not.
        return new([.. SessionCommands.Matching(text, sources.Registry, sources.Custom).Select(c =>
            Row(c.Name, c.TakesArguments ? $"{c.Summary}  {c.Hint}" : c.Summary, c.Name))], false);
    }

    /// <summary>
    /// What a colleague's Ask or Talk tab offers: its own commands, from the table its send runs
    /// (<see cref="Core.Colleagues.ColleagueTabCommands"/>), and nothing else — no session command runs there.
    /// </summary>
    public static IReadOnlyList<ComposerRow> ColleagueTab(string text) =>
        [.. Core.Colleagues.ColleagueTabCommands.Matching(text).Select(c => Row(c.Name, c.Summary, c.Name))];

    /// <summary>
    /// The paths an <c>@</c> reference can complete to. A DIRECTORY KEEPS THE <c>@</c>, so the list goes on to what is
    /// inside it; a FILE DROPS IT, since the reference is then complete and the model is sent an ordinary path in an
    /// ordinary sentence. The text after the caret is kept.
    /// </summary>
    public static IReadOnlyList<ComposerRow> Paths(string text, AtToken at, string root, int limit) =>
        [.. PathCompletions.Find(root, at.Prefix, limit).Select(h =>
        {
            var insert = h.IsDirectory ? "@" + h.Path : h.Path;
            return new ComposerRow(h.Display, h.IsDirectory ? "directory" : "file",
                CommandMenu.Splice(text, at, insert), at.Start + insert.Length);
        })];

    private static ComposerRow Row(string label, string summary, string? completion) =>
        new(label, summary, completion, completion?.Length ?? 0);
}
