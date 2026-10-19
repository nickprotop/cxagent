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

    private static async Task<JsonElement> EntryAfter(params string[] args)
    {
        var outDir = Folders.NewFolder("pack-out-");
        var (exit, _, err) = await Run(["pack", .. args, "--out", outDir]);
        Assert.True(exit == 0, err);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "harness.catalog.json")));
        return doc.RootElement.Clone();
    }

    private static string InfoFile(string name, string text)
    {
        var path = Path.Combine(Folders.NewFolder("pack-info-"), name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task AJsonInfoFile_FillsTheEntry()
    {
        var info = InfoFile("info.json", """{"description":"from json","publisher":"pj","license":"Apache-2.0","repository":"https://example.org/j"}""");

        var entry = await EntryAfter(Folders.Harness(), "--info", info);

        Assert.Equal("from json", entry.GetProperty("description").GetString());
        Assert.Equal("pj", entry.GetProperty("publisher").GetString());
        Assert.Equal("Apache-2.0", entry.GetProperty("license").GetString());
        Assert.Equal("https://example.org/j", entry.GetProperty("repository").GetString());
    }

    [Fact]
    public async Task AYamlInfoFile_FillsTheEntry()
    {
        var info = InfoFile("info.yaml", "description: from yaml\npublisher: py\nlicense: MIT\nrepository: \"https://example.org/y\"\n");

        var entry = await EntryAfter(Folders.Harness(), "--info", info);

        Assert.Equal("from yaml", entry.GetProperty("description").GetString());
        Assert.Equal("https://example.org/y", entry.GetProperty("repository").GetString());
    }

    [Fact]
    public async Task AnOption_OverridesTheInfoFile()
    {
        var info = InfoFile("info.json", """{"description":"from the file"}""");

        var entry = await EntryAfter(Folders.Harness(), "--info", info, "--description", "from the option");

        Assert.Equal("from the option", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task TheInfoFile_OverridesTheCsproj()
    {
        var project = Folders.NewFolder("pack-proj-");
        File.WriteAllText(Path.Combine(project, "cxagent.Tests.PluginFixture.Harness.csproj"),
            "<Project><PropertyGroup><Description>from the csproj</Description><Authors>csproj author</Authors></PropertyGroup></Project>");
        var dir = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Folders.Harness())) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var info = InfoFile("info.yml", "description: from the file\n");

        var entry = await EntryAfter(dir, "--info", info);

        Assert.Equal("from the file", entry.GetProperty("description").GetString());
        Assert.Equal("csproj author", entry.GetProperty("publisher").GetString());
    }

    [Theory]
    [InlineData("info.json", """{"licence":"MIT"}""")]
    [InlineData("info.yaml", "licence: MIT\n")]
    [InlineData("info.json", """{"description":5}""")]
    [InlineData("info.txt", "description: x\n")]
    [InlineData("info.json", "not json")]
    public async Task AnInfoFileItCannotUse_IsUsage(string name, string text)
    {
        var (exit, _, _) = await Run("pack", Folders.Harness(), "--info", InfoFile(name, text), "--out", Folders.NewFolder("pack-out-"));
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task AnInfoFileThatIsNotThere_IsUsage() =>
        Assert.Equal(2, (await Run("pack", Folders.Harness(), "--info", "/no/such/info.json")).Exit);

    /// <summary>Everything at the top of the checked folder ships, except build leftovers — a native library or a data file the plugin reads included.</summary>
    [Fact]
    public async Task TopLevelFilesThePluginNeeds_AreKept()
    {
        var dir = Folders.Harness();
        File.WriteAllText(Path.Combine(dir, "libhelper.so"), "");
        File.WriteAllText(Path.Combine(dir, "cities.txt"), "");
        File.WriteAllText(Path.Combine(dir, "cxagent.Tests.PluginFixture.Harness.xml"), "");
        var outDir = Folders.NewFolder("pack-out-");

        await Run("pack", dir, "--out", outDir);

        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(outDir, "harness.zip"));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("libhelper.so", names);
        Assert.Contains("cities.txt", names);
        Assert.DoesNotContain("cxagent.Tests.PluginFixture.Harness.xml", names);
    }

    /// <summary>
    /// A native library built as libweather.so is stored under the sidecar's name, because the loader
    /// finds the sidecar from the library's own name.
    /// </summary>
    [Fact]
    public async Task ANativeLibraryNamedDifferently_IsStoredUnderTheSidecarsName()
    {
        if (NativeFixtures.Path("fixture-reports") is null) return;
        var dir = Folders.NativeReports();
        var built = Path.Combine(Folders.NewFolder("pack-lib-"), "libfixture" + NativeFixtures.Suffix);
        File.Copy(Path.Combine(dir, "fixture-reports" + NativeFixtures.Suffix), built);
        var outDir = Folders.NewFolder("pack-out-");

        var (exit, _, _) = await Run("pack", dir, "--native", $"{built}=linux-x64", "--out", outDir);

        Assert.Equal(0, exit);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(outDir, "fixture-linux-x64.zip"));
        Assert.Contains("fixture-reports" + NativeFixtures.Suffix, zip.Entries.Select(e => e.FullName));
        using var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "fixture.catalog.json")));
        Assert.Equal("fixture-reports" + NativeFixtures.Suffix,
            entry.RootElement.GetProperty("sources").GetProperty("linux-x64").GetProperty("file").GetString());
    }
}
