using System.Diagnostics;
using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.Worktree;
using Xunit;

namespace CxAgent.Plugins.Worktree.Tests;

/// <summary>A throwaway repository with one commit, removed when the test ends.</summary>
internal sealed class Repo : IDisposable
{
    public string Root { get; } = System.IO.Path.TrimEndingDirectorySeparator(
        new DirectoryInfo(Directory.CreateTempSubdirectory("worktree-").FullName).FullName);

    public Repo(bool init = true)
    {
        if (!init) return;
        Git("init", "-q", "-b", "main");
        // AN IDENTITY OF ITS OWN: a merge commits, and a test machine with no global git identity
        // would fail every merge test for a reason that has nothing to do with the plugin.
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Write("app.txt", "one\ntwo\nthree\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "first");
    }

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public Repo Write(string relative, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(relative))!);
        File.WriteAllText(Path(relative), content);
        return this;
    }

    public string Read(string relative) => File.ReadAllText(Path(relative));

    public string Git(params string[] args) => GitIn(Root, args);

    public static string GitIn(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var git = Process.Start(psi)!;
        var output = git.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class WorktreeTests
{
    private static readonly List<int> Children = [];
    private static Worktrees Make(WorktreeSettings? settings = null) =>
        new(new Git("git", pid => { lock (Children) Children.Add(pid); }), settings ?? new WorktreeSettings());

    private static Outcome Ok(Outcome outcome)
    {
        Assert.True(outcome.Ok, outcome.Text);
        return outcome;
    }

    private static Outcome Refused(Outcome outcome)
    {
        Assert.False(outcome.Ok, outcome.Text);
        return outcome;
    }

    [Fact]
    public void Create_MakesACheckoutOnItsOwnBranch_InsideTheFolder()
    {
        using var repo = new Repo();

        var made = Ok(Make().Create(repo.Root, "fix-x", null));

        Assert.Contains("spawn it with root \".worktrees/fix-x\"", made.Text);
        Assert.True(File.Exists(repo.Path(".worktrees/fix-x/app.txt")));
        Assert.Equal("worktree/fix-x", Repo.GitIn(repo.Path(".worktrees/fix-x"), "branch", "--show-current").Trim());
    }

    /// <summary>
    /// THE USER'S STATUS STAYS CLEAN. A second checkout inside their tree would otherwise show as
    /// one enormous untracked folder, and their searches would find every file twice. The exclude
    /// file is local to this clone, so nothing about it is committed.
    /// </summary>
    [Fact]
    public void Create_KeepsTheWorktreeOutOfTheUsersStatus_WithoutTouchingGitignore()
    {
        using var repo = new Repo();

        Ok(Make().Create(repo.Root, "a", null));
        Ok(Make().Create(repo.Root, "b", null));

        Assert.Equal("", repo.Git("status", "--porcelain"));
        Assert.False(File.Exists(repo.Path(".gitignore")));
        // ONCE, however many worktrees are made.
        Assert.Single(File.ReadAllLines(repo.Path(".git/info/exclude")), l => l == "/.worktrees/");
    }

    /// <summary>
    /// FROM A SUBFOLDER, THE ROOT IS THAT SUBFOLDER'S COPY. The worktree is a checkout of the whole
    /// repository, so an agent working in <c>sub/</c> wants <c>sub/</c> inside it, not its top.
    /// </summary>
    [Fact]
    public void Create_FromASubfolder_ReturnsTheSubfoldersCopyAsTheRoot()
    {
        using var repo = new Repo();
        repo.Write("sub/code.txt", "x").Git("add", "-A");
        repo.Git("commit", "-q", "-m", "sub");

        var made = Ok(Make().Create(repo.Path("sub"), "s", null));

        Assert.Contains("root \".worktrees/s/sub\"", made.Text);
        Assert.True(File.Exists(repo.Path("sub/.worktrees/s/sub/code.txt")));
        Assert.Contains("/sub/.worktrees/", File.ReadAllLines(repo.Path(".git/info/exclude")));
        Assert.Equal("", repo.Git("status", "--porcelain"));
    }

    /// <summary>WORK IN PROGRESS IS NOT IN A NEW WORKTREE, and the reply says so — the surprise worth naming.</summary>
    [Fact]
    public void Create_SaysWhenTheFolderHasUncommittedChanges()
    {
        using var repo = new Repo();
        repo.Write("app.txt", "edited\n");

        var made = Ok(Make().Create(repo.Root, "w", null));

        Assert.Contains("1 uncommitted change", made.Text);
        Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(repo.Path(".worktrees/w/app.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("-b")]
    [InlineData("a/b")]
    [InlineData("x.lock")]
    [InlineData("")]
    public void Create_RefusesANameThatIsNotOne(string name)
    {
        using var repo = new Repo();
        Refused(Make().Create(repo.Root, name, null));
        Assert.False(Directory.Exists(repo.Path(".worktrees")));
    }

    [Fact]
    public void Create_RefusesABaseThatIsNotACommit_OrLooksLikeAnOption()
    {
        using var repo = new Repo();
        Assert.Contains("not a commit", Refused(Make().Create(repo.Root, "w", "nope")).Text);
        Assert.Contains("not a commit", Refused(Make().Create(repo.Root, "w", "--orphan")).Text);
    }

    [Fact]
    public void Create_RefusesADuplicate()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        Assert.Contains("already exists", Refused(Make().Create(repo.Root, "w", null)).Text);
    }

    [Fact]
    public void Create_OutsideARepository_Refuses()
    {
        using var folder = new Repo(init: false);
        Assert.Contains("not inside a git repository", Refused(Make().Create(folder.Root, "w", null)).Text);
    }

    /// <summary>
    /// NOT FROM INSIDE A WORKTREE. A sub-agent rooted in one that asked for another would nest
    /// checkouts, and its merge would land in the sandbox instead of the user's branch.
    /// </summary>
    [Fact]
    public void EveryOperation_RefusesFromInsideAWorktree()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        var inside = repo.Path(".worktrees/w");

        Assert.Contains("itself a worktree", Refused(Make().Create(inside, "x", null)).Text);
        Assert.Contains("itself a worktree", Refused(Make().Merge(inside, "w", null, false)).Text);
        Assert.Contains("itself a worktree", Refused(Make().Discard(inside, "w")).Text);
    }

    [Fact]
    public void List_ShowsWorkNotMerged_AndOnlyThisPluginsWorktrees()
    {
        using var repo = new Repo();
        using var elsewhere = new Repo(init: false);
        Ok(Make().Create(repo.Root, "busy", null));
        Ok(Make().Create(repo.Root, "idle", null));
        File.WriteAllText(repo.Path(".worktrees/busy/new.txt"), "n");
        // A WORKTREE THE USER MADE THEMSELVES, elsewhere: theirs, not listed.
        repo.Git("worktree", "add", "-q", "-b", "mine", Path.Combine(elsewhere.Root, "mine"));

        var listed = Ok(Make().List(repo.Root)).Text;

        Assert.Contains("busy: .worktrees/busy, branch worktree/busy — 0 commits and 1 uncommitted change not merged", listed);
        Assert.Contains("idle: .worktrees/idle, branch worktree/idle — nothing to merge", listed);
        Assert.DoesNotContain("mine", listed);
    }

    /// <summary>A NEW FILE IS PART OF THE WORK, though git does not track it yet.</summary>
    [Fact]
    public void Diff_ShowsCommittedUncommittedAndNewFiles()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        var wt = repo.Path(".worktrees/w");
        File.WriteAllText(Path.Combine(wt, "app.txt"), "one\nTWO\nthree\n");
        Repo.GitIn(wt, "commit", "-q", "-am", "change two");
        File.WriteAllText(Path.Combine(wt, "added.txt"), "brand new\n");

        var diff = Ok(Make().Diff(repo.Root, "w", statOnly: false)).Text;

        Assert.Contains("1 commit and 1 uncommitted change", diff);
        // FENCED AS A DIFF: rendered as markdown, a removed line would read as a bullet point.
        Assert.Contains("```diff\ndiff --git", diff);
        Assert.EndsWith("```", diff);
        Assert.Contains("+TWO", diff);
        Assert.Contains("+brand new", diff);

        var stat = Ok(Make().Diff(repo.Root, "w", statOnly: true)).Text;
        Assert.Contains("added.txt", stat);
        Assert.DoesNotContain("+brand new", stat);
    }

    /// <summary>A DIFF OF MARKDOWN CARRIES ITS OWN FENCES; the outer one is longer, so they cannot close it.</summary>
    [Fact]
    public void Diff_OfAFileWithFences_IsFencedByALongerOne()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/notes.md"), "```bash\nls\n```\n");

        var diff = Ok(Make().Diff(repo.Root, "w", statOnly: false)).Text;

        Assert.Contains("````diff\n", diff);
        Assert.EndsWith("\n````", diff);
    }

    [Fact]
    public void Diff_OfAnUnknownWorktree_NamesTheOnesThatExist()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "real", null));
        Assert.Contains("There is: real.", Refused(Make().Diff(repo.Root, "nope", false)).Text);
    }

    /// <summary>
    /// A SUB-AGENT LEAVES UNCOMMITTED EDITS, and the merge commits them there first — then brings
    /// them in and removes the worktree, so the user is left with the work and no clutter.
    /// </summary>
    [Fact]
    public void Merge_CommitsTheWorktreesEdits_BringsThemIn_AndRemovesIt()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/app.txt"), "one\nTWO\nthree\n");
        File.WriteAllText(repo.Path(".worktrees/w/added.txt"), "new\n");

        var merged = Ok(Make().Merge(repo.Root, "w", "Shout two", keep: false)).Text;

        Assert.Contains("Merged worktree 'w' into main (fast-forward", merged);
        Assert.Equal("one\nTWO\nthree\n", repo.Read("app.txt"));
        Assert.Equal("new\n", repo.Read("added.txt"));
        Assert.Equal("Shout two", repo.Git("log", "-1", "--format=%s").Trim());
        Assert.False(Directory.Exists(repo.Path(".worktrees/w")));
        Assert.Equal("", repo.Git("branch", "--list", "worktree/w"));
    }

    [Fact]
    public void Merge_WhenTheBranchMovedOn_MakesAMergeCommit()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/added.txt"), "new\n");
        repo.Write("other.txt", "main moved").Git("add", "-A");
        repo.Git("commit", "-q", "-m", "main moves");

        Assert.Contains("(merge commit", Ok(Make().Merge(repo.Root, "w", null, keep: false)).Text);
        Assert.Equal("new\n", repo.Read("added.txt"));
    }

    /// <summary>
    /// A CONFLICT IS UNDONE, never left in the user's files. Their checkout ends as it began, and the
    /// worktree still has its work for another try.
    /// </summary>
    [Fact]
    public void Merge_ThatConflicts_IsUndone_AndChangesNothing()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/app.txt"), "one\nTHEIRS\nthree\n");
        repo.Write("app.txt", "one\nOURS\nthree\n").Git("commit", "-q", "-am", "ours");
        var before = repo.Git("rev-parse", "HEAD");

        var refused = Refused(Make().Merge(repo.Root, "w", null, keep: false)).Text;

        Assert.Contains("conflicts in: app.txt", refused);
        Assert.Equal("one\nOURS\nthree\n", repo.Read("app.txt"));
        Assert.Equal(before, repo.Git("rev-parse", "HEAD"));
        Assert.Equal("", repo.Git("status", "--porcelain"));
        Assert.True(Directory.Exists(repo.Path(".worktrees/w")));
    }

    [Fact]
    public void Merge_WithKeep_LeavesTheWorktreeToGoOnIn()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/added.txt"), "new\n");

        Assert.Contains("still at .worktrees/w", Ok(Make().Merge(repo.Root, "w", null, keep: true)).Text);
        Assert.True(Directory.Exists(repo.Path(".worktrees/w")));
    }

    [Fact]
    public void Merge_WithNothingToMerge_SaysSo()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        Assert.Contains("Nothing to merge", Ok(Make().Merge(repo.Root, "w", null, keep: true)).Text);
    }

    [Fact]
    public void Discard_RemovesTheFolderAndBranch_AndSaysWhatWasLost()
    {
        using var repo = new Repo();
        Ok(Make().Create(repo.Root, "w", null));
        File.WriteAllText(repo.Path(".worktrees/w/added.txt"), "new\n");

        var discarded = Ok(Make().Discard(repo.Root, "w")).Text;

        Assert.Contains("0 commits and 1 uncommitted change are gone", discarded);
        Assert.False(Directory.Exists(repo.Path(".worktrees")));
        Assert.Equal("", repo.Git("branch", "--list", "worktree/w"));
        Assert.False(File.Exists(repo.Path("added.txt")));
    }

    [Fact]
    public void ACustomFolderAndPrefix_AreUsed_AndAFolderOutsideIsRefused()
    {
        using var repo = new Repo();
        var made = Ok(Make(new WorktreeSettings("sandboxes", "agent/")).Create(repo.Root, "w", null));
        Assert.Contains("root \"sandboxes/w\"", made.Text);
        Assert.Contains("on branch agent/w", made.Text);

        Refused(Make(new WorktreeSettings("../outside", "agent/")).Create(repo.Root, "v", null));
    }

    /// <summary>EVERY git PROCESS IS REGISTERED, so the host can reap one left running by a crash.</summary>
    [Fact]
    public void GitProcesses_AreRegisteredWithTheHost()
    {
        using var repo = new Repo();
        int before;
        lock (Children) before = Children.Count;
        Ok(Make().List(repo.Root));
        lock (Children) Assert.True(Children.Count > before);
    }
}

