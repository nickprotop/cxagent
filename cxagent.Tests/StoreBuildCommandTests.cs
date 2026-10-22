using CxAgent.Cli;
using Xunit;

namespace CxAgent.Tests;

/// <summary><c>cxagent store build</c>: a folder of built plugins becomes one catalog verify accepts.</summary>
// IN THE working-directory COLLECTION: the printed paths are relative to the process directory, as pack's are.
[Collection("working-directory")]
public class StoreBuildCommandTests
{
    private const string Info = """{ "description": "d", "publisher": "p", "license": "MIT", "repository": "https://example/r" }""";

    private readonly string _plugins = Folders.NewFolder("store-plugins-");
    private readonly string _out = Path.Combine(Folders.NewFolder("store-out-"), "site");
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public StoreBuildCommandTests()
    {
        Add("calc", Folders.WellFormed());
        Add("harness", Folders.Harness());
    }

    private void Add(string sub, string built)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_plugins, sub)).FullName;
        foreach (var f in Directory.GetFiles(built)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(dir, "pack-info.json"), Info);
    }

    [Fact]
    public async Task TwoPlugins_MakeOneCatalog_ThatVerifyAccepts()
    {
        var code = await StoreBuildCommand.RunAsync([_plugins, "--url", "https://tools.example/cx", "--out", _out], _stdout, _stderr, default);
        Assert.Equal(0, code);
        var json = File.ReadAllText(Path.Combine(_out, "catalog.json"));
        var claims = CatalogClaims.Read(json, out var error);
        Assert.Null(error);
        Assert.Equal(2, claims!.Count);
        Assert.All(claims, c => Assert.Empty(c.Shape));
        Assert.All(claims, c => Assert.StartsWith("https://tools.example/cx/", Assert.Single(c.Downloads).Url));
        Assert.Contains("/plugin store add <name> https://tools.example/cx/catalog.json", _stdout.ToString());
    }

    [Fact]
    public async Task AnUnfilledField_WritesNoCatalog()
    {
        File.Delete(Path.Combine(_plugins, "calc", "pack-info.json"));
        var code = await StoreBuildCommand.RunAsync([_plugins, "--url", "https://t/cx", "--out", _out], _stdout, _stderr, default);
        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(_out, "catalog.json")));
        Assert.Contains("calc", _stderr.ToString());
    }

    [Fact]
    public async Task APluginThatFailsCheck_WritesNoCatalog()
    {
        Directory.CreateDirectory(Path.Combine(_plugins, "broken"));
        File.WriteAllText(Path.Combine(_plugins, "broken", "broken.plugin.json"), "{ not json");
        var code = await StoreBuildCommand.RunAsync([_plugins, "--url", "https://t/cx", "--out", _out], _stdout, _stderr, default);
        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(_out, "catalog.json")));
        Assert.Contains("broken", _stderr.ToString());
    }

    [Fact]
    public async Task TwoPluginsWithOneName_AreRefused()
    {
        Add("again", Folders.Harness());
        var code = await StoreBuildCommand.RunAsync([_plugins, "--url", "https://t/cx", "--out", _out], _stdout, _stderr, default);
        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(_out, "catalog.json")));
        Assert.Contains("two plugins are named 'harness'", _stderr.ToString());
    }

    private string[] Args => [_plugins, "--url", "https://t/cx", "--out", _out];

    private string[] Snapshot() => Directory.Exists(_out)
        ? [.. Directory.GetFileSystemEntries(_out, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => f + (File.Exists(f) ? ":" + Convert.ToHexString(File.ReadAllBytes(f)).GetHashCode() : ""))]
        : [];

    [Fact]
    public async Task AFailedBuild_LeavesTheStoreThatWasThere_Untouched()
    {
        Assert.Equal(0, await StoreBuildCommand.RunAsync(Args, _stdout, _stderr, default));
        var before = Snapshot();

        // A's zip would differ if it were rewritten; B cannot be built.
        File.WriteAllText(Path.Combine(_plugins, "harness", "extra.txt"), "new");
        File.Delete(Path.Combine(_plugins, "calc", "pack-info.json"));
        Assert.Equal(1, await StoreBuildCommand.RunAsync(Args, _stdout, _stderr, default));

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task AnOutFolderThatCannotBeMade_IsAFailure_NotACrash()
    {
        var file = Path.Combine(Folders.NewFolder("store-blocker-"), "file");
        File.WriteAllText(file, "x");
        var code = await StoreBuildCommand.RunAsync([_plugins, "--url", "https://t/cx", "--out", Path.Combine(file, "site")], _stdout, _stderr, default);
        Assert.Equal(1, code);
        Assert.NotEmpty(_stderr.ToString());
    }

    [Fact]
    public async Task PrintedPaths_AreTheOutFolderAsGiven()
    {
        await StoreBuildCommand.RunAsync(Args, _stdout, _stderr, default);
        Assert.Contains($"wrote {Path.Combine(_out, "catalog.json")}", _stdout.ToString());
        Assert.Contains($"  {Path.Combine(_out, "harness.zip")}", _stdout.ToString());
    }

    [Fact]
    public void ANativePlugin_KeepsItsOwnPlatform_BesideThePlatformFolders()
    {
        var dir = Folders.NewFolder("store-native-");
        File.WriteAllText(Path.Combine(dir, "weather.plugin.json"), """{"kind":"native","name":"weather","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(dir, "weather.so"), "");
        Directory.CreateDirectory(Path.Combine(dir, "osx-arm64"));
        File.WriteAllText(Path.Combine(dir, "osx-arm64", "weather.dylib"), "");

        var builds = StoreBuildCommand.NativeBuildsOf(dir);

        Assert.Contains(builds, b => b.Rid == "osx-arm64" && b.Library.EndsWith("weather.dylib"));
        Assert.Contains(builds, b => b.Rid == System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier
                                     && b.Library == Path.Combine(dir, "weather.so"));
    }

    [Fact]
    public async Task AManagedPlugin_IgnoresAStrayPlatformFolder()
    {
        var stray = Path.Combine(_plugins, "calc", "linux-x64");
        Directory.CreateDirectory(stray);
        File.Copy(Path.Combine(_plugins, "calc", "fixture.dll"), Path.Combine(stray, "fixture.dll"), overwrite: true);
        Assert.Empty(StoreBuildCommand.NativeBuildsOf(Path.Combine(_plugins, "calc")));
        Assert.Equal(0, await StoreBuildCommand.RunAsync(Args, _stdout, _stderr, default));
    }

    // ONE STRING PER CASE, split on spaces: an array is not a constant, so it cannot sit in an attribute.
    [Theory]
    [InlineData("")]
    [InlineData("folder")]
    [InlineData("folder --url http://not-https/x --out o")]
    public async Task BadArguments_AreUsage(string args) =>
        Assert.Equal(2, await StoreBuildCommand.RunAsync(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), _stdout, _stderr, default));
}
