using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Plugins.Testing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Plugin folders for the harness tests, each a fresh temp folder the test may change.</summary>
internal static class Folders
{
    public static string Of(string assemblyStem, string folderName)
    {
        var dir = Directory.CreateTempSubdirectory("harness-").FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, assemblyStem + ".dll"), Path.Combine(dir, folderName + ".dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, assemblyStem + ".plugin.json"), Path.Combine(dir, folderName + ".plugin.json"));
        return dir;
    }

    public static string WellFormed() => Of("cxagent.Tests.PluginFixture", "fixture");

    // THE ASSEMBLY'S OWN NAME FOR THE COPY: the fixture's Load uses FromSidecar, which reads
    // <assembly-name>.plugin.json — a renamed copy would have no sidecar its own code can find.
    public static string Harness() => Of("cxagent.Tests.PluginFixture.Harness", "cxagent.Tests.PluginFixture.Harness");

    public static string NativeReports()
    {
        var dir = Directory.CreateTempSubdirectory("harness-native-").FullName;
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
        var dir = Directory.CreateTempSubdirectory("harness-").FullName;
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
