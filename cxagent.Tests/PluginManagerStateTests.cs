using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE HALF THAT TOUCHES DISK. Which configured entries have no file, what each installed plugin's
/// sidecar declares — read WITHOUT loading anything, which is the guarantee the load gate depends on.
/// </summary>
public class PluginManagerStateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-mgr-" + Guid.NewGuid().ToString("N"));

    public PluginManagerStateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes a plugin's two files into a directory of its own and returns that directory.</summary>
    private string Place(string name, string version = "1.0.0", int contract = 2)
    {
        var home = Path.Combine(_dir, name);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, name + ".dll"), "not a real assembly");
        File.WriteAllText(Path.Combine(home, name + ".plugin.json"),
            $$"""
            { "pluginContract": {{contract}}, "name": "{{name}}", "version": "{{version}}",
              "spawns": false, "tools": [] }
            """);
        return home;
    }

    /// <summary>A configured entry whose file resolves is not reported missing.</summary>
    [Fact]
    public void APresentFileIsNotMissing()
    {
        Place("here");

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig> { ["here"] = new("here.dll") },
            [], [_dir], [], _dir);

        Assert.Empty(inputs.MissingFiles);
    }

    /// <summary>
    /// A CONFIGURED ENTRY WITH NO FILE IS THE POINT OF THIS. Nothing else in cxagent notices — the
    /// plugin simply never loads — so the dialog is where a user finds out their config names
    /// something that is not there.
    /// </summary>
    [Fact]
    public void AConfiguredEntryWithNoFileIsReportedMissing()
    {
        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig> { ["ghost"] = new("ghost.dll") },
            [], [_dir], [], _dir);

        Assert.Contains("ghost", inputs.MissingFiles);
    }

    /// <summary>The sidecar's version and contract are read from disk, without loading the binary —
    /// which is what makes an incompatible plugin visible rather than a failed load.</summary>
    [Fact]
    public void TheSidecarsVersionAndContractAreRead()
    {
        Place("real", version: "2.5.0", contract: 1);

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig> { ["real"] = new("real.dll") },
            [], [_dir], [], _dir);

        Assert.Equal("2.5.0", inputs.InstalledVersions["real"]);
        Assert.Equal(1, inputs.InstalledContracts["real"]);
    }

    /// <summary>A plugin on disk that config never named is discovered, so the rail can offer it.</summary>
    [Fact]
    public void APluginConfigNeverNamedIsDiscovered()
    {
        Place("stranger");

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig>(), [], [_dir], [], _dir);

        Assert.Contains(inputs.Unconfigured, u => u.Name == "stranger");
    }
}
