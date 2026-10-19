using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Plugins.Testing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Plugin folders for the harness tests, each a fresh temp folder the test may change.</summary>
internal static class Folders
{
    // ONE ROOT PER TEST RUN, removed when the run ends: a plugin a test loaded keeps its dll open
    // until then, so a test cannot always delete its own folder, and dozens were left in /tmp.
    private static readonly string Root = CreateRoot();

    private static string CreateRoot()
    {
        var root = Directory.CreateTempSubdirectory("harness-run-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        };
        return root;
    }

    public static string NewFolder(string prefix) => Directory.CreateDirectory(Path.Combine(Root, prefix + Guid.NewGuid().ToString("N"))).FullName;

    public static string Of(string assemblyStem, string folderName)
    {
        var dir = NewFolder("harness-");
        File.Copy(Path.Combine(AppContext.BaseDirectory, assemblyStem + ".dll"), Path.Combine(dir, folderName + ".dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, assemblyStem + ".plugin.json"), Path.Combine(dir, folderName + ".plugin.json"));
        return dir;
    }

    public static string WellFormed() => Of("cxagent.Tests.PluginFixture", "fixture");

    /// <summary>
    /// A first-party plugin as a user installs it: its dll and sidecar from the test output, with the
    /// files it depends on beside them. The test output itself holds every plugin's sidecar at once,
    /// which is not a plugin folder.
    /// </summary>
    public static string FirstParty(string name, params string[] dependencies)
    {
        var dir = Of(name, name);
        foreach (var file in dependencies)
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(dir, file));
        return dir;
    }

    // THE ASSEMBLY'S OWN NAME FOR THE COPY: the fixture's Load uses FromSidecar, which reads
    // <assembly-name>.plugin.json — a renamed copy would have no sidecar its own code can find.
    public static string Harness() => Of("cxagent.Tests.PluginFixture.Harness", "cxagent.Tests.PluginFixture.Harness");

    public static string NativeReports()
    {
        var dir = NewFolder("harness-native-");
        File.Copy(NativeFixtures.Path("fixture-reports")!, Path.Combine(dir, "fixture-reports" + NativeFixtures.Suffix));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixture-reports.plugin.json"), Path.Combine(dir, "fixture-reports.plugin.json"));
        return dir;
    }

    public static string WellFormedWithSidecar(string json)
    {
        var dir = WellFormed();
        File.WriteAllText(Path.Combine(dir, "fixture.plugin.json"), json);
        return dir;
    }
}

/// <summary>What <c>cxagent plugin check</c> finds without loading anything: the folder and the sidecar.</summary>
public class PluginHarnessCheckTests
{
    [Fact]
    public async Task AWellFormedPlugin_HasNoProblems()
    {
        var dir = Folders.WellFormed();
        Assert.Empty(await PluginHarness.CheckAsync(dir));
        TempDirectory.Delete(dir);
    }

    [Fact]
    public async Task AFolderWithoutASidecar_IsOneProblem()
    {
        var dir = Folders.NewFolder("harness-");
        var problem = Assert.Single(await PluginHarness.CheckAsync(dir));
        Assert.Equal("sidecar", problem.Field);
        Assert.Contains("no *.plugin.json", problem.Message);
        TempDirectory.Delete(dir);
    }

    [Fact]
    public async Task TwoSidecars_IsOneProblem()
    {
        var dir = Folders.WellFormed();
        File.Copy(Path.Combine(dir, "fixture.plugin.json"), Path.Combine(dir, "other.plugin.json"));
        var problem = Assert.Single(await PluginHarness.CheckAsync(dir));
        Assert.Equal("sidecar", problem.Field);
        Assert.Contains("fixture.plugin.json", problem.Message);
        Assert.Contains("other.plugin.json", problem.Message);
        TempDirectory.Delete(dir);
    }

    [Fact]
    public async Task ASidecarWithoutItsBinary_IsOneProblem()
    {
        var dir = Folders.WellFormed();
        File.Delete(Path.Combine(dir, "fixture.dll"));
        var problem = Assert.Single(await PluginHarness.CheckAsync(dir));
        Assert.Equal("file", problem.Field);
        Assert.Contains("fixture.dll", problem.Message);
        TempDirectory.Delete(dir);
    }

    /// <summary>Every problem, not the first: a sidecar wrong in three ways is three lines.</summary>
    [Fact]
    public async Task EveryStaticProblem_IsReported()
    {
        var dir = Folders.WellFormedWithSidecar("""
            {"pluginContract":2,"kind":"native","name":"fixture","version":"1.0.0","spawns":false,
             "tools":[{"name":"read_file","description":"x","inputSchema":{"type":"object"}}]}
            """);

        var problems = await PluginHarness.CheckAsync(dir);

        Assert.Contains(problems, p => p.Field == "kind" && p.Message.Contains("contract 4"));
        Assert.Contains(problems, p => p.Field == "kind" && p.Message.Contains(".NET assembly"));
        Assert.Contains(problems, p => p.Field == "tools[0].name" && p.Message.Contains("built-in"));
        TempDirectory.Delete(dir);
    }
}

/// <summary>What check finds by loading: the code's manifest against the sidecar, managed and native.</summary>
public class PluginHarnessLoadCheckTests
{
    [Fact]
    public async Task AManifestTheCodeContradicts_IsALoadProblem()
    {
        var dir = Folders.Of("cxagent.Tests.PluginFixture.Mismatched", "p");

        var problem = Assert.Single(await PluginHarness.CheckAsync(dir));

        Assert.Equal("load", problem.Field);
        TempDirectory.Delete(dir);
    }

    [Fact]
    public async Task ANativePlugin_IsLoadedThroughTheHost_AndPasses()
    {
        if (NativeFixtures.Path("fixture-reports") is null) return;
        var dir = Folders.NativeReports();

        Assert.Empty(await PluginHarness.CheckAsync(dir));
        TempDirectory.Delete(dir);
    }

    /// <summary>A test finds a host without being told where: here, the one the reference put beside it.</summary>
    [Fact]
    public void AHost_IsFound_WithoutBeingTold()
    {
        Assert.True(File.Exists(PluginHarness.HostDllForTest(new HarnessOptions())));
    }
}

/// <summary>One call through a loaded plugin: the gate a session would ask, the result, what it reported.</summary>
public class PluginHarnessCallTests
{
    private static readonly JsonElement FailStart = JsonSerializer.SerializeToElement(new { failStart = true });

    [Fact]
    public async Task ACall_RunsTheLifecycle_AndReturnsTheResult()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.WellFormed());

        var call = await plugin.CallAsync("wf_tool");

        Assert.Null(call.Gate);
        Assert.True(call.Result!.Success);
    }

