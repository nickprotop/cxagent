using System.Diagnostics;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The working tree's shape — "1 changed · 1 untracked" — read the one way both front ends show it.</summary>
public sealed class GitTreeTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("gittree-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private void Repo()
    {
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Git("add", "a.txt");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "first");
    }

    [Fact]
    public void A_changed_and_an_untracked_file_are_counted()
    {
        Repo();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "two\n");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "new\n");
        Assert.Equal("1 changed · 1 untracked", GitTree.Status(_repo, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_clean_tree_is_null()
    {
        Repo();
        Assert.Null(GitTree.Status(_repo, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task The_cached_reading_arrives_and_names_the_folders_own_tree()
    {
        Repo();
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "new\n");
        string? seen = null;
        for (var i = 0; i < 50 && seen is null; i++) { seen = GitTree.Cached(_repo); await Task.Delay(100); }
        Assert.Equal("1 untracked", seen);
    }

    /// <summary>THE FOLDER'S OWN REPOSITORY, whatever folder the process runs in — a second tab in another project
    /// reads that project's branch and tree, not the first one's.</summary>
    [Fact]
    public void A_folder_is_read_as_its_own_repository_not_the_processes()
    {
        Repo();
        Git("checkout", "-q", "-b", "elsewhere");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "new\n");

        var read = GitTree.Read(_repo);

        Assert.NotNull(read);
        Assert.Equal("elsewhere", read.Branch);
        Assert.Equal("1 untracked", read.Status);
        Assert.Null(GitTree.Read(Path.GetTempPath()));
    }

    /// <summary>THE TERMINAL'S PANEL ASKS FOR THE SESSION'S FOLDER, as the browser's does.</summary>
    [Fact]
    public async Task The_terminal_panel_reads_git_in_the_sessions_folder()
    {
        var asked = new TaskCompletionSource<string>();
        var panel = new SessionPanel { ReadGitForTest = folder => { asked.TrySetResult(folder); return null; } };

        panel.Refresh(new SessionPanel.SessionPanelState { Endpoint = "", Rules = 0, WorkingDirectory = _repo });

        Assert.Equal(_repo, await asked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
