using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a contained command can write, what it sees empty, and the bubblewrap arguments that say so. Every
/// folder here is built under a temp directory — a fake home and a fake project — so nothing depends on the
/// machine the suite runs on.
/// </summary>
public sealed class SandboxProfileTests : IDisposable
{
    private readonly string _base = PathResolver.RealFolder(Directory.CreateTempSubdirectory("sbx-").FullName);
    private string Home => Path.Combine(_base, "home");
    private string ConfigDir => Path.Combine(Home, ".config", "cxagent");
    private string Runtime => Path.Combine(_base, "run");

    public SandboxProfileTests()
    {
        Directory.CreateDirectory(Path.Combine(Home, ".ssh"));
        Directory.CreateDirectory(Path.Combine(Home, ".cache"));
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(Runtime);
        File.WriteAllText(Path.Combine(Home, ".netrc"), "machine x");
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch (IOException) { } }

    private SandboxHome FakeHome => new(Home, ConfigDir, Runtime);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_base, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void GitDir(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, ".git", "hooks"));
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main");
    }

    private SandboxLayout Layout(string workingDir, bool narrowed = false, string? jobDir = null, SandboxSettings? settings = null) =>
        SandboxLayout.For(new SandboxLayoutRequest(workingDir, narrowed, jobDir, FakeHome, settings ?? SandboxSettings.AppDefault));

    private static IReadOnlyList<string> Args(SandboxLayout layout, bool foreground = true) =>
        SandboxProfile.Arguments(layout, new SandboxRun(foreground, []));

    /// <summary>The index of the pair <c>flag path</c> in the argument list, or -1.</summary>
    private static int At(IReadOnlyList<string> args, string flag, string path)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == flag && args[i + 1] == path) return i;
        return -1;
    }

    private static int AtDest(IReadOnlyList<string> args, string flag, string dest)
    {
        for (var i = 0; i + 2 < args.Count; i++)
            if (args[i] == flag && args[i + 2] == dest) return i;
        return -1;
    }

    [Fact]
    public void Plain_folder_is_its_own_writable_root()
    {
        var work = Folder("plain");
        Assert.Contains(work, Layout(work).Writable);
    }

    [Fact]
    public void Folder_inside_a_repo_writes_the_repo_root()
    {
        var repo = Folder("repo");
        GitDir(repo);
        var sub = Folder("repo", "src", "app");
        var layout = Layout(sub);
        Assert.Contains(repo, layout.Writable);
        Assert.DoesNotContain(sub, layout.Writable);
    }

    [Fact]
    public void Narrowed_child_writes_only_its_folder()
    {
        var repo = Folder("repo");
        GitDir(repo);
        var sub = Folder("repo", "module");
        var layout = Layout(sub, narrowed: true);
        Assert.Contains(sub, layout.Writable);
        Assert.DoesNotContain(repo, layout.Writable);
    }

    private (string Main, string Worktree) LinkedWorktree()
    {
        var main = Folder("main");
        GitDir(main);
        var gitdir = Folder("main", ".git", "worktrees", "w");
        File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..");
        File.WriteAllText(Path.Combine(gitdir, "config.worktree"), "");
        var wt = Folder("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {gitdir}\n");
        return (main, wt);
    }

    [Fact]
    public void Linked_worktree_adds_the_common_git_dir_with_hooks_and_config_read_only()
    {
        var (main, wt) = LinkedWorktree();
        var layout = Layout(wt);
        var common = Path.Combine(main, ".git");
        Assert.Contains(common, layout.Writable);
        Assert.DoesNotContain(main, layout.Writable);
        Assert.Contains(Path.Combine(common, "hooks"), layout.ReadOnly);
        Assert.Contains(Path.Combine(common, "config"), layout.ReadOnly);
        Assert.Contains(Path.Combine(common, "worktrees", "w", "config.worktree"), layout.ReadOnly);
        // THE POINTER FILE TOO: rewritten, it sends the next git command to a gitdir the command planted.
        Assert.Contains(Path.Combine(wt, ".git"), layout.ReadOnly);
    }

    [Fact]
    public void Narrowed_child_in_a_worktree_still_gets_the_common_dir()
    {
        var (main, wt) = LinkedWorktree();
        Assert.Contains(Path.Combine(main, ".git"), Layout(wt, narrowed: true).Writable);
    }

    [Fact]
    public void Executable_config_is_read_only_after_the_writable_root()
    {
        var repo = Folder("repo");
        GitDir(repo);
        Folder("repo", ".github");
        Folder("repo", ".cxagent");
        File.WriteAllText(Path.Combine(repo, ".envrc"), "");
        var layout = Layout(repo);
        var args = Args(layout);

        var root = At(args, "--bind", repo);
        Assert.True(root >= 0);
        foreach (var ro in new[] { ".github", ".cxagent", ".envrc", Path.Combine(".git", "hooks"), Path.Combine(".git", "config") })
            Assert.True(At(args, "--ro-bind", Path.Combine(repo, ro)) > root, ro);
        // THE REST OF .git STAYS WRITABLE, or add and commit fail.
        Assert.DoesNotContain(Path.Combine(repo, ".git"), layout.ReadOnly);
    }

    [Fact]
    public void Submodule_git_dirs_have_their_hooks_and_config_read_only()
    {
        var repo = Folder("repo");
        GitDir(repo);
        var module = Folder("repo", ".git", "modules", "lib");
        Directory.CreateDirectory(Path.Combine(module, "hooks"));
        File.WriteAllText(Path.Combine(module, "config"), "");
        File.WriteAllText(Path.Combine(module, "HEAD"), "");
        var layout = Layout(repo);
        Assert.Contains(Path.Combine(module, "hooks"), layout.ReadOnly);
        Assert.Contains(Path.Combine(module, "config"), layout.ReadOnly);
    }

    [Fact]
    public void Hidden_comes_after_writable_when_home_is_the_root()
    {
        var args = Args(Layout(Home));
        var home = At(args, "--bind", Home);
        Assert.True(home >= 0);
        Assert.True(At(args, "--tmpfs", Path.Combine(Home, ".ssh")) > home);
        Assert.True(AtDest(args, "--ro-bind", Path.Combine(Home, ".netrc")) > home);
    }

    [Fact]
    public void Job_folder_is_bound_back_after_the_config_dir_is_hidden()
    {
        var job = Path.Combine(ConfigDir, "logs", "a", "j1");
        Directory.CreateDirectory(job);
        var args = Args(Layout(Folder("w"), jobDir: job));
        Assert.True(At(args, "--bind", job) > At(args, "--tmpfs", ConfigDir));
        Assert.True(At(args, "--tmpfs", ConfigDir) >= 0);
    }

    [Fact]
    public void Runtime_dir_is_an_empty_tmpfs() =>
        Assert.True(At(Args(Layout(Folder("w"))), "--tmpfs", Runtime) >= 0);

    [Fact]
    public void A_hidden_file_gets_dev_null() =>
        Assert.Contains(Path.Combine(Home, ".netrc"), Layout(Folder("w")).HiddenFiles);

    [Fact]
    public void Cache_is_writable_and_missing_paths_are_skipped()
    {
        var layout = Layout(Folder("w"));
        Assert.Contains(Path.Combine(Home, ".cache"), layout.Writable);
        Assert.DoesNotContain(Path.Combine(Home, ".npm"), layout.Writable);
        Assert.DoesNotContain(Path.Combine(Home, ".aws"), layout.HiddenFolders);
    }

    [Fact]
    public void User_lists_expand_tilde_against_the_home()
    {
        Directory.CreateDirectory(Path.Combine(Home, "out"));
        Directory.CreateDirectory(Path.Combine(Home, "secret"));
        var layout = Layout(Folder("w"), settings: SandboxSettings.AppDefault with { Writable = ["~/out"], Hidden = ["~/secret"] });
        Assert.Contains(Path.Combine(Home, "out"), layout.Writable);
        Assert.Contains(Path.Combine(Home, "secret"), layout.HiddenFolders);
    }

    [Fact]
    public void Network_off_adds_unshare_net()
    {
        var work = Folder("w");
        Assert.DoesNotContain("--unshare-net", Args(Layout(work)));
        Assert.Contains("--unshare-net", Args(Layout(work, settings: SandboxSettings.AppDefault with { Network = false })));
    }

    [Fact]
    public void Background_call_has_no_die_with_parent()
    {
        var layout = Layout(Folder("w"));
        Assert.Contains("--die-with-parent", Args(layout, foreground: true));
        Assert.DoesNotContain("--die-with-parent", Args(layout, foreground: false));
    }

    [Fact]
    public void Arguments_end_with_the_separator_and_run_in_the_working_folder()
    {
        var work = Folder("w");
        var args = Args(Layout(work));
        Assert.Equal("--", args[^1]);
        Assert.True(At(args, "--chdir", work) >= 0);
        Assert.Contains("--unshare-pid", args);
        Assert.Contains("--new-session", args);
    }

    [Fact]
    public void Unset_names_become_unsetenv()
    {
        var args = SandboxProfile.Arguments(Layout(Folder("w")), new SandboxRun(true, ["GITHUB_TOKEN"]));
        Assert.True(At(args, "--unsetenv", "GITHUB_TOKEN") >= 0);
    }

    [Fact]
    public void Symlinked_working_folder_binds_the_real_path()
    {
        var real = Folder("real");
        var link = Path.Combine(_base, "link");
        Directory.CreateSymbolicLink(link, real);
        var layout = Layout(link);
        Assert.Contains(real, layout.Writable);
        Assert.Equal(real, layout.WorkingDir);
    }
}
