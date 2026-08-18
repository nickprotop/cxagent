using CxAgent.UI.Tools;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The parser, against output captured from real git rather than invented. Every fixture below was
/// produced by running the command in a repository and pasting what came back — a hand-written
/// approximation of porcelain format is a test of my memory, not of the parser.
/// </summary>
public class GitDiffTests
{
    /// <summary>Runs canned output instead of git, so these tests need no repository.</summary>
    private static GitDiff.Runner Canned(string wordDiff, string numstat = "1\t1\tf.cs", int insideExit = 0) =>
        (dir, args) => args[0] switch
        {
            "rev-parse" => new GitDiff.GitResult(insideExit, "true", ""),
            "diff" when args.Contains("--numstat") => new GitDiff.GitResult(0, numstat, ""),
            _ => new GitDiff.GitResult(0, wordDiff, ""),
        };

    private static GitDiffRequest Ask(string path = "f.cs") => new(path, "/repo", Staged: false);

    [Fact]
    public void ParsesIntraLineSpans()
    {
        // Captured from `git diff --word-diff=porcelain --unified=0` on a real edit.
        var diff = GitDiff.Read(Ask(), Canned("""
            diff --git a/f.cs b/f.cs
            index f1e734d..a88f057 100644
            --- a/f.cs
            +++ b/f.cs
            @@ -286 +285 @@ public class ExportOptionsTests
                 public void 
            -RejectsTopZeroOrNegative(string
            +RejectsTopNegative(string
             value)
            ~
            """));

        Assert.Equal(DiffStatus.Changed, diff.Status);
        var hunk = Assert.Single(diff.Hunks);

        var removed = hunk.Lines.Single(l => l.Kind == LineKind.Removed);
        var added = hunk.Lines.Single(l => l.Kind == LineKind.Added);

        // THE WHOLE POINT: the unchanged head and tail are separate spans from the changed middle,
        // so a renderer can highlight only what moved.
        Assert.Contains(removed.Spans, s => !s.Changed && s.Text.Contains("public void"));
        Assert.Contains(removed.Spans, s => s.Changed && s.Text.Contains("RejectsTopZeroOrNegative"));
        Assert.Contains(added.Spans, s => s.Changed && s.Text.Contains("RejectsTopNegative"));
    }

    [Fact]
    public void ParsesAPureDeletionWithNoAddedCounterpart()
    {
        // Real capture. The renderer must not assume - and + arrive in pairs.
        var diff = GitDiff.Read(Ask(), Canned("""
            diff --git a/f.cs b/f.cs
            index f1e734d..a88f057 100644
            --- a/f.cs
            +++ b/f.cs
            @@ -283 +282,0 @@ public class ExportOptionsTests
            -    [InlineData("0")]
            ~
            """));

        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.Lines, l => l.Kind == LineKind.Removed);
        Assert.DoesNotContain(hunk.Lines, l => l.Kind == LineKind.Added);
    }

    [Fact]
    public void SkipsTheDiffGitHeaderBlock()
    {
        // Every invocation opens with four lines that are not content. A parser that took them as
        // body would render "--- a/f.cs" as a removed line, which looks almost right and is wrong.
        var diff = GitDiff.Read(Ask(), Canned("""
            diff --git a/f.cs b/f.cs
            index f1e734d..a88f057 100644
            --- a/f.cs
            +++ b/f.cs
            @@ -1 +1 @@
            -old
            +new
            ~
            """));

        var hunk = Assert.Single(diff.Hunks);
        Assert.DoesNotContain(hunk.Lines, l => l.Spans.Any(s => s.Text.Contains("a/f.cs")));
        Assert.Equal(2, hunk.Lines.Count);
    }

    [Fact]
    public void ReportsBinaryRatherThanRenderingAnEmptyBody()
    {
        var diff = GitDiff.Read(Ask("b.bin"), Canned("""
            diff --git a/b.bin b/b.bin
            index 0f49c4a..f302552 100644
            Binary files a/b.bin and b/b.bin differ
            """, numstat: "-\t-\tb.bin"));

        Assert.Equal(DiffStatus.Binary, diff.Status);
        Assert.Empty(diff.Hunks);
    }

    [Fact]
    public void BinaryCountsAreZeroNotAParseFailure()
    {
        // git prints "-\t-" for a binary file's numstat. Parsing that as a number throws, and the
        // likeliest handling of the throw is a status the user reads as "no changes".
        var diff = GitDiff.Read(Ask("b.bin"), Canned("Binary files a/b.bin and b/b.bin differ",
            numstat: "-\t-\tb.bin"));

        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void ReportsNoChangesForAnUnchangedPath()
    {
        // The likeliest real call: the model shows a file it only thinks it edited. git prints
        // nothing, and a blank body would read as a rendering bug.
        var diff = GitDiff.Read(Ask(), Canned("", numstat: ""));

        Assert.Equal(DiffStatus.NoChanges, diff.Status);
    }

    [Fact]
    public void ReportsNotARepositoryWithoutRunningDiff()
    {
        // Asked FIRST, because outside a repository `git diff` fails with a message about ownership
        // and discovery that reads as a bug in this app rather than as the plain fact that there is
        // nothing to diff.
        var ran = new List<string>();
        GitDiff.Runner runner = (dir, args) =>
        {
            ran.Add(args[0]);
            return new GitDiff.GitResult(128, "", "not a git repository");
        };

        var diff = GitDiff.Read(Ask(), runner);

        Assert.Equal(DiffStatus.NotARepository, diff.Status);
        Assert.Equal(["rev-parse"], ran);
    }

    [Fact]
    public void CarriesTheCountsFromNumstat()
    {
        var diff = GitDiff.Read(Ask(), Canned("""
            diff --git a/f.cs b/f.cs
            @@ -1 +1 @@
            -old
            +new
            ~
            """, numstat: "12\t3\tf.cs"));

        Assert.Equal(12, diff.Added);
        Assert.Equal(3, diff.Removed);
    }
}

