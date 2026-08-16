using System.Diagnostics;

namespace CxAgent.Core.Commands;

/// <summary>
/// <c>/diff</c> — what has changed in the working tree, in the transcript.
///
/// <para>THE REVIEW STEP HAD NO HOME. The app writes files without asking inside the working folder,
/// and the README says plainly that <c>git diff</c> is how you check it — which means the one action
/// every user must perform after every session was the one thing the app could not show them. They
/// either trusted it or opened another terminal.</para>
///
/// <para>IT IS <c>git diff</c>, NOT OUR OWN RECORD. Snapshotting files ourselves would mean a second
/// baseline that disagrees with git's: it would miss edits made in another window, and show as
/// changed a file the user had since reverted. Deferring to git means the answer is the same one the
/// user's own tooling gives, and the cases we cannot see — a commit made mid-session — are git's to
/// explain rather than ours to get subtly wrong.</para>
///
/// <para>FOR THE USER, NOT THE MODEL. Whether the agent should be able to diff its own work is a
/// separate and larger question; this is a command someone types, and its output goes to the
/// transcript rather than into the conversation.</para>
/// </summary>
public static class DiffCommand
{
    /// <summary>
    /// How many lines of diff reach the transcript.
    ///
    /// <para>A five-thousand-line diff is not review material, it is scrollback. The cut is STATED
    /// rather than silent — a diff that simply stops is one someone will read as complete, and
    /// "everything after this is fine" is the worst possible thing to imply by accident.</para>
    /// </summary>
    public const int MaxLines = 400;

    /// <summary>How long git gets before the command gives up. Generous: a large repo's first diff
    /// can be slow, and the alternative to waiting is a wrong answer.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs a git command in <paramref name="workingDir"/>, or reports why it could not.</summary>
    public delegate GitResult Runner(string workingDir, IReadOnlyList<string> arguments);

    /// <param name="ExitCode">Git's exit code, or -1 when git could not be run at all.</param>
    /// <param name="Output">Standard output.</param>
    /// <param name="Error">Standard error, or the reason git never started.</param>
    public readonly record struct GitResult(int ExitCode, string Output, string Error);

    /// <summary>
    /// Renders <c>/diff</c>, <c>/diff --staged</c> or <c>/diff &lt;path&gt;</c>.
    /// </summary>
    /// <param name="argument">Everything after the command word.</param>
    /// <param name="workingDir">The folder to ask git about.</param>
    /// <param name="run">How to run git. Injected so this is testable without a repository.</param>
    public static string Render(string argument, string workingDir, Runner? run = null)
    {
        run ??= RunGit;

        // IS THIS A REPOSITORY AT ALL? Asked first, because `git diff` outside one fails with a
        // message about ownership and discovery that reads as a bug in this app rather than as the
        // plain fact that there is nothing to diff.
        var inside = run(workingDir, ["rev-parse", "--is-inside-work-tree"]);
        if (inside.ExitCode != 0)
        {
            return inside.ExitCode < 0
                ? $"[{Markup.Danger}]Could not run git: {Escape(inside.Error.Trim())}[/]"
                : $"[{Markup.Muted}]Not a git repository — nothing to diff.[/]";
        }

        var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var staged = words.Any(w => w is "--staged" or "--cached");
        var paths = words.Where(w => !w.StartsWith('-')).ToList();

        // NO COLOUR FROM GIT. Colour is applied below, per line; git's own ANSI escapes would arrive
        // as literal bytes in the transcript — visible garbage rather than colour.
        var args = new List<string> { "--no-pager", "diff", "--no-color" };
        if (staged) args.Add("--staged");
        if (paths.Count > 0)
        {
            // `--` SEPARATES PATHS FROM REVISIONS, so a file named like a branch is still a file.
            args.Add("--");
            args.AddRange(paths);
        }

        var result = run(workingDir, args);

        if (result.ExitCode < 0)
            return $"[{Markup.Danger}]Could not run git: {Escape(result.Error.Trim())}[/]";

        // A BAD PATH IS GIT'S MESSAGE, NOT OURS. It already says which path it could not find, and
        // rewording it would only make it less precise than the tool the user will check it against.
        if (result.ExitCode != 0)
            return $"[{Markup.Danger}]{Escape(result.Error.Trim())}[/]";

        var scope = staged ? "staged" : "uncommitted";
        var what = paths.Count > 0 ? $" · {Escape(string.Join(" ", paths))}" : "";

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            // EMPTY IS NOT ALWAYS "NOTHING CHANGED", and the difference matters here more than
            // almost anywhere. `git diff` exits 0 with no output for a path that does not exist, and
            // for a file git has never seen — so a brand-new file, which is precisely what this app
            // spends its time creating, reports as "no changes". Saying that about a file someone
            // just watched an agent write is how a review step loses its credibility in one use.
            var untracked = staged ? [] : Untracked(run, workingDir, paths);

            if (untracked.Count > 0)
                return $"[{Markup.Accent}]Diff[/] "
                     + $"[{Markup.Muted}]· no tracked changes{what} · "
                     + $"{untracked.Count} untracked file{(untracked.Count == 1 ? "" : "s")}: "
                     + $"{Escape(string.Join(", ", untracked.Take(5)))}"
                     + $"{(untracked.Count > 5 ? ", …" : "")} — `git add` to see them here[/]";

            // A NAMED PATH THAT IS NOT THERE. Git says nothing about it, so this has to.
            if (paths.Count > 0 && paths.All(p => !PathExists(workingDir, p)))
                return $"[yellow]No such path: "
                     + $"{Escape(string.Join(", ", paths))}[/]";

            return $"[{Markup.Accent}]Diff[/] "
                 + $"[{Markup.Muted}]· no {scope} changes{what}[/]";
        }

