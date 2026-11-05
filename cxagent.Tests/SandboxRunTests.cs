using System.Diagnostics;
using CxAgent.Core.Execution;
using CxAgent.Core.Jobs.Builtin;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sandbox;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Commands run inside the real bubblewrap. Every folder is a temp one — a fake home, a fake config folder, a
/// project — so what is asserted is the sandbox's doing, not this machine's layout.
///
/// <para>EVERY TEST RETURNS EARLY WHERE BUBBLEWRAP DOES NOT START — the project's convention for a test that needs
/// something from the machine (see AbiPluginHostTests). Where it does start, these are the tests that show the
/// sandbox holds.</para>
/// </summary>
public sealed class SandboxRunTests : IDisposable
{
    private readonly string _base = PathResolver.RealFolder(Directory.CreateTempSubdirectory("sbx-run-").FullName);
    private string Home => Path.Combine(_base, "home");
    private string ConfigDir => Path.Combine(_base, "config");
    private string Project => Path.Combine(_base, "project");
    /// <summary>
    /// A folder no contained command may write: under the test's own build output, NOT the temp folder — the sandbox
    /// leaves /tmp writable on purpose. Where the build output itself sits under the temp folder there is no such
    /// place to test with, and <see cref="CanRun"/> is false.
    /// </summary>
    private readonly string _outside = Path.Combine(AppContext.BaseDirectory, "sbx-outside-" + Guid.NewGuid().ToString("N"));
    private string Outside => _outside;
    private static bool BuildIsUnderTemp =>
        PathResolver.RealFolder(AppContext.BaseDirectory).StartsWith(PathResolver.RealFolder(Path.GetTempPath()), StringComparison.Ordinal)
        || PathResolver.RealFolder(AppContext.BaseDirectory).StartsWith("/tmp/", StringComparison.Ordinal);
    private static readonly SandboxStatus Probed = SandboxProbe.Run(SandboxProbeRequest.ThisMachine());
    private static bool CanRun => Probed.Kind == SandboxStatusKind.Active && !BuildIsUnderTemp;