    [Fact]
    public async Task ADynamicGate_IsAskedAsTheSessionAsks_AndWithoutAnAnswer_TheCallStops()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var call = await plugin.CallAsync("echo", new { text = "hi" });

        Assert.Equal("echo hi", call.Gate!.Display);
        Assert.True(call.Gate.AlwaysAskable);
        Assert.True(call.StoppedAtGate);
        Assert.Null(call.Result);
    }

    [Fact]
    public async Task ADeniedGate_NeverInvokes()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var call = await plugin.CallAsync("echo", new { text = "hi" }, answer: GateAnswer.Deny);

        Assert.Equal(GateAnswer.Deny, call.Answer);
        Assert.Null(call.Result);
        Assert.Empty(call.Reports);
    }

    [Fact]
    public async Task AnAllowedCall_RecordsItsReports_InOrder()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());
        var live = new List<HarnessReport>();

        var call = await plugin.CallAsync("echo", new { text = "hi" }, answer: GateAnswer.Allow, onReport: live.Add);

        Assert.Equal("hi", call.Result!.Output["content"]);
        Assert.Equal(
            [new HarnessReport.Progress(50, "half"), new HarnessReport.Log(JobLogLevel.Info, "working"), new HarnessReport.Stream("hi")],
            call.Reports);
        Assert.Equal(call.Reports, live);
    }

    [Fact]
    public async Task ANativeCall_RecordsProgressLogAndStream_InOrder()
    {
        if (NativeFixtures.Path("fixture-reports") is null) return;
        await using var plugin = await PluginHarness.LoadAsync(Folders.NativeReports());

        var call = await plugin.CallAsync("echo");

        Assert.True(call.Result!.Success);
        Assert.Equal(
            [new HarnessReport.Progress(50, "half"), new HarnessReport.Log(JobLogLevel.Info, "working"),
             new HarnessReport.Stream("one "), new HarnessReport.Stream("two")],
            call.Reports);
    }

    [Fact]
    public async Task AToolTheManifestDoesNotDeclare_IsRefused_NamingTheOnesItDoes()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => plugin.CallAsync("nope"));

        Assert.Contains("echo, submit, slow", refused.Message);
    }

    [Fact]
    public async Task AStartThatThrows_IsALoadProblem()
    {
        var refused = await Assert.ThrowsAsync<PluginLoadException>(() =>
            PluginHarness.LoadAsync(Folders.Harness(), new HarnessOptions { Settings = FailStart }));

        var problem = Assert.Single(refused.Problems);
        Assert.Equal("start", problem.Field);
        Assert.Contains("start refused", problem.Message);
    }

    [Fact]
    public async Task ASubmit_IsRecorded_NotSentAnywhere()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        await plugin.CallAsync("submit");

        Assert.Equal(["tidy the folder"], plugin.Submits);
    }

    [Fact]
    public async Task ACommand_RunsThroughItsHandler()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var result = await plugin.CommandAsync("harness-ping", "now");

        Assert.Equal("pong now", result.Message);
    }

    [Fact]
    public async Task ACommandTheManifestDoesNotDeclare_IsRefused()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        await Assert.ThrowsAsync<ArgumentException>(() => plugin.CommandAsync("nope"));
    }

    [Fact]
    public async Task ACallsOwnToken_StopsAPluginThatNeverReturns()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.CallAsync("slow", ct: soon.Token));
    }
}

