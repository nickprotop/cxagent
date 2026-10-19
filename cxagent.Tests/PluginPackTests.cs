using System.Text.Json;
using Xunit;

namespace CxAgent.Tests;

/// <summary><c>cxagent plugin pack</c>: a release zip, its catalog entry, and what to do next.</summary>
public class PluginPackTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    /// <summary>A build folder's leftovers stay out; the plugin's own dependencies go in.</summary>
    [Fact]
    public async Task TheZip_HoldsWhatAReleaseZipHolds()
    {
        var dir = Folders.Harness();
        foreach (var leftover in new[] { "x.pdb", "x.deps.json", "CxAgent.Plugins.Abstractions.dll", "CxAgent.Core.dll" })
            File.WriteAllText(Path.Combine(dir, leftover), "");
        File.WriteAllText(Path.Combine(dir, "Dependency.dll"), "");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# harness");
        var outDir = Folders.NewFolder("pack-out-");

        var (exit, _, _) = await Run("pack", dir, "--out", outDir);

        Assert.Equal(0, exit);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(outDir, "harness.zip"));
        Assert.Equal(
            ["Dependency.dll", "README.md", "cxagent.Tests.PluginFixture.Harness.dll", "cxagent.Tests.PluginFixture.Harness.plugin.json"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheEntry_MatchesTheSidecar_AndHashesTheZip()
    {
        var dir = Folders.Harness();
        var outDir = Folders.NewFolder("pack-out-");

        await Run("pack", dir, "--out", outDir, "--description", "a fixture", "--publisher", "me", "--license", "MIT", "--repository", "https://example.org/r");

        using var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "harness.catalog.json")));
        var root = entry.RootElement;
        var zipHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(outDir, "harness.zip")))).ToLowerInvariant();
        Assert.Equal("harness", root.GetProperty("name").GetString());
        Assert.Equal("<upload URL>", root.GetProperty("source").GetProperty("url").GetString());
        Assert.Equal(zipHash, root.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal("dynamic", root.GetProperty("tools")[0].GetProperty("gated").GetString());
        Assert.Equal(4, root.GetProperty("compatibility").GetProperty("pluginContract").GetInt32());
        Assert.Equal("a fixture", root.GetProperty("description").GetString());
    }

    [Fact]
    public async Task TextFields_ComeFromTheCsproj_WhenNotGiven()
    {
        var project = Folders.NewFolder("pack-proj-");
        File.WriteAllText(Path.Combine(project, "cxagent.Tests.PluginFixture.Harness.csproj"),
            "<Project><PropertyGroup><Description>from the csproj</Description><Authors>someone</Authors></PropertyGroup></Project>");
        var dir = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Folders.Harness())) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var outDir = Folders.NewFolder("pack-out-");

        var (_, output, _) = await Run("pack", dir, "--out", outDir);

        using var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "harness.catalog.json")));
        Assert.Equal("from the csproj", entry.RootElement.GetProperty("description").GetString());
        Assert.Equal("someone", entry.RootElement.GetProperty("publisher").GetString());
        Assert.Equal("<fill in: repository>", entry.RootElement.GetProperty("repository").GetString());
        Assert.Contains(output.Split('\n'), line => line.StartsWith("still to fill in:", StringComparison.Ordinal) && line.Contains("repository"));
    }

    [Fact]
    public async Task APluginCheckRefuses_IsNotPacked()
    {
        var dir = Folders.WellFormedWithSidecar("""{"pluginContract":2,"kind":"native","name":"fixture","version":"1.0.0","spawns":false,"tools":[]}""");
        var outDir = Folders.NewFolder("pack-out-");

        var (exit, _, _) = await Run("pack", dir, "--out", outDir);

        Assert.Equal(1, exit);
        Assert.Empty(Directory.GetFiles(outDir));
    }

    [Fact]
    public async Task ANativePlugin_IsOneZipPerPlatform_WithSources()
    {
        if (NativeFixtures.Path("fixture-reports") is null) return;
        var dir = Folders.NativeReports();
        var lib = Path.Combine(dir, "fixture-reports" + NativeFixtures.Suffix);
        var outDir = Folders.NewFolder("pack-out-");

        var (exit, output, _) = await Run("pack", dir, "--native", $"{lib}=linux-x64", "--native", $"{lib}=linux-arm64", "--out", outDir);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(outDir, "fixture-linux-x64.zip")));
        Assert.True(File.Exists(Path.Combine(outDir, "fixture-linux-arm64.zip")));
        using var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "fixture.catalog.json")));
        Assert.Equal(["linux-x64", "linux-arm64"], entry.RootElement.GetProperty("compatibility").GetProperty("platforms").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("gh release create v1.0.0 fixture-*.zip", output);
    }

    [Fact]
    public async Task AnUnknownPlatform_IsUsage() =>
        Assert.Equal(2, (await Run("pack", Folders.Harness(), "--native", "x.so=solaris-sparc")).Exit);

    [Fact]
    public async Task TheNextSteps_AreFilledInForThePlugin()
    {
        var (exit, output, _) = await Run("pack", Folders.Harness(), "--out", Folders.NewFolder("pack-out-"));

        Assert.Equal(0, exit);
        Assert.Contains("gh release create v1.0.0 harness.zip", output);
        Assert.Contains("harness.catalog.json", output);
        Assert.Contains("cxagent plugin verify harness.catalog.json", output);
        Assert.Contains("https://github.com/nickprotop/cxagent/issues/new/choose", output);
    }
}