    public SandboxRunTests()
    {
        foreach (var d in new[] { Path.Combine(Home, ".ssh"), Path.Combine(Home, ".cache"), ConfigDir, Project, Outside })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(Home, ".ssh", "id"), "PRIVATE");
        File.WriteAllText(Path.Combine(ConfigDir, "config.json"), """{ "providers": { "x": { "apiKey": "{env:SBX_TEST_LLM}" } } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_outside, recursive: true); } catch (Exception) { }
    }

    private PermissionPolicy Policy(SandboxSettings? settings = null)
    {
        var policy = new PermissionPolicy(Project, new PermissionRulesStore(new AppPaths(ConfigDir)));
        policy.Sandbox = new SandboxState(settings ?? SandboxSettings.AppDefault,
            new SandboxHome(Home, ConfigDir, null), () => Probed) { ConfigKeyVariables = ["SBX_TEST_LLM"] };
        return policy;
    }

    private async Task<JobResult> Run(string command, PermissionPolicy? policy = null, Dictionary<string, string>? env = null,
        SandboxDecision? judged = null, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, object?> { ["command"] = command };
        if (env is not null) parameters["env"] = env;
        var context = new JobContext("a", "j", new Dictionary<string, JobResult>(), null)
        {
            Policy = policy ?? Policy(),
            WorkingDirectory = Project,
            JudgedSandbox = judged,
        };
        return await new ShellJobExecutor().ExecuteAsync(new JobParameters(parameters), context, ct);
    }

    private static string Out(JobResult r) => r.Output["stdout"]?.ToString() ?? "";

    [Fact]
    public async Task Writes_inside_the_folder_succeed()
    {
        if (!CanRun) return;
        var r = await Run("echo hi > made.txt && cat made.txt");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(Project, "made.txt")));
    }

    [Fact]
    public async Task Writes_outside_fail()
    {
        if (!CanRun) return;
        var r = await Run($"echo x > {Outside}/escaped.txt");
        Assert.False(r.Success);
        Assert.False(File.Exists(Path.Combine(Outside, "escaped.txt")));
    }

    [Fact]
    public async Task Writes_to_dot_git_hooks_fail()
    {
        if (!CanRun) return;
        Directory.CreateDirectory(Path.Combine(Project, ".git", "hooks"));
        var r = await Run("echo 'rm -rf ~' > .git/hooks/pre-commit");
        Assert.False(r.Success);
        Assert.False(File.Exists(Path.Combine(Project, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task Git_commit_works_in_the_folder()
    {
        if (!CanRun || SandboxProbe.FindOnPath() is null || !OnPath("git")) return;
        var r = await Run("git init -q . && git -c user.email=a@b -c user.name=a commit -q --allow-empty -m one && git log --oneline | wc -l");
        // INIT CREATES .git FIRST, so hooks/config are not yet bound read-only on this call — which is what lets
        // init run. The next call is the one that must still commit.
        Assert.True(r.Success, r.ErrorMessage + Out(r) + r.Output["stderr"]);
        var again = await Run("git -c user.email=a@b -c user.name=a commit -q --allow-empty -m two && git log --oneline | wc -l");
        Assert.True(again.Success, again.ErrorMessage + again.Output["stderr"]);
        Assert.Equal("2", Out(again).Trim());
    }

    [Fact]
    public async Task Ssh_folder_reads_empty()
    {
        if (!CanRun) return;
        var r = await Run($"ls -A {Home}/.ssh | wc -l");
        Assert.Equal("0", Out(r).Trim());
    }

    [Fact]
    public async Task Config_folder_reads_empty()
    {
        if (!CanRun) return;
        Assert.Equal("0", Out(await Run($"ls -A {ConfigDir} | wc -l")).Trim());
    }

    [Fact]
    public async Task Api_key_variable_is_gone()
    {
        if (!CanRun) return;
        Environment.SetEnvironmentVariable("SBX_TEST_API_KEY", "secret");
        Environment.SetEnvironmentVariable("SBX_TEST_LLM", "secret");
        try
        {
            var r = await Run("echo \"[$SBX_TEST_API_KEY][$SBX_TEST_LLM][${HOME:+home}]\"");
            Assert.Equal("[][][home]", Out(r).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SBX_TEST_API_KEY", null);
            Environment.SetEnvironmentVariable("SBX_TEST_LLM", null);
        }
    }

    /// <summary>
    /// BUBBLEWRAP IS PID 1 INSIDE, and its environment is readable at /proc/1/environ. A variable only unset for the
    /// command (--unsetenv) is still there; bubblewrap itself must start without it.
    /// </summary>
    [Fact]
    public async Task Unset_variables_are_not_readable_through_bubblewrap()
    {
        if (!CanRun) return;
        Environment.SetEnvironmentVariable("SBX_TEST_API_KEY", "secret");
        try
        {
            var r = await Run("tr '\\0' '\\n' < /proc/1/environ | grep -c SBX_TEST_API_KEY || true");
            Assert.Equal("0", Out(r).Trim());
        }
        finally { Environment.SetEnvironmentVariable("SBX_TEST_API_KEY", null); }
    }

    /// <summary>
    /// THE CALL'S env GOES IN, NOT ON BUBBLEWRAP. Set on bubblewrap's own process, an LD_PRELOAD the command built
    /// a call earlier runs before any namespace exists — outside the sandbox, and without a prompt.
    /// </summary>
    [Fact]
    public async Task The_calls_env_does_not_reach_bubblewrap()
    {
        if (!CanRun) return;
        var r = await Run("echo \"$SBX_MARK\"; tr '\\0' '\\n' < /proc/1/environ | grep -c SBX_MARK || true",
            env: new() { ["SBX_MARK"] = "inside" });
        Assert.Equal(["inside", "0"], Out(r).Trim().Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public async Task The_calls_own_env_is_kept()
    {
        if (!CanRun) return;
        var r = await Run("echo $MY_TOKEN", env: new() { ["MY_TOKEN"] = "asked-for" });
        Assert.Equal("asked-for", Out(r).Trim());
    }

    [Fact]
    public async Task Cache_is_writable()
    {
        if (!CanRun) return;
        Assert.True((await Run($"touch {Home}/.cache/x")).Success);
    }

    [Fact]
    public async Task Network_off_cannot_connect()
    {
        if (!CanRun) return;
        // A LOOPBACK LISTENER, so nothing leaves the machine: with --unshare-net the sandbox has its own, empty
        // loopback, and the connection is refused there.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var probe = $"exec 3<>/dev/tcp/127.0.0.1/{port} && echo open";
            var on = await Run($"bash -c '{probe}'");
            var off = await Run($"bash -c '{probe}'", Policy(SandboxSettings.AppDefault with { Network = false }));
            Assert.Equal("open", Out(on).Trim());
            Assert.NotEqual("open", Out(off).Trim());
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Symlinked_folder_still_writes()
    {
        if (!CanRun) return;
        var link = Path.Combine(_base, "link");
        Directory.CreateSymbolicLink(link, Project);
        var policy = new PermissionPolicy(link, new PermissionRulesStore(new AppPaths(ConfigDir)));
        policy.Sandbox = Policy().Sandbox;
        var parameters = new Dictionary<string, object?> { ["command"] = "touch via-link" };
        var context = new JobContext("a", "j", new Dictionary<string, JobResult>(), null) { Policy = policy, WorkingDirectory = link };
        var r = await new ShellJobExecutor().ExecuteAsync(new JobParameters(parameters), context, CancellationToken.None);
        Assert.True(r.Success, r.ErrorMessage + r.Output["stderr"]);
        Assert.True(File.Exists(Path.Combine(Project, "via-link")));
    }

    [Fact]
    public async Task Cancel_kills_the_whole_tree()
    {
        if (!CanRun) return;
        var marker = $"sbx-sleep-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        await Run($"sh -c 'exec -a {marker} sleep 300' & sh -c 'exec -a {marker} sleep 300'", ct: cts.Token);
        await Task.Delay(500);
        var left = Process.GetProcesses().Count(p =>
        {
            try { return File.ReadAllText($"/proc/{p.Id}/cmdline").Contains(marker); }
            catch (Exception) { return false; }
        });
        Assert.Equal(0, left);
    }

    [Fact]
    public async Task Escape_runs_uncontained()
    {
        if (!CanRun) return;
        var r = await Run($"echo x > {Outside}/out.txt", judged: new SandboxDecision(SandboxUse.Escape, "why"));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("uncontained", r.Output["sandbox"]);
    }

    [Fact]
    public async Task Off_runs_as_today_with_no_mark()
    {
        var r = await Run($"echo x > {Outside}/off.txt", Policy(SandboxSettings.Off));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.False(r.Output.ContainsKey("sandbox"));
    }

    [Fact]
    public async Task Result_says_contained()
    {
        if (!CanRun) return;
        Assert.Equal("contained", (await Run("true")).Output["sandbox"]);
    }

    [Fact]
    public async Task A_bwrap_that_will_not_start_fails_the_call_and_reprobes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fake = Path.Combine(_base, "bwrap");
        File.WriteAllText(fake, "#!/bin/sh\necho 'bwrap: setting up uid map: Permission denied' >&2\nexit 1\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var probes = 0;
        var policy = new PermissionPolicy(Project, new PermissionRulesStore(new AppPaths(ConfigDir)));
        // WORKING WHEN ASKED FIRST, BROKEN WHEN ASKED AGAIN: the bubblewrap that fails the command is the one the
        // re-probe confirms is failing, which is what lets the call be reported as the sandbox's failure.
        policy.Sandbox = new SandboxState(SandboxSettings.AppDefault, new SandboxHome(Home, ConfigDir, null), () =>
            ++probes == 1
                ? new SandboxStatus(SandboxStatusKind.Active, null, fake)
                : new SandboxStatus(SandboxStatusKind.Failed, "bwrap: setting up uid map: Permission denied"));
        var r = await Run($"echo x > {Outside}/never.txt", policy);
        Assert.False(r.Success);
        Assert.Contains("the sandbox could not start", r.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(Outside, "never.txt")));
        Assert.Equal(2, probes);
    }

    /// <summary>
    /// A COMMAND THAT ONLY LOOKS LIKE BUBBLEWRAP FAILING IS THE COMMAND'S OWN FAILURE. Believed outright, any command
    /// could print "bwrap: …" and exit 1 to be reported as a broken sandbox — pushing the model toward the escape.
    /// The status is asked again, and only a real failure is called one.
    /// </summary>
    [Fact]
    public async Task A_command_that_only_looks_like_a_bwrap_failure_is_its_own()
    {
        if (!CanRun) return;
        var r = await Run("echo 'bwrap: pretend' >&2; exit 1");
        Assert.False(r.Success);
        Assert.DoesNotContain("could not start", r.ErrorMessage);
        Assert.Equal("contained", r.Output["sandbox"]);
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, name)));
}