/// <summary>The harness when a plugin misbehaves: it comes back, cleans up, and answers as a session would.</summary>
public class PluginHarnessMisbehaviourTests
{
    private static HarnessOptions With(string setting) =>
        new() { Settings = JsonSerializer.SerializeToElement(new Dictionary<string, bool> { [setting] = true }) };

    /// <summary>A session stops waiting when its turn is cancelled, whether or not the plugin reads the token.</summary>
    [Fact]
    public async Task ACallThatIgnoresItsToken_StillReturnsWhenTheTokenFires()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness(), new HarnessOptions { StopTimeout = TimeSpan.FromSeconds(1) });
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => plugin.CallAsync("stuck", ct: soon.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>A Stop that never returns costs its time limit, not the rest of the run.</summary>
    [Fact]
    public async Task AStopThatHangs_IsAbandonedAtItsLimit()
    {
        var options = With("stopHangs") with { StopTimeout = TimeSpan.FromSeconds(1) };
        var plugin = await PluginHarness.LoadAsync(Folders.Harness(), options);

        await plugin.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("did not return", plugin.StopError);
    }

    /// <summary>
    /// The lifetime ends BEFORE Stop is awaited, as in a session: a plugin whose Stop waits for a loop
    /// that exits on its lifetime stops cleanly, rather than waiting out the limit.
    /// </summary>
    [Fact]
    public async Task AStopThatWaitsOnItsLifetime_Returns()
    {
        var plugin = await PluginHarness.LoadAsync(Folders.Harness(), With("stopWaitsOnLifetime"));

        await plugin.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(plugin.StopError);
    }

    /// <summary>Check hands the plugin what a session would: a client, when the sidecar declares one.</summary>
    [Fact]
    public async Task Check_GivesADeclaredClient()
    {
        Assert.Empty(await PluginHarness.CheckAsync(Folders.Harness()));
    }

    /// <summary>A tool that throws is a failed call, as it is in a session — not an exception out of the harness.</summary>
    [Fact]
    public async Task AToolThatThrows_IsAFailedResult()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var call = await plugin.CallAsync("throws");

        Assert.False(call.Result!.Success);
        Assert.Contains("the tool threw", call.Result.ErrorMessage);
    }

    /// <summary>A Start that throws OperationCanceledException of its own is a start problem, and the plugin is still stopped.</summary>
    [Fact]
    public async Task AStartThatCancelsItself_IsALoadProblem()
    {
        var refused = await Assert.ThrowsAsync<PluginLoadException>(
            () => PluginHarness.LoadAsync(Folders.Harness(), With("startCancels")));

        Assert.Equal("start", Assert.Single(refused.Problems).Field);
    }

    /// <summary>A command name a built-in already has is refused by a session; check says so first.</summary>
    [Fact]
    public async Task ACommandWithABuiltInName_IsAProblem()
    {
        var dir = Folders.WellFormedWithSidecar("""
            {"pluginContract":2,"name":"fixture","version":"1.0.0","spawns":false,
             "tools":[{"name":"wf_tool","description":"x","inputSchema":{"type":"object"}}],
             "commands":[{"name":"model","summary":"x"}]}
            """);

        var problems = await PluginHarness.CheckAsync(dir);

        Assert.Contains(problems, p => p.Field == "commands[0].name" && p.Message.Contains("/model"));
    }

    /// <summary>Every problem: a contract-2 sidecar using both client and kind is two lines, not the first one.</summary>
    [Fact]
    public async Task ClientAndKindBelowTheirContracts_AreTwoProblems()
    {
        var dir = Folders.WellFormedWithSidecar("""
            {"pluginContract":2,"client":true,"kind":"managed","name":"fixture","version":"1.0.0","spawns":false,
             "tools":[{"name":"wf_tool","description":"x","inputSchema":{"type":"object"}}]}
            """);

        var problems = await PluginHarness.CheckAsync(dir);

        Assert.Contains(problems, p => p.Field == "client");
        Assert.Contains(problems, p => p.Field == "kind");
    }
}

