using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <c>/diff</c> — the review step, in the transcript.
///
/// <para>Git is injected, so these are about what the command DOES with git's answer rather than
/// about git. The one thing not tested here is the real process call, which the live drive covers.</para>
/// </summary>
public class DiffCommandTests
{
    private const string Here = "/projects/here";

    /// <summary>A runner that says "yes, a repo" and then returns one canned diff.</summary>
    private static DiffCommand.Runner Git(string diff, int exitCode = 0, string error = "") =>
        (_, args) => args[0] == "rev-parse"
            ? new DiffCommand.GitResult(0, "true\n", "")
            : new DiffCommand.GitResult(exitCode, diff, error);

    private const string OneFile = """
        diff --git a/a.txt b/a.txt
        index 1111111..2222222 100644
        --- a/a.txt
        +++ b/a.txt
        @@ -1,2 +1,3 @@
         context
        -removed
        +added one
        +added two
        """;

    /// <summary>
    /// OUTSIDE A REPOSITORY IT SAYS SO. `git diff` there fails with a message about discovery and
    /// ownership that reads as a bug in this app rather than as the plain fact that there is nothing
    /// to diff.
    /// </summary>
    [Fact]
    public void OutsideARepositoryItSaysSoPlainly()
    {
        DiffCommand.Runner notARepo = (_, _) => new(128, "", "fatal: not a git repository");

        var output = DiffCommand.Render("", Here, notARepo);

        Assert.Contains("Not a git repository", output);
        Assert.DoesNotContain("fatal", output);
    }

    /// <summary>Git absent is a different failure from git saying no, and is reported as one.</summary>
    [Fact]
    public void WhenGitCannotBeRunAtAllItSaysThat()
    {
        DiffCommand.Runner noGit = (_, _) => new(-1, "", "No such file or directory");

        var output = DiffCommand.Render("", Here, noGit);

        Assert.Contains("Could not run git", output);
        Assert.Contains("No such file", output);
    }

    /// <summary>
    /// COLOURED A LINE AT A TIME, not by a ```diff fence.
    ///
    /// <para>The System role renders as MARKUP rather than markdown, deliberately: every other
    /// System line is written in the library's [red]/[cyan] markup. A message can override its
    /// role's markdown setting, so a fence is reachable — this does not use one because colouring
    /// here is exact about which lines are content, and a generic highlighter paints the +++/---
    /// headers as additions and removals.</para>
    /// </summary>
    [Fact]
    public void AdditionsAndRemovalsAreColouredForScanning()
    {
        var output = DiffCommand.Render("", Here, Git(OneFile));

        Assert.Contains("[green]+added one[/]", output);
        Assert.Contains("[red]-removed[/]", output);
        Assert.Contains($"[{ColorScheme.AccentMarkup}]@@ -1,2 +1,3 @@[/]", output);
    }

    /// <summary>
    /// The +++/--- headers start with the same characters as additions and removals. Colouring them
    /// green and red is a small lie told on every single diff.
    /// </summary>
    [Fact]
    public void FileHeadersAreNotColouredAsChanges()
    {
        var output = DiffCommand.Render("", Here, Git(OneFile));

        Assert.DoesNotContain("[green]+++", output);
        Assert.DoesNotContain("[red]---", output);
        Assert.Contains($"[{ColorScheme.MutedMarkup}]+++ b/a.txt[/]", output);
    }

    /// <summary>
    /// DIFF CONTENT IS ARBITRARY FILE TEXT. A source line containing a bracketed token would be
    /// parsed as markup and swallowed — so the line vanishes from a review whose entire purpose is
    /// showing what changed.
    /// </summary>
    [Fact]
    public void MarkupInTheSourceIsEscapedRatherThanInterpreted()
    {
        var withMarkup = "diff --git a/x.cs b/x.cs\n+var s = \"[red]danger[/]\";";

        var output = DiffCommand.Render("", Here, Git(withMarkup));

        Assert.Contains("danger", output);
        Assert.DoesNotContain("\"[red]danger", output);   // not left as live markup
    }

