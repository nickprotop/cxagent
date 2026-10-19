using System.Text.Json;
using CxAgent.Cli;
using Xunit;

namespace CxAgent.Tests;

/// <summary>Runs <c>cxagent plugin …</c> in process, capturing what it prints and its exit code.</summary>
internal static class CliRun
{
    public sealed record Outcome(int Exit, string Out, string Err);

    public static async Task<Outcome> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = await PluginCli.RunAsync(args, o, e);
        return new Outcome(exit, o.ToString(), e.ToString());
    }
}

/// <summary><c>cxagent plugin check</c>: 0 when the folder loads, 1 naming each problem, 2 for a usage error.</summary>
public class PluginCliCheckTests
{
    private const string Broken = """{"pluginContract":2,"kind":"native","name":"fixture","version":"1.0.0","spawns":false,"tools":[{"name":"read_file","description":"x","inputSchema":{"type":"object"}}]}""";

    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    [Fact] public async Task NoVerb_IsUsage() => Assert.Equal(2, (await Run()).Exit);
    [Fact] public async Task AnUnknownVerb_IsUsage() => Assert.Equal(2, (await Run("frobnicate")).Exit);
    [Fact] public async Task CheckWithoutAFolder_IsUsage() => Assert.Equal(2, (await Run("check")).Exit);

    [Fact]
    public async Task AGoodPlugin_ExitsZero_AndSaysWhatItIs()
    {
        var dir = Folders.WellFormed();
        var (exit, output, _) = await Run("check", dir);
        Assert.Equal(0, exit);
        Assert.Contains("ok", output);
    }

    [Fact]
    public async Task ABrokenPlugin_ExitsOne_OneLinePerProblem()
    {
        var dir = Folders.WellFormedWithSidecar(Broken);
        var (exit, output, _) = await Run("check", dir);
        Assert.Equal(1, exit);
        Assert.Equal(3, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Contains(": ")));
    }

    [Fact]
    public async Task Json_CarriesTheSameProblems()
    {
        var dir = Folders.WellFormedWithSidecar(Broken);
        var (exit, output, _) = await Run("check", dir, "--json");
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(1, exit);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("problems").GetArrayLength());
    }
}

