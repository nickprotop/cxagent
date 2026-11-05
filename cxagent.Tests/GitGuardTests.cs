using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What mounts cannot hold: a contained command redirecting git (a planted commondir, a rewritten pointer) or creating
/// an executable-config folder the project did not have. Seen after the fact, said, and contained commands ask again.
/// </summary>
public sealed class GitGuardTests : IDisposable
{
    private readonly string _repo = PathResolver.RealFolder(Directory.CreateTempSubdirectory("sbx-guard-").FullName);

    public GitGuardTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git", "hooks"));
        File.WriteAllText(Path.Combine(_repo, ".git", "HEAD"), "ref: refs/heads/main");
    }

    public void Dispose() { try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } }

    private IReadOnlyList<string> Changes(Action change)
    {
        var before = GitGuard.Snapshot(_repo);
        change();
        return GitGuard.Changes(before, GitGuard.Snapshot(_repo));
    }

    [Fact]
    public void Nothing_changed_is_no_change() => Assert.Empty(Changes(() => File.WriteAllText(Path.Combine(_repo, "a.txt"), "x")));

    [Fact]
    public void A_planted_commondir_is_a_change() =>
        Assert.Contains(Changes(() => File.WriteAllText(Path.Combine(_repo, ".git", "commondir"), "/tmp/evil")),
            c => c.Contains("commondir"));

    [Fact]
    public void A_rewritten_git_pointer_is_a_change()
    {
        Directory.Delete(Path.Combine(_repo, ".git"), recursive: true);
        File.WriteAllText(Path.Combine(_repo, ".git"), "gitdir: /a");
        Assert.Contains(Changes(() => File.WriteAllText(Path.Combine(_repo, ".git"), "gitdir: /tmp/evil")), c => c.Contains(".git"));
    }

    [Fact]
    public void A_new_executable_config_folder_is_a_change() =>
        Assert.Contains(Changes(() => Directory.CreateDirectory(Path.Combine(_repo, ".github"))), c => c.Contains(".github"));

    private static SandboxState State(List<string> warned) =>
        new(SandboxSettings.AppDefault, new SandboxHome(Path.GetTempPath(), null, null), () => SandboxStatus.Active)
        { Warn = warned.Add };

    [Fact]
    public void A_change_marks_the_state_and_warns_once()
    {
        var warned = new List<string>();
        var state = State(warned);
        state.Check(_repo);
        File.WriteAllText(Path.Combine(_repo, ".git", "commondir"), "/tmp/evil");
        Assert.NotNull(state.Check(_repo));
        Assert.Null(state.Check(_repo));
        Assert.NotNull(state.Tampered);
        Assert.Single(warned);
    }

    [Fact]
    public void Review_clears_it()
    {
        var state = State([]);
        state.Check(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".cxagent"));
        state.Check(_repo);
        state.Review();
        Assert.Null(state.Tampered);
        Assert.Null(state.Check(_repo));
    }

    /// <summary>AFTER A CHANGE, CONTAINED COMMANDS ASK until the user has looked: the sandbox cannot vouch for what
    /// git will run next.</summary>
    [Fact]
    public void A_tampered_sandbox_loses_its_silent_pass()
    {
        var cfg = Directory.CreateTempSubdirectory("sbx-guard-cfg-").FullName;
        try
        {
            var rules = new PermissionRulesStore(new AppPaths(cfg));
            rules.SetTrust(_repo, TrustState.Trusted);
            var policy = new PermissionPolicy(_repo, rules, Core.Sessions.EditMode.AcceptEdits);
            policy.Sandbox = State([]);
            var request = new PermissionRequest(PermissionKind.Shell, "touch x", "touch*", Subject: "touch x") { Contained = true };
            Assert.True(policy.IsContainedSilently(request));
            policy.Sandbox.Check(_repo);
            Directory.CreateDirectory(Path.Combine(_repo, ".vscode"));
            policy.Sandbox.Check(_repo);
            Assert.False(policy.IsContainedSilently(request));
        }
        finally { Directory.Delete(cfg, recursive: true); }
    }

    /// <summary>CXAGENT'S OWN git STATUS runs no fsmonitor and no hooks: a contained command that redirected git would
    /// otherwise have the app run its command, outside the sandbox, on the next status refresh.</summary>
    [Fact]
    public void The_apps_own_git_status_runs_no_fsmonitor()
    {
        if (!OperatingSystem.IsLinux() || !(Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, "git")))) return;
        var repo = Directory.CreateTempSubdirectory("sbx-fsmon-").FullName;
        try
        {
            var marker = Path.Combine(repo, "..", Path.GetFileName(repo) + ".ran");
            Run(repo, "init", "-q", ".");
            File.WriteAllText(Path.Combine(repo, "monitor.sh"), $"#!/bin/sh\ntouch '{marker}'\n");
            File.SetUnixFileMode(Path.Combine(repo, "monitor.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Run(repo, "config", "core.fsmonitor", Path.Combine(repo, "monitor.sh"));
            CxAgent.UI.SessionPanel.GitStatusForTest(repo, TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(marker));
            try { File.Delete(marker); } catch (IOException) { }
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    private static void Run(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }
}