/// <summary>The smaller promises: duplicate keys, an unreadable sidecar, reaping, and what the package exposes.</summary>
public class PluginHarnessEdgeTests
{
    /// <summary>Arguments are taken as a session takes them: a repeated key's last value wins.</summary>
    [Fact]
    public async Task ARepeatedArgument_TakesItsLastValue_AsASessionDoes()
    {
        await using var plugin = await PluginHarness.LoadAsync(Folders.Harness());

        var call = await plugin.CallAsync("echo", """{"text":"first","text":"last"}""", answer: GateAnswer.Allow);

        Assert.Equal("last", call.Result!.Output["content"]);
    }

    /// <summary>A sidecar the harness cannot read is a problem naming it, not a stack trace.</summary>
    [Fact]
    public async Task AnUnreadableSidecar_IsAProblem()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var dir = Folders.WellFormed();
        var sidecar = Path.Combine(dir, "fixture.plugin.json");
        File.SetUnixFileMode(sidecar, UnixFileMode.None);

        var problem = Assert.Single(await PluginHarness.CheckAsync(dir));

        Assert.Equal("sidecar", problem.Field);
        Assert.Contains("fixture.plugin.json", problem.Message);
        File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>A Start that fails after spawning still has its child reaped: the caller gets no plugin to dispose.</summary>
    [Fact]
    public async Task AStartThatFailsAfterSpawning_HasItsChildReaped()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = Folders.NewFolder("harness-work-");
        var options = new HarnessOptions
        {
            WorkingDirectory = work,
            Settings = JsonSerializer.SerializeToElement(new { spawnThenFail = true }),
        };

        await Assert.ThrowsAsync<PluginLoadException>(() => PluginHarness.LoadAsync(Folders.Harness(), options));

        var pid = int.Parse(File.ReadAllText(Path.Combine(work, "child.pid")));
        Assert.True(HasExited(pid), $"child {pid} is still running");
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.WaitForExit(2000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>A pid is only killed while it is still the process that was registered — never one that reused the number.</summary>
    [Fact]
    public void AReusedPid_IsNotTheRegisteredProcess()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.True(HarnessContext.IsStill(self.Id, self.StartTime));
        Assert.False(HarnessContext.IsStill(self.Id, self.StartTime.AddMinutes(-1)));
    }

    /// <summary>The package's public surface carries no test seams.</summary>
    [Fact]
    public void TheHostLookup_IsNotPublicApi()
    {
        Assert.Null(typeof(PluginHarness).GetMethod("HostDllForTest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }
}