        var lines = result.Output.TrimEnd('\n').Split('\n');
        var shown = lines.Length > MaxLines ? lines[..MaxLines] : lines;

        var head = $"[{Markup.Accent}]Diff[/] "
                 + $"[{Markup.Muted}]· {scope} · {Summarise(lines)}{what}[/]";

        // COLOURED HERE, A LINE AT A TIME, rather than by a ```diff fence.
        //
        // The role cannot do it: System renders as MARKUP, not markdown, and deliberately — every
        // other System line is written in the library's [red]/[cyan] markup, so turning markdown on
        // for the role would make all of those render literally. (A MESSAGE can override its role's
        // markdown setting, so a fence IS reachable; this does not take that route because the
        // per-line colouring is already here and already exact about which lines are content.)
        //
        // Doing it here also fixes something a fence would not: +++/--- headers start with the same
        // characters as additions and removals, and a generic diff highlighter paints them green and
        // red — a small lie told on every single diff.
        var body = string.Join('\n', shown.Select(Colour));

        var elided = lines.Length > MaxLines
            ? $"\n[{Markup.Muted}]… {lines.Length - MaxLines} more lines — "
              + $"`git diff{(staged ? " --staged" : "")}` for the rest[/]"
            : "";

        return $"{head}\n{body}{elided}";
    }

    /// <summary>
    /// Files git is not tracking, so an empty diff can say WHY it is empty.
    ///
    /// <para>Scoped to the paths asked about, when any were: <c>/diff src/</c> reporting untracked
    /// files elsewhere in the repo would be answering a question nobody asked.</para>
    /// </summary>
    private static IReadOnlyList<string> Untracked(
        Runner run, string workingDir, IReadOnlyList<string> paths)
    {
        var args = new List<string> { "ls-files", "--others", "--exclude-standard" };
        if (paths.Count > 0)
        {
            args.Add("--");
            args.AddRange(paths);
        }

        var result = run(workingDir, args);
        if (result.ExitCode != 0) return [];

        return [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Is this path actually there? Bounded and never throwing, like everything else here.</summary>
    private static bool PathExists(string workingDir, string path)
    {
        try
        {
            var full = Path.Combine(workingDir, path);
            return File.Exists(full) || Directory.Exists(full);
        }
        catch (Exception)
        {
            // An unreadable or malformed path is not something to claim exists.
            return false;
        }
    }

    /// <summary>
    /// One diff line, coloured by what it is.
    ///
    /// <para>THE FOUR THINGS A READER SCANS FOR: which file, which hunk, what arrived, what left.
    /// Everything else is context and stays plain, so the eye lands on the changes rather than on
    /// the surrounding lines.</para>
    ///
    /// <para>ESCAPED FIRST. Diff content is arbitrary file text, and a source line containing
    /// <c>[red]</c> — or any bracketed token — would otherwise be parsed as markup and swallowed.
    /// </para>
    /// </summary>
    private static string Colour(string line)
    {
        var text = Escape(line);

        // File headers before the +++/--- check: those lines start with the same characters as
        // additions and removals, and colouring them green and red is a small daily lie.
        if (line.StartsWith("diff --git ", StringComparison.Ordinal)
         || line.StartsWith("index ", StringComparison.Ordinal)
         || line.StartsWith("+++", StringComparison.Ordinal)
         || line.StartsWith("---", StringComparison.Ordinal)
         || line.StartsWith("new file", StringComparison.Ordinal)
         || line.StartsWith("deleted file", StringComparison.Ordinal))
            return $"[{Markup.Muted}]{text}[/]";

        if (line.StartsWith("@@", StringComparison.Ordinal))
            return $"[{Markup.Accent}]{text}[/]";

        if (line.StartsWith('+')) return $"[green]{text}[/]";
        if (line.StartsWith('-')) return $"[red]{text}[/]";

        return text;
    }

    /// <summary>
    /// "3 files · +42 −7", counted from the diff itself.
    ///
    /// <para>THE SHAPE OF THE CHANGE BEFORE THE CHANGE ITSELF. A user scrolling past a capped diff
    /// still learns how much there was — which is the number that decides whether to open a real
    /// terminal.</para>
    /// </summary>
    private static string Summarise(IReadOnlyList<string> lines)
    {
        int files = 0, added = 0, removed = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) files++;
            // The +++/--- header lines are not content, and counting them inflates every file by one.
            else if (line.StartsWith("+++", StringComparison.Ordinal)
                  || line.StartsWith("---", StringComparison.Ordinal)) continue;
            else if (line.StartsWith('+')) added++;
            else if (line.StartsWith('-')) removed++;
        }

        return $"{files} file{(files == 1 ? "" : "s")} · +{added} −{removed}";
    }

    /// <summary>
    /// Runs git, bounded, never throwing — the same contract the session panel's git reads have.
    /// Git may be absent, the repository may be enormous, and neither is worth a broken command.
    /// </summary>
    private static GitResult RunGit(string workingDir, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in arguments) info.ArgumentList.Add(a);

            using var p = Process.Start(info);
            if (p is null) return new(-1, "", "git did not start");

            // READ BEFORE WAITING. A diff larger than the pipe buffer blocks git until someone
            // drains it, so waiting first would deadlock on exactly the large diffs this caps.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch (Exception) { }
                return new(-1, "", $"git did not finish within {Timeout.TotalSeconds:0}s");
            }

            return new(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return new(-1, "", ex.Message);
        }
    }

    private static string Escape(string text) => SharpConsoleUI.Parsing.MarkupParser.Escape(text);
}
