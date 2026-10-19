using CxAgent.Cli;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary><c>cxagent plugin new</c>: a working project from the template, never over anything.</summary>
public class PluginNewTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    [Fact]
    public async Task ANewPlugin_IsWritten_WithItsNameFilledIn()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");

        var (exit, output, _) = await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        Assert.Equal(0, exit);
        foreach (var file in new[] { "weather.csproj", "weather.plugin.json", "Plugin.cs", "README.md", ".gitignore",
                                     "tests/weather.Tests.csproj", "tests/PluginTests.cs" })
            Assert.True(File.Exists(Path.Combine(dir, file)), file);
        Assert.False(Directory.Exists(Path.Combine(dir, ".template.config")));
        var every = string.Concat(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("CXP_", every);
        Assert.Contains("cd ", output);
    }

    [Fact]
    public async Task TheSidecarItWrites_Parses_AndGatesPerCall()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        var parsed = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, "weather.plugin.json")));

        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Errors));
        Assert.Equal(PluginGating.Dynamic, Assert.Single(parsed.Manifest!.Tools).Gated);
    }

    [Fact]
    public async Task TheVersionsItPins_AreThisBuildsPackages()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        var values = PluginTemplate.ValuesForTest("weather");
        Assert.Contains($"Version=\"{values.AbstractionsVersion}\"", File.ReadAllText(Path.Combine(dir, "weather.csproj")));
        Assert.Contains($"Version=\"{values.TestingVersion}\"", File.ReadAllText(Path.Combine(dir, "tests", "weather.Tests.csproj")));
        Assert.Matches(@"^\d+\.\d+\.\d+", values.AbstractionsVersion);
    }

    [Fact]
    public async Task ANonEmptyFolder_IsRefused_AndUntouched()
    {
        var dir = Folders.NewFolder("new-");
        File.WriteAllText(Path.Combine(dir, "mine.txt"), "keep me");

        var (exit, _, err) = await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        Assert.Equal(2, exit);
        Assert.Contains("not empty", err);
        Assert.Equal(["mine.txt"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("My Plugin")]
    [InlineData("Weather")]
    [InlineData("1weather")]
    [InlineData("")]
    public async Task AnInvalidName_IsRefused(string name)
    {
        var (exit, _, _) = await Run("new", name, "--lang", "csharp", "--dir", Folders.NewFolder("new-"));
        Assert.Equal(2, exit);
    }

    [Fact] public async Task AnUnknownLanguage_IsUsage() => Assert.Equal(2, (await Run("new", "weather", "--lang", "cobol")).Exit);

    /// <summary>C arrives with the C SDK; until then it is named, not silently ignored.</summary>
    [Fact]
    public async Task C_IsNotYetAvailable_AndSaysSo()
    {
        var (exit, _, err) = await Run("new", "weather", "--lang", "c", "--dir", Folders.NewFolder("new-"));
        Assert.Equal(2, exit);
        Assert.Contains("not available yet", err);
    }

    /// <summary>
    /// The project leaves the catalog's description empty rather than holding sample text: pack then
    /// writes a placeholder that verify refuses, where sample text would pass into a listing as if
    /// someone had written it.
    /// </summary>
    [Fact]
    public async Task TheDescription_IsLeftForTheAuthor()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        Assert.Contains("<Description></Description>", File.ReadAllText(Path.Combine(dir, "weather.csproj")));
    }

    /// <summary>A dashed name stays dashed everywhere a name is meant, and becomes a namespace only where one is.</summary>
    [Fact]
    public async Task ADashedName_KeepsItsDash_ExceptInTheNamespace()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "my-tool");
        await Run("new", "my-tool", "--lang", "csharp", "--dir", dir);

        var csproj = File.ReadAllText(Path.Combine(dir, "my-tool.csproj"));
        Assert.Contains("<AssemblyName>my-tool</AssemblyName>", csproj);
        Assert.Contains("<RootNamespace>my_tool</RootNamespace>", csproj);
        Assert.Contains("../my-tool.csproj", File.ReadAllText(Path.Combine(dir, "tests", "my-tool.Tests.csproj")));
    }

    /// <summary>A package the author adds must reach bin/, where the loader looks for it.</summary>
    [Fact]
    public async Task TheProject_CopiesItsPackagesBesideThePlugin()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        Assert.Contains("<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>", File.ReadAllText(Path.Combine(dir, "weather.csproj")));
    }

    /// <summary>The tests load the build they were built with: under -c Release, bin/Release, not a stale bin/Debug.</summary>
    [Fact]
    public async Task TheTests_LoadTheirOwnConfigurationsBuild()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        var tests = File.ReadAllText(Path.Combine(dir, "tests", "PluginTests.cs"));
        Assert.DoesNotContain("\"Debug\"", tests);
    }
}
