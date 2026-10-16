using CxAgent.Core.Storage;
using CxAgent.UI.Config;
using Xunit;

namespace CxAgent.Tests;

/// <summary>What the Providers tab reads: no file and no providers are a first run, not an error.</summary>
public class ProvidersFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "providers-file-" + Guid.NewGuid().ToString("N"));
    private static readonly Dictionary<string, string> NoEnv = new();
    public ProvidersFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void NoFile_IsNoProvidersYet()
    {
        var read = ProvidersFile.Read(new AppPaths(_dir), NoEnv);
        Assert.Empty(read.Errors);
        Assert.Empty(read.Settings!.Providers);
    }

    // THE THEME PICKER WRITES A FILE WITH ONLY A THEME IN IT, before any provider exists.
    [Fact]
    public void AFileWithNoProvidersBlock_IsNoProvidersYet()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "theme": "slate" }""");
        var read = ProvidersFile.Read(new AppPaths(_dir), NoEnv);
        Assert.Empty(read.Errors);
        Assert.Empty(read.Settings!.Providers);
    }

    [Fact]
    public void ABrokenFile_IsItsErrors()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ broken");
        var read = ProvidersFile.Read(new AppPaths(_dir), NoEnv);
        Assert.Null(read.Settings);
        Assert.Contains("not valid JSON", read.Errors[0]);
    }
}
