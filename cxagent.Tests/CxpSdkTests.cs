using System.Text.Json;
using CxAgent.Core.Jobs;
using CxAgent.Plugins.Testing;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The C SDK (cxp) through the real plugin host: a fixture written with it, built by cxp_add_plugin,
/// driven by the plugin harness exactly as a session would drive it.
/// </summary>
public class CxpSdkTests
{
    private static string? Folder() => NativeFixtures.Path("cxp-fixture") is { } lib ? FolderOf(lib) : null;

    // THE LIBRARY AND THE SIDECAR cxp_add_plugin COPIED BESIDE IT, in a folder of their own.
    private static string FolderOf(string lib)
    {
        var dir = Folders.NewFolder("cxp-");
        File.Copy(lib, Path.Combine(dir, Path.GetFileName(lib)));
        File.Copy(Path.ChangeExtension(lib, null) + ".plugin.json", Path.Combine(dir, "cxp-fixture.plugin.json"));
        return dir;
    }

    private static string? Content(HarnessCall call) => call.Result?.Output["content"]?.ToString();

    /// <summary>
    /// The tests below skip without the fixture; on a machine with CMake, a missing fixture is a broken
    /// build, not a reason to skip — otherwise every test here would "pass" having run nothing.
    /// </summary>
    [Fact]
    public void TheFixture_IsBuilt_WhereCMakeIs()
    {
        var hasCmake = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? "cmake.exe" : "cmake")));
        if (!hasCmake) return;

        Assert.NotNull(NativeFixtures.Path("cxp-fixture"));
    }

    [Fact]
    public async Task TheEmbeddedSidecar_IsWhatDescribeReturns()
    {
        if (Folder() is not { } dir) return;
        Assert.Empty(await PluginHarness.CheckAsync(dir));
    }

    [Fact]
    public async Task AToolReturnsText()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        Assert.Equal("echo: hi", Content(await plugin.CallAsync("echo", new { text = "hi" }, answer: GateAnswer.Allow)));
    }

    [Fact]
    public async Task AMissingArgument_IsTheToolsError()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("echo", answer: GateAnswer.Allow);

        Assert.False(call.Result!.Success);
        Assert.Equal("text is required", call.Result.ErrorMessage);
    }

    [Fact]
    public async Task ArgumentsOfTheWrongType_GiveTheFallback()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        Assert.Equal("-1 -1 none", Content(await plugin.CallAsync("typed", new { n = "x", b = 3 })));
        Assert.Equal("2.5 1 json", Content(await plugin.CallAsync("typed", new { n = 2.5, b = true, o = new { a = 1 } })));
    }

    [Fact]
    public async Task ASetting_IsReadFromStart()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir,
            new HarnessOptions { Settings = JsonSerializer.SerializeToElement(new { greeting = "hello" }) });

        Assert.Equal("hello", Content(await plugin.CallAsync("setting")));
    }

    [Fact]
    public async Task Gates_AskAsWritten()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var once = await plugin.CallAsync("echo", new { text = "hi" });
        Assert.Equal("echo hi", once.Gate!.Display);
        Assert.True(once.Gate.AlwaysAskable);
        Assert.False((await plugin.CallAsync("always", new { text = "hi" })).Gate!.AlwaysAskable);
        Assert.Null((await plugin.CallAsync("typed")).Gate);
    }

    [Fact]
    public async Task Reports_ArriveInOrder()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("reports");

        Assert.Equal(
            [new HarnessReport.Progress(50, "half"), new HarnessReport.Log(JobLogLevel.Info, "working"),
             new HarnessReport.Stream("one "), new HarnessReport.Stream("two")],
            call.Reports);
    }

    [Fact]
    public async Task AToolReturningNothing_IsAFailedCall()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("nothing");

        Assert.False(call.Result!.Success);
        Assert.Contains("nothing", call.Result.ErrorMessage);
    }

    [Fact]
    public async Task ASubmit_ReachesTheClient()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        await plugin.CallAsync("submits");
        for (var i = 0; i < 50 && plugin.Submits.Count == 0; i++) await Task.Delay(100);

        Assert.Equal(["tidy up"], plugin.Submits);
    }

    [Fact]
    public async Task ACommand_Runs()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        Assert.Equal("pong now", (await plugin.CommandAsync("cxp-ping", "now")).Message);
    }

    [Fact]
    public async Task AReportFromAnotherThread_AfterTheCall_IsDropped()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("late");
        await Task.Delay(500);

        // A REAL ID WAS COPIED: an empty one would be dropped by the SDK itself, and this test would
        // pass without the host's drop ever running.
        Assert.Matches(@"^started \d+$", Content(call));
        Assert.DoesNotContain(call.Reports, r => r is HarnessReport.Progress { Percent: 99 });
        Assert.True((await plugin.CallAsync("typed")).Result!.Success);
    }

    /// <summary>Every string cxp hands the host is freed exactly once, and each call's cxp_format strings with it.</summary>
    [Fact]
    public async Task AThousandCalls_LeakNothing()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        for (var i = 0; i < 1000; i++) await plugin.CallAsync("echo", new { text = "x" }, answer: GateAnswer.Allow);

        // COUNTED BEFORE THE outstanding CALL'S OWN ENVELOPE IS HANDED OVER: every earlier string freed is 0.
        Assert.Equal("0", Content(await plugin.CallAsync("outstanding")));
    }

    /// <summary>A "dynamic" tool registered with no gate function asks — the sidecar said its calls may need permission.</summary>
    [Fact]
    public async Task ADynamicToolWithNoGateFunction_Asks()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("nogate", new { text = "hi" });

        Assert.NotNull(call.Gate);
        Assert.Contains("nogate", call.Gate!.Display);
    }

    /// <summary>cxp_ok_json with something other than an object is the author's mistake, said as a failed call — not empty output.</summary>
    [Fact]
    public async Task AnOkJsonThatIsNotAnObject_IsAFailedCall()
    {
        if (Folder() is not { } dir) return;
        await using var plugin = await PluginHarness.LoadAsync(dir);

        var call = await plugin.CallAsync("badjson");

        Assert.False(call.Result!.Success);
        Assert.Contains("object", call.Result.ErrorMessage);
    }
}
