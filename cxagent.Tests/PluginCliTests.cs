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