    /// <summary>The shape of the change, before the change itself.</summary>
    [Fact]
    public void TheHeaderCountsFilesAndLines()
    {
        var output = DiffCommand.Render("", Here, Git(OneFile));

        Assert.Contains("1 file", output);
        Assert.Contains("+2", output);
        Assert.Contains("−1", output);
    }

    /// <summary>The +++/--- headers are not content; counting them inflates every file by one.</summary>
    [Fact]
    public void TheHeaderDoesNotCountTheFileHeaderLinesAsChanges()
    {
        var twoFiles = OneFile + "\n" + OneFile.Replace("a.txt", "b.txt");

        var output = DiffCommand.Render("", Here, Git(twoFiles));

        Assert.Contains("2 files", output);
        Assert.Contains("+4", output);      // not +8, which is what counting +++ would give
        Assert.Contains("−2", output);
    }

    [Fact]
    public void NoChangesSaysSoRatherThanShowingAnEmptyBlock()
    {
        var output = DiffCommand.Render("", Here, Git(""));

        Assert.Contains("no uncommitted changes", output);
    }

    /// <summary>
    /// CAPPED, AND THE CUT IS STATED. A diff that silently stops is one someone reads as complete,
    /// and "everything after this is fine" is the worst thing to imply by accident.
    /// </summary>
    [Fact]
    public void ALongDiffIsCappedAndSaysHowMuchWasElided()
    {
        var long_ = string.Join('\n',
            Enumerable.Range(0, DiffCommand.MaxLines + 25).Select(i => $"+line {i}"));

        var output = DiffCommand.Render("", Here, Git(long_));

        Assert.Contains("25 more lines", output);
        Assert.Contains("git diff", output);                 // how to see the rest
        Assert.Contains($"+line {DiffCommand.MaxLines - 1}", output);
        Assert.DoesNotContain($"+line {DiffCommand.MaxLines}\n", output);
    }

    [Fact]
    public void AShortDiffSaysNothingAboutElision()
    {
        Assert.DoesNotContain("more lines", DiffCommand.Render("", Here, Git(OneFile)));
    }

    // --- what git is actually asked ---

    /// <summary>Git's own ANSI escapes would arrive as literal bytes in the transcript, and the
    /// colouring is done here anyway.</summary>
    [Fact]
    public void GitIsAskedForAnUncolouredDiff()
    {
        var seen = new List<string>();
        DiffCommand.Runner capture = (_, args) =>
        {
            if (args[0] != "rev-parse") seen.AddRange(args);
            return new(0, args[0] == "rev-parse" ? "true\n" : OneFile, "");
        };

        DiffCommand.Render("", Here, capture);

        Assert.Contains("--no-color", seen);
        Assert.Contains("--no-pager", seen);
    }

    [Fact]
    public void StagedPassesTheFlagThroughAndSaysSoInTheHeader()
    {
        var seen = new List<string>();
        DiffCommand.Runner capture = (_, args) =>
        {
            if (args[0] != "rev-parse") seen.AddRange(args);
            return new(0, args[0] == "rev-parse" ? "true\n" : OneFile, "");
        };

        var output = DiffCommand.Render("--staged", Here, capture);

        Assert.Contains("--staged", seen);
        Assert.Contains("staged", output);
    }

    /// <summary>`--cached` is the same request spelled the way long-time git users spell it.</summary>
    [Fact]
    public void CachedIsAcceptedAsAnAliasForStaged()
    {
        var seen = new List<string>();
        DiffCommand.Runner capture = (_, args) =>
        {
            if (args[0] != "rev-parse") seen.AddRange(args);
            return new(0, args[0] == "rev-parse" ? "true\n" : "", "");
        };

        DiffCommand.Render("--cached", Here, capture);

        Assert.Contains("--staged", seen);
    }