/// <summary><c>cxagent plugin call</c>: one real call, its gate, its reports, its result, and the exit code a script reads.</summary>
public class PluginCliCallTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("oops")]
    public async Task ArgumentsThatAreNotAnObject_AreAUsageError(string json)
    {
        var (exit, _, err) = await Run("call", Folders.WellFormed(), "wf_tool", json);

        Assert.Equal(2, exit);
        Assert.Contains("JSON object", err);
    }

    [Fact]
    public async Task BothSettingsForms_AreAUsageError()
    {
        var (exit, _, _) = await Run("call", Folders.WellFormed(), "wf_tool", "--settings", "{}", "--settings-file", "s.json");
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task AnUnknownAnswer_IsAUsageError()
    {
        var (exit, _, _) = await Run("call", Folders.Harness(), "echo", "--answer", "maybe");
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task ASucceedingCall_ExitsZero_AndPrintsTheResult()
    {
        var (exit, output, _) = await Run("call", Folders.WellFormed(), "wf_tool");

        Assert.Equal(0, exit);
        Assert.Contains("\"success\": true", output);
    }

    [Fact]
    public async Task AnUnansweredGate_StopsThere_ExitZero_SayingAUserWouldBeAsked()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "echo", """{"text":"hi"}""");

        Assert.Equal(0, exit);
        Assert.Contains("asks: echo hi", output);
        Assert.Contains("stopped: a user would be asked", output);
        Assert.DoesNotContain("success", output);
    }

    [Fact]
    public async Task AnAllowedCall_PrintsItsReportsInOrder_ThenTheResult()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "echo", """{"text":"hi"}""", "--answer", "allow");

        Assert.Equal(0, exit);
        var progress = output.IndexOf("progress 50% half", StringComparison.Ordinal);
        var log = output.IndexOf("log [Info] working", StringComparison.Ordinal);
        var result = output.IndexOf("\"success\": true", StringComparison.Ordinal);
        Assert.True(progress >= 0 && log > progress && result > log, output);
    }

    [Fact]
    public async Task NativeReports_ArePrintedInOrder()
    {
        if (NativeFixtures.Path("fixture-reports") is null) return;

        var (exit, output, _) = await Run("call", Folders.NativeReports(), "echo");

        Assert.Equal(0, exit);
        Assert.True(output.IndexOf("progress 50% half", StringComparison.Ordinal)
                    < output.IndexOf("log [Info] working", StringComparison.Ordinal), output);
        Assert.Contains("one two", output);
    }

    [Fact]
    public async Task ASubmit_IsPrinted_AsATurnThatWouldStart()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "submit");

        Assert.Equal(0, exit);
        Assert.Contains("would start a turn: tidy the folder", output);
    }

    [Fact]
    public async Task ACallPastItsTimeout_StopsAndFails()
    {
        var (exit, _, err) = await Run("call", Folders.Harness(), "slow", "--timeout", "1");

        Assert.Equal(1, exit);
        Assert.Contains("timed out after 1 s", err);
    }

    [Fact]
    public async Task ALoadFailure_ExitsOne_NamingEachProblem()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "echo", "--settings", """{"failStart":true}""");

        Assert.Equal(1, exit);
        Assert.Contains("start: ", output);
    }

    [Fact]
    public async Task ACommand_PrintsItsMessage()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "--command", "harness-ping", "now");

        Assert.Equal(0, exit);
        Assert.Contains("pong now", output);
    }

    [Fact]
    public async Task Json_CarriesGateReportsAndResult()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "echo", """{"text":"hi"}""", "--answer", "allow", "--json");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(0, exit);
        Assert.Equal("echo hi", root.GetProperty("gate").GetProperty("display").GetString());
        Assert.Equal("allow", root.GetProperty("answer").GetString());
        Assert.Equal(3, root.GetProperty("reports").GetArrayLength());
        Assert.True(root.GetProperty("result").GetProperty("success").GetBoolean());
    }
}

/// <summary><c>cxagent plugin call</c> when the plugin or the command line misbehaves: an exit code, never a crash.</summary>
public class PluginCliCallMisbehaviourTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e30")]
    public async Task ATimeoutThatIsNotAUsableNumber_IsUsage(string timeout)
    {
        var (exit, _, _) = await Run("call", Folders.Harness(), "echo", "--timeout", timeout);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task ASettingsFileThatIsNotAnObject_IsUsage()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "[1,2]");

        var (exit, _, err) = await Run("call", Folders.Harness(), "echo", "--settings-file", file);

        Assert.Equal(2, exit);
        Assert.Contains("JSON object", err);
        File.Delete(file);
    }

    [Fact]
    public async Task ACommandThatThrows_ExitsOne()
    {
        var (exit, _, err) = await Run("call", Folders.Harness(), "--command", "harness-throw");

        Assert.Equal(1, exit);
        Assert.Contains("the command threw", err);
    }

    [Fact]
    public async Task AToolThatThrows_ExitsOne_WithTheFailure()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "throws");

        Assert.Equal(1, exit);
        Assert.Contains("the tool threw", output);
    }

    [Fact]
    public async Task ACallDeafToItsToken_StillStopsAtTheTimeout()
    {
        var run = Run("call", Folders.Harness(), "stuck", "--timeout", "1");

        var (exit, _, err) = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, exit);
        Assert.Contains("timed out after 1 s", err);
    }

    /// <summary>A result is printed as written: "21°C", not "21°C".</summary>
    [Fact]
    public async Task NonAsciiText_IsPrintedAsIs()
    {
        var (exit, output, _) = await Run("call", Folders.Harness(), "echo", """{"text":"21°C Αθήνα"}""", "--answer", "allow");

        Assert.Equal(0, exit);
        Assert.Contains("\"21°C Αθήνα\"", output);
    }
}
