using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A plugin installed into its own subdirectory has a readable manifest.
///
/// <para>THE SHAPE THAT BROKE: the manager writes <c>"file": "clone-finder/clone-finder.dll"</c>,
/// and the sidecar sits beside the DLL — one level below the search folder. Resolving it against the
/// folder instead looks a level too high, finds nothing, and reports "no usable manifest" for a
/// plugin that had already loaded successfully.</para>
/// </summary>
public class PluginManagerNestedSidecarTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "nested-sidecar-" + Guid.NewGuid().ToString("N"));

    public PluginManagerNestedSidecarTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Install(string name, string sidecarJson)
    {
        var home = Path.Combine(_dir, name);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, name + ".dll"), "");
        File.WriteAllText(Path.Combine(home, name + ".plugin.json"), sidecarJson);
        return home;
    }

    [Fact]
    public void ANestedPlugin_IsNotReportedAsHavingNoManifest()
    {
        Install("clone-finder", """
        { "pluginContract": 2, "name": "clone-finder", "version": "0.9.8", "tools": [] }
        """);

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig>
            {
                ["clone-finder"] = new("clone-finder/clone-finder.dll"),
            },
            loaded: ["clone-finder"],
            searchFolders: [_dir],
            catalog: [],
            projectDirectory: _dir);

        Assert.DoesNotContain("clone-finder", inputs.Unreadable);
        Assert.DoesNotContain("clone-finder", inputs.MissingFiles);
    }

    /// <summary>The version comes from the sidecar, which is only readable if it was found.</summary>
    [Fact]
    public void ItsVersionIsReadFromTheNestedSidecar()
    {
        Install("clone-finder", """
        { "pluginContract": 2, "name": "clone-finder", "version": "0.9.8", "tools": [] }
        """);

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig> { ["clone-finder"] = new("clone-finder/clone-finder.dll") },
            loaded: [], searchFolders: [_dir], catalog: [], projectDirectory: _dir);

        Assert.Equal("0.9.8", inputs.InstalledVersions["clone-finder"]);
    }

    /// <summary>A genuinely broken sidecar must STILL be reported — the fix must not hide that.</summary>
    [Fact]
    public void ABrokenNestedSidecar_IsStillUnreadable()
    {
        Install("broken", "{ this is not json");

        var inputs = PluginManagerState.Gather(
            new Dictionary<string, PluginConfig> { ["broken"] = new("broken/broken.dll") },
            loaded: [], searchFolders: [_dir], catalog: [], projectDirectory: _dir);

        Assert.Contains("broken", inputs.Unreadable);
    }
}
