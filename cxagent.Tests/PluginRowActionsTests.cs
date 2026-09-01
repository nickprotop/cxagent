using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Which section a plugin lands in, for the cases whose row decides what the dialog can offer.
///
/// <para>THE SECTION IS THE BUTTON SET. PluginManagerDialog switches on it, so a row filed under
/// the wrong one silently removes every action that applies — which is how two working plugins came
/// to be offered nothing but Uninstall.</para>
/// </summary>
public class PluginRowActionsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "rows-" + Guid.NewGuid().ToString("N"));

    public PluginRowActionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private void Install(string name, string version = "1.0.0", int contract = 2)
    {
        var home = Path.Combine(_dir, name);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, name + ".dll"), "");
        File.WriteAllText(Path.Combine(home, name + ".plugin.json"),
            $$"""{ "pluginContract": {{contract}}, "name": "{{name}}", "version": "{{version}}", "tools": [] }""");
    }

    private IReadOnlyList<PluginRow> Rows(
        Dictionary<string, PluginConfig>? configured = null, string[]? loaded = null)
    {
        var inputs = PluginManagerState.Gather(
            configured ?? [], loaded ?? [], [_dir], [], _dir);
        return PluginManagerRows.Build(inputs);
    }

    /// <summary>
    /// A configured plugin installed in its own directory is INSTALLED, not broken — the case that
    /// regressed, where the sidecar was sought a level above where it lives.
    /// </summary>
    [Fact]
    public void ANestedConfiguredPlugin_IsInstalled()
    {
        Install("clone-finder");

        var row = Rows(new() { ["clone-finder"] = new("clone-finder/clone-finder.dll") })
            .Single(r => r.Name == "clone-finder");

        Assert.Equal(PluginRowSection.Installed, row.Section);
    }

    /// <summary>
    /// A discovered plugin knows its file, which is what the "Auto load" button needs to write a
    /// config entry — without it the row can be shown but never configured.
    /// </summary>
    [Fact]
    public void ADiscoveredPlugin_IsInstalledAndSaysItIsNotInConfig()
    {
        Install("local-build");

        var row = Rows().Single(r => r.Name == "local-build");

        Assert.Equal(PluginRowSection.Installed, row.Section);
        Assert.Contains("not in config", row.State);
        Assert.Null(row.Configured);
    }

    /// <summary>A plugin loaded from outside every search folder says so, because it has fewer
    /// actions than one that looks identical beside it.</summary>
    [Fact]
    public void APluginLoadedFromAPath_SaysWhereItCameFrom()
    {
        var row = Rows(loaded: ["by-path"]).Single(r => r.Name == "by-path");

        Assert.Equal(PluginRowSection.Installed, row.Section);
        Assert.Contains("path", row.State);
        Assert.Null(row.Folder);
    }

    /// <summary>A broken sidecar still reports — the nested-path fix must not mask a real fault.</summary>
    [Fact]
    public void ABrokenManifest_StillNeedsAttention()
    {
        var home = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "broken.dll"), "");
        File.WriteAllText(Path.Combine(home, "broken.plugin.json"), "{ not json");

        var row = Rows(new() { ["broken"] = new("broken/broken.dll") }).Single(r => r.Name == "broken");

        Assert.Equal(PluginRowSection.NeedsAttention, row.Section);
    }
}