    /// <summary>`--` separates paths from revisions, so a file named like a branch stays a file.</summary>
    [Fact]
    public void APathIsPassedAfterADoubleDash()
    {
        var seen = new List<string>();
        DiffCommand.Runner capture = (_, args) =>
        {
            if (args[0] != "rev-parse") seen.AddRange(args);
            return new(0, args[0] == "rev-parse" ? "true\n" : OneFile, "");
        };

        var output = DiffCommand.Render("src/main.cs", Here, capture);

        Assert.Equal("--", seen[^2]);
        Assert.Equal("src/main.cs", seen[^1]);
        Assert.Contains("src/main.cs", output);   // named in the header, so the scope is visible
    }

    /// <summary>
    /// A BAD PATH IS GIT'S MESSAGE, NOT OURS. It already names what it could not find, and rewording
    /// it would make it less precise than the tool the user will check it against.
    /// </summary>
    [Fact]
    public void GitsOwnErrorIsShownForABadPath()
    {
        var output = DiffCommand.Render("nope.txt", Here,
            Git("", exitCode: 128, error: "fatal: nope.txt: no such path in the working tree"));

        Assert.Contains("no such path", output);
    }

    // --- an empty diff is not always "nothing changed" ---

    /// <summary>
    /// A BRAND-NEW FILE IS NOT "NO CHANGES". `git diff` exits 0 with no output for a file git has
    /// never seen — and new files are precisely what this app spends its time creating. Reporting
    /// "no uncommitted changes" about a file someone just watched an agent write is how a review
    /// step loses its credibility in a single use.
    /// </summary>
    [Fact]
    public void UntrackedFilesAreNamedRatherThanReportedAsNoChanges()
    {
        DiffCommand.Runner git = (_, args) => args[0] switch
        {
            "rev-parse" => new(0, "true\n", ""),
            "ls-files" => new(0, "new.cs\ndocs/also-new.md\n", ""),
            _ => new(0, "", ""),
        };

        var output = DiffCommand.Render("", Here, git);

        Assert.Contains("2 untracked files", output);
        Assert.Contains("new.cs", output);
        Assert.Contains("git add", output);
        Assert.DoesNotContain("no uncommitted changes", output);
    }

    /// <summary>
    /// A PATH THAT IS NOT THERE. Git says nothing about it at all — same exit code, same empty
    /// output as a clean file — so reporting "no changes" would confirm a file that does not exist.
    /// </summary>
    [Fact]
    public void ANonExistentPathIsReportedRatherThanCalledClean()
    {
        DiffCommand.Runner git = (_, args) =>
            new(0, args[0] == "rev-parse" ? "true\n" : "", "");

        var output = DiffCommand.Render("definitely-not-here.txt", Here, git);

        Assert.Contains("No such path", output);
        Assert.Contains("definitely-not-here.txt", output);
    }

    /// <summary>A tracked file with nothing to show is the one case that really is "no changes".</summary>
    [Fact]
    public void ACleanTrackedTreeStillSaysNoChanges()
    {
        DiffCommand.Runner git = (_, args) =>
            new(0, args[0] == "rev-parse" ? "true\n" : "", "");

        Assert.Contains("no uncommitted changes", DiffCommand.Render("", Here, git));
    }

    /// <summary>
    /// `--staged` asks what is in the index. An untracked file is by definition not in it, so
    /// mentioning one there would answer a different question than the one asked.
    /// </summary>
    [Fact]
    public void StagedDoesNotMentionUntrackedFiles()
    {
        var asked = new List<string>();
        DiffCommand.Runner git = (_, args) =>
        {
            asked.Add(args[0]);
            return new(0, args[0] == "rev-parse" ? "true\n" : "", "");
        };

        var output = DiffCommand.Render("--staged", Here, git);

        Assert.DoesNotContain("ls-files", asked);
        Assert.Contains("no staged changes", output);
    }
}
