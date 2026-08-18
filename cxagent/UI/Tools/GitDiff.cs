using System.Diagnostics;

namespace CxAgent.UI.Tools;

/// <summary>Why there is no body to render, when there is none. Three of these four values mean the
/// renderer prints one line instead of a diff, and naming them here keeps the "why is this empty"
/// answer with the code that actually knows.</summary>
public enum DiffStatus { Changed, NoChanges, Binary, NotARepository }

/// <summary>Context, Added or Removed — the leading marker in porcelain output.</summary>
public enum LineKind { Context, Added, Removed }

/// <param name="Text">The run of characters.</param>
/// <param name="Changed">True when git marked this run as differing. A line has at least one span
/// and often three: unchanged head, changed middle, unchanged tail.</param>
public readonly record struct Span(string Text, bool Changed);

/// <summary>Old and new line numbers. Either is null on a pure add or a pure delete.</summary>
public readonly record struct LineNumbers(int? Old, int? New);

/// <param name="Kind">Which side of the diff this line belongs to.</param>
/// <param name="Spans">The line split into unchanged and changed runs. A line with ONE span is one
/// git reported as wholly changed; the renderer must not assume more.</param>
/// <param name="Numbers">The gutter. A record because the pair is one concept, and because two bare
/// <c>int?</c> side by side transpose silently — AV1561's exact case.</param>
public sealed record DiffLine(LineKind Kind, IReadOnlyList<Span> Spans, LineNumbers Numbers);

/// <param name="Header">git's own <c>@@</c> line, including the enclosing function it appends.</param>
public sealed record Hunk(string Header, IReadOnlyList<DiffLine> Lines);

/// <param name="Path">The one file, relative to <paramref name="WorkingDir"/>.</param>
/// <param name="WorkingDir">The folder to ask git about.</param>
/// <param name="Staged">Staged changes rather than unstaged.</param>
public readonly record struct GitDiffRequest(string Path, string WorkingDir, bool Staged);

public sealed record FileDiff(
    string Path, int Added, int Removed, DiffStatus Status, IReadOnlyList<Hunk> Hunks);

/// <summary>
/// One file's diff, as structure. No markup, no colour, no terminal — <see cref="DiffRenderer"/>
/// turns this into something to look at, and keeping the two apart is what makes the parser
/// testable against captured output.
///
/// <para>ONE FILE, NOT A TREE. That is the whole shape of the tool rather than a flag on it: it
/// bounds the cost, makes the header unambiguous, and removes rename handling entirely — a rename is
/// a two-path fact and this is only ever asked about one path.</para>
///
/// <para>INTRA-LINE HIGHLIGHTING COMES FROM GIT. <c>--word-diff=porcelain</c> marks the changed
/// runs directly, so the alternative — an LCS implementation of our own — buys nothing but a second
/// thing to get subtly wrong.</para>
/// </summary>
public static class GitDiff
{
    /// <param name="ExitCode">Git's exit code, or -1 when git could not be run at all.</param>
    /// <param name="Output">Standard output.</param>
    /// <param name="Error">Standard error, or the reason git never started.</param>
    public readonly record struct GitResult(int ExitCode, string Output, string Error);

    /// <summary>
    /// Runs a git command, or reports why it could not.
    ///
    /// <para>COPIED FROM <c>DiffCommand</c> RATHER THAN SHARED, and deliberately. That type lives in
    /// cxagent.Core and answers a different question — what the USER typed <c>/diff</c> to see. The
    /// two will drift, and coupling them would make every change to one a risk to the other. What is
    /// borrowed is the shape, because it exists so this is testable without a repository.</para>
    /// </summary>
    public delegate GitResult Runner(string workingDir, IReadOnlyList<string> arguments);

    /// <summary>Generous: a large repository's first diff can be slow, and the alternative to
    /// waiting is a wrong answer.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static FileDiff Read(GitDiffRequest request, Runner? run = null)
    {
        run ??= RunGit;

        // ASKED FIRST, exactly as DiffCommand does. Outside a repository `git diff` fails with a
        // message about ownership and discovery that reads as a bug in this app rather than as the
        // plain fact that there is nothing to diff.
        var inside = run(request.WorkingDir, ["rev-parse", "--is-inside-work-tree"]);
        if (inside.ExitCode != 0)
            return new FileDiff(request.Path, 0, 0, DiffStatus.NotARepository, []);

        var staged = request.Staged ? new[] { "--staged" } : [];

        var counts = run(request.WorkingDir,
            ["diff", .. staged, "--numstat", "--", request.Path]);
        var (added, removed) = ParseCounts(counts.Output);

        var body = run(request.WorkingDir,
            ["diff", .. staged, "--word-diff=porcelain", "--unified=0", "--", request.Path]);

        // NOTHING AT ALL means the path is unchanged — the likeliest real call, when the model shows
        // a file it only thinks it edited. A blank body would read as a rendering bug.
        if (string.IsNullOrWhiteSpace(body.Output))
            return new FileDiff(request.Path, added, removed, DiffStatus.NoChanges, []);

        if (body.Output.Contains("\nBinary files ", StringComparison.Ordinal)
            || body.Output.StartsWith("Binary files ", StringComparison.Ordinal))
            return new FileDiff(request.Path, added, removed, DiffStatus.Binary, []);

        var hunks = ParseHunks(body.Output);

        // A DIFF WHOSE HUNKS ALL PARSED AWAY is not a change worth showing. Reached when git printed
        // only a header — a mode change, say — where saying "no changes" is closer to true than
        // rendering an empty box.
        return hunks.Count == 0
            ? new FileDiff(request.Path, added, removed, DiffStatus.NoChanges, [])
            : new FileDiff(request.Path, added, removed, DiffStatus.Changed, hunks);
    }

