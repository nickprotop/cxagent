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