public class PluginTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Gate_AsksForMergeAndDiscardOnly_AndDiscardNeverOffersAlways()
    {
        var plugin = new WorktreePlugin();
        JobParameters Call(params (string, object?)[] args) => new(args.ToDictionary(a => a.Item1, a => a.Item2));

        var merge = plugin.Gate("worktree_merge", Call(("name", "fix")));
        Assert.NotNull(merge);
        Assert.Contains("Merge worktree 'fix'", merge.Display);
        Assert.Contains("then remove", merge.Display);
        Assert.True(merge.AlwaysAskable);

        Assert.DoesNotContain("remove", plugin.Gate("worktree_merge", Call(("name", "fix"), ("keep", true)))!.Display);

        var discard = plugin.Gate("worktree_discard", Call(("name", "fix")));
        Assert.NotNull(discard);
        Assert.False(discard.AlwaysAskable);

        Assert.Null(plugin.Gate("worktree_create", Call(("name", "fix"))));
        Assert.Null(plugin.Gate("worktree_diff", Call(("name", "fix"))));
        Assert.Null(plugin.Gate("worktree_list", Call()));
    }

    [Fact]
    public void Settings_AreReadLeniently()
    {
        Assert.Equal(new WorktreeSettings(), WorktreePlugin.Settings(default, out var git));
        Assert.Equal("git", git);

        var custom = WorktreePlugin.Settings(Json("""{ "folder": "box", "branchPrefix": "ai/", "git": "/usr/bin/git" }"""), out git);
        Assert.Equal(new WorktreeSettings("box", "ai/"), custom);
        Assert.Equal("/usr/bin/git", git);

        Assert.Equal(new WorktreeSettings(), WorktreePlugin.Settings(Json("""{ "folder": 3 }"""), out _));
    }

    [Fact]
    public async Task Load_ReturnsTheSidecar_WithMergeAndDiscardGated()
    {
        var manifest = await new WorktreePlugin().Load(new FakeContext(Path.GetTempPath()), CancellationToken.None);

        Assert.Equal("worktree", manifest.Name);
        Assert.True(manifest.Spawns);
        var gated = manifest.Tools.Where(t => t.Gated != PluginGating.Never).Select(t => t.Name).Order();
        Assert.Equal(["worktree_discard", "worktree_merge"], gated);
        Assert.False(manifest.Tools.Single(t => t.Name == "worktree_discard").AlwaysAskable);
    }

    /// <summary>THE CALLER'S FOLDER decides which repository a call is about, not the session's.</summary>
    [Fact]
    public async Task Invoke_UsesTheCallersFolder_AndReportsARefusalAsAFailedCall()
    {
        using var repo = new Repo();
        using var other = new Repo(init: false);
        var plugin = new WorktreePlugin();
        await plugin.Load(new FakeContext(other.Root), CancellationToken.None);

        var made = await plugin.Invoke("worktree_create", new JobParameters(new() { ["name"] = "w" }), new FakeJobContext(repo.Root), CancellationToken.None);
        Assert.True(made.Success, made.ErrorMessage);
        Assert.True(Directory.Exists(repo.Path(".worktrees/w")));

        var missing = await plugin.Invoke("worktree_diff", new JobParameters(), new FakeJobContext(repo.Root), CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Contains("needs a 'name'", missing.ErrorMessage);
    }
}

internal sealed class FakeContext(string folder) : IPluginContext
{
    public string WorkingDirectory => folder;
    public JsonElement Settings => default;
    public int HostContract => 3;
    public string HostVersion => "0.0.0";
    public IPluginLogger Logger { get; } = new NullLogger();
    public CancellationToken Lifetime => CancellationToken.None;
    public void RegisterChildProcess(int processId) { }

    private sealed class NullLogger : IPluginLogger { public void Log(string message) { } }
}

internal sealed class FakeJobContext(string? folder) : IJobContext
{
    public void ReportProgress(double percent, string? message = null) { }
    public string? Requester => null;
    public string? WorkingDirectory => folder;
    public void Log(string line) { }
    public void Log(JobLogLevel level, string line) { }
    public void ReportTextDelta(string delta) { }
}