    /// <summary>
    /// <c>12\t3\tpath</c> → (12, 3).
    ///
    /// <para>BINARY FILES PRINT <c>-\t-</c>, not numbers. int.Parse throws on that, and the likeliest
    /// handling of the throw is a status the user reads as "no changes" — so the dash is parsed as
    /// the zero it means.</para>
    /// </summary>
    private static (int Added, int Removed) ParseCounts(string numstat)
    {
        var line = numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return (0, 0);

        var parts = line.Split('\t');
        if (parts.Length < 2) return (0, 0);

        return (int.TryParse(parts[0], out var a) ? a : 0,
                int.TryParse(parts[1], out var r) ? r : 0);
    }

    /// <summary>
    /// Porcelain word-diff into hunks.
    ///
    /// <para>THE FORMAT, because it is not obvious from the name: after a <c>@@</c> header, each
    /// line carries ONE marker — a leading space for an unchanged run, <c>-</c> or <c>+</c> for a
    /// changed one, and a bare <c>~</c> for the end of a line. Runs accumulate until a <c>~</c>
    /// closes them. A single source line therefore spans several output lines, which is exactly the
    /// structure needed to highlight part of it.</para>
    ///
    /// <para>THE FIRST FOUR LINES ARE NOT CONTENT (<c>diff --git</c>, <c>index</c>, <c>---</c>,
    /// <c>+++</c>). Taking them as body renders "--- a/f.cs" as a removed line, which looks almost
    /// right.</para>
    /// </summary>
    private static List<Hunk> ParseHunks(string output)
    {
        var hunks = new List<Hunk>();

        string? header = null;
        var lines = new List<DiffLine>();
        var pending = new List<MarkedSpan>();
        var oldNumber = 0;
        var newNumber = 0;

        void CloseLine()
        {
            if (pending.Count == 0) return;

            // ONE GROUP CAN BE TWO LINES, and this is the format's least obvious property. A `~`
            // closes the word-diff GROUP, not a source line — a modification emits `-old`, `+new`
            // and then a SINGLE `~`, verified against real git output. Treating a group as one line
            // merged both sides of every modification into a single row that showed the old and new
            // text run together.
            //
            // So the group is projected onto each side: the removed line takes the unchanged runs
            // plus the `-` runs, the added line the unchanged runs plus the `+` runs. Context spans
            // appear on BOTH, which is what makes an unchanged prefix show up on either row.
            var hasRemoved = pending.Any(s => s.Changed && s.Kind == LineKind.Removed);
            var hasAdded = pending.Any(s => s.Changed && s.Kind == LineKind.Added);

            if (!hasRemoved && !hasAdded)
            {
                lines.Add(new DiffLine(LineKind.Context,
                    [.. pending.Select(s => new Span(s.Text, false))],
                    new LineNumbers(++oldNumber, ++newNumber)));
                pending.Clear();
                return;
            }

            if (hasRemoved)
                lines.Add(new DiffLine(LineKind.Removed,
                    [.. pending.Where(s => s.Kind != LineKind.Added).Select(s => new Span(s.Text, s.Changed))],
                    new LineNumbers(++oldNumber, null)));

            if (hasAdded)
                lines.Add(new DiffLine(LineKind.Added,
                    [.. pending.Where(s => s.Kind != LineKind.Removed).Select(s => new Span(s.Text, s.Changed))],
                    new LineNumbers(null, ++newNumber)));

            pending.Clear();
        }

        void CloseHunk()
        {
            CloseLine();
            if (header is not null && lines.Count > 0)
                hunks.Add(new Hunk(header, [.. lines]));
            lines.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                CloseHunk();
                header = line;
                (oldNumber, newNumber) = ParseHunkStart(line);
                continue;
            }

            // Before the first @@ everything is header, and after one a "diff --git" means a second
            // file — which cannot happen here, since this asks about one path.
            if (header is null) continue;

            if (line == "~") { CloseLine(); continue; }
            if (line.Length == 0) continue;

            var marker = line[0];
            var text = line[1..];

            pending.Add(marker switch
            {
                '-' => new MarkedSpan(text, Changed: true, LineKind.Removed),
                '+' => new MarkedSpan(text, Changed: true, LineKind.Added),
                _ => new MarkedSpan(text, Changed: false, LineKind.Context),
            });
        }

        CloseHunk();
        return hunks;
    }

    /// <summary>The starting line numbers from <c>@@ -283 +282,0 @@</c>. One less than the first
    /// line, because the counters above pre-increment as each line closes.</summary>
    private static (int Old, int New) ParseHunkStart(string header)
    {
        var parts = header.Split(' ');
        var old = parts.FirstOrDefault(p => p.StartsWith('-'))?.TrimStart('-').Split(',')[0];
        var neu = parts.FirstOrDefault(p => p.StartsWith('+'))?.TrimStart('+').Split(',')[0];

        return (int.TryParse(old, out var o) ? o - 1 : 0,
                int.TryParse(neu, out var n) ? n - 1 : 0);
    }

    /// <summary>A span that still remembers which marker produced it, so <c>CloseLine</c> can decide
    /// the line's kind. Dropped on the way into <see cref="Span"/>, which a renderer reads: by then
    /// the LINE carries the kind and repeating it per span would let the two disagree.</summary>
    private readonly record struct MarkedSpan(string Text, bool Changed, LineKind Kind);

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
}