/// <summary>
/// The parser against a REAL repository, not canned strings.
///
/// <para>The fixtures above are captured output and cannot drift from git, but they also cannot
/// catch a mistake in how the arguments are ASSEMBLED — a wrong flag, a missing <c>--</c>, an
/// argument order git tolerates in one version and not the next. This runs the real binary.</para>
/// </summary>
public class GitDiffLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-gitdiff-" + Guid.NewGuid().ToString("N")[..8]);

    public GitDiffLiveTests()
    {
        Directory.CreateDirectory(_dir);
        Run("init", "-q", ".");
        Run("config", "user.email", "t@t");
        Run("config", "user.name", "t");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private void Run(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(info)!;
        p.WaitForExit(10_000);
    }

    [Fact]
    public void ReadsAModificationFromARealRepository()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "public void RejectsTopZero(string value)\n");
        Run("add", "-A");
        Run("commit", "-qm", "init");
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "public void RejectsTopNegative(string value)\n");

        var diff = GitDiff.Read(new GitDiffRequest("f.txt", _dir, Staged: false));

        Assert.Equal(DiffStatus.Changed, diff.Status);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);

        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(lines, l => l.Kind == LineKind.Removed);
        Assert.Contains(lines, l => l.Kind == LineKind.Added);

        // The unchanged head is its own span on both rows — the intra-line highlight this exists for.
        Assert.Contains(lines.SelectMany(l => l.Spans), s => !s.Changed && s.Text.Contains("public void"));
    }

    [Fact]
    public void ReportsNoChangesForAnUntouchedFile()
    {
        File.WriteAllText(Path.Combine(_dir, "same.txt"), "unchanged\n");
        Run("add", "-A");
        Run("commit", "-qm", "init");

        var diff = GitDiff.Read(new GitDiffRequest("same.txt", _dir, Staged: false));

        Assert.Equal(DiffStatus.NoChanges, diff.Status);
    }

    [Fact]
    public void ReportsBinaryFromARealRepository()
    {
        File.WriteAllBytes(Path.Combine(_dir, "b.bin"), [0, 1, 2, 3]);
        Run("add", "-A");
        Run("commit", "-qm", "init");
        File.WriteAllBytes(Path.Combine(_dir, "b.bin"), [9, 9, 9, 9, 9]);

        var diff = GitDiff.Read(new GitDiffRequest("b.bin", _dir, Staged: false));

        Assert.Equal(DiffStatus.Binary, diff.Status);
        Assert.Equal(0, diff.Added);   // git prints "-\t-", not numbers
    }

    [Fact]
    public void ReadsStagedChangesWhenAsked()
    {
        File.WriteAllText(Path.Combine(_dir, "s.txt"), "before\n");
        Run("add", "-A");
        Run("commit", "-qm", "init");
        File.WriteAllText(Path.Combine(_dir, "s.txt"), "after\n");
        Run("add", "s.txt");

        // Unstaged now sees nothing; staged sees the change. If --staged were dropped from the
        // argument list both would return the same answer and neither test would notice alone.
        Assert.Equal(DiffStatus.NoChanges, GitDiff.Read(new GitDiffRequest("s.txt", _dir, Staged: false)).Status);
        Assert.Equal(DiffStatus.Changed, GitDiff.Read(new GitDiffRequest("s.txt", _dir, Staged: true)).Status);
    }

    [Fact]
    public void ReportsNotARepositoryOutsideOne()
    {
        var bare = Path.Combine(Path.GetTempPath(), "cxagent-norepo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bare);
        try
        {
            var diff = GitDiff.Read(new GitDiffRequest("f.txt", bare, Staged: false));
            Assert.Equal(DiffStatus.NotARepository, diff.Status);
        }
        finally { Directory.Delete(bare, recursive: true); }
    }
}
