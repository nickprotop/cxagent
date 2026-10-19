using CxAgent.Cli;
using CxAgent.Core.Plugins;
using CxAgent.Plugins.Testing;
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

    /// <summary>The license is the author's choice; the template leaves it for them, as it does the description.</summary>
    [Fact]
    public async Task TheLicense_IsLeftForTheAuthor()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "weather");
        await Run("new", "weather", "--lang", "csharp", "--dir", dir);

        Assert.Contains("<PackageLicenseExpression></PackageLicenseExpression>", File.ReadAllText(Path.Combine(dir, "weather.csproj")));
    }

    [Fact]
    public async Task ADirThatIsAFile_IsUsage()
    {
        var file = Path.Combine(Folders.NewFolder("new-"), "taken");
        File.WriteAllText(file, "");

        Assert.Equal(2, (await Run("new", "weather", "--lang", "csharp", "--dir", file)).Exit);
    }

    /// <summary>A refused name writes nothing — not even the folder.</summary>
    [Fact]
    public async Task ARefusedName_WritesNothing()
    {
        var dir = Path.Combine(Folders.NewFolder("new-"), "target");

        await Run("new", "My Plugin", "--lang", "csharp", "--dir", dir);

        Assert.False(Directory.Exists(dir));
    }
}

/// <summary><c>cxagent plugin new --lang c</c>: the wordcount template with the C SDK beside it, building and loading.</summary>
public class PluginNewCTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    private static readonly string[] Tokens = ["CXP_NAME", "CXP_ROOT_NAMESPACE", "CXP_ABSTRACTIONS_VERSION", "CXP_TESTING_VERSION"];

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? tool + ".exe" : tool)));

    private static async Task<string> NewProject(string name)
    {
        var dir = Path.Combine(Folders.NewFolder("newc-"), name);
        var (exit, _, err) = await Run("new", name, "--lang", "c", "--dir", dir);
        Assert.True(exit == 0, err);
        return dir;
    }

    private static (int Exit, string Output) Cmake(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmake") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public async Task ANewCPlugin_IsWritten_WithTheSdkBesideIt()
    {
        var dir = await NewProject("wordcount");

        foreach (var file in new[] { "CMakeLists.txt", "wordcount.c", "wordcount.plugin.json", "README.md", ".gitignore",
                                     ".github/workflows/build.yml", "cxp/cxp.h", "cxp/cxp.c", "cxp/cxp.cmake",
                                     "cxp/cJSON.h", "cxp/cJSON.c", "cxp/cJSON-LICENSE", "cxp/cxagent_plugin.h" })
            Assert.True(File.Exists(Path.Combine(dir, file)), file);
        var ours = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "cxp" + Path.DirectorySeparatorChar));
        foreach (var file in ours)
            foreach (var token in Tokens)
                Assert.DoesNotContain(token, File.ReadAllText(file));
    }

    [Fact]
    public async Task ANewCPlugin_Builds_Loads_AndCounts()
    {
        if (!OnPath("cmake")) return;
        var dir = await NewProject("wordcount");
        Assert.Equal(0, Cmake(dir, "-B", "build").Exit);
        var (built, output) = Cmake(dir, "--build", "build");
        Assert.True(built == 0, output);

        Assert.Empty(await PluginHarness.CheckAsync(Path.Combine(dir, "build")));
        await using var plugin = await PluginHarness.LoadAsync(Path.Combine(dir, "build"), new HarnessOptions { WorkingDirectory = dir });
        var call = await plugin.CallAsync("wordcount_count", new { file = "CMakeLists.txt" });

        Assert.Null(call.Gate);
        Assert.True(call.Result!.Success, call.Result.ErrorMessage);
        Assert.Matches(@"^CMakeLists\.txt: \d+ lines, \d+ words$", call.Result.Output["content"]?.ToString());
        Assert.NotEmpty(call.Progress);
    }

    [Fact]
    public async Task AFileOutsideTheWorkingFolder_Asks()
    {
        if (!OnPath("cmake")) return;
        var dir = await NewProject("wordcount");
        Cmake(dir, "-B", "build");
        Cmake(dir, "--build", "build");
        var outside = Path.Combine(Folders.NewFolder("outside-"), "notes.txt");
        File.WriteAllText(outside, "one two\nthree\n");
        await using var plugin = await PluginHarness.LoadAsync(Path.Combine(dir, "build"), new HarnessOptions { WorkingDirectory = dir });

        var call = await plugin.CallAsync("wordcount_count", new { file = outside });

        Assert.Contains("outside the working folder", call.Gate!.Display);
        Assert.True(call.StoppedAtGate);
    }

    /// <summary>The running count is streamed once per thousand lines — not again for each blank line that follows one.</summary>
    [Fact]
    public async Task TheRunningCount_IsStreamedOncePerThousand()
    {
        if (!OnPath("cmake")) return;
        var dir = await NewProject("wordcount");
        Cmake(dir, "-B", "build");
        Cmake(dir, "--build", "build");
        File.WriteAllText(Path.Combine(dir, "lines.txt"),
            string.Concat(Enumerable.Repeat("word\n", 1000)) + "\n\n\n" + string.Concat(Enumerable.Repeat("word\n", 1000)));
        await using var plugin = await PluginHarness.LoadAsync(Path.Combine(dir, "build"), new HarnessOptions { WorkingDirectory = dir });

        var call = await plugin.CallAsync("wordcount_count", new { file = "lines.txt" });

        Assert.Equal("1000 lines… 2000 lines… ", call.Streamed);
    }

    /// <summary>A sidecar cxp.cmake cannot read fails the configure, naming it — never a library that describes nothing.</summary>
    [Fact]
    public async Task ASidecarCMakeCannotRead_FailsTheConfigure()
    {
        if (!OnPath("cmake")) return;
        var dir = await NewProject("wordcount");
        File.WriteAllText(Path.Combine(dir, "wordcount.plugin.json"), """{"name":"wordcount","version":"1.0.0"}""");

        var (exit, output) = Cmake(dir, "-B", "build");

        Assert.NotEqual(0, exit);
        Assert.Contains("wordcount.plugin.json", output);
    }
}

