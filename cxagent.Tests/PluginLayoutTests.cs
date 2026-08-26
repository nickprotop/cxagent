using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A plugin's load set must be its own directory, so that its hash describes it and nothing else.
///
/// <para>THE HASH IS TRUTHFUL, NOT PARANOID. <c>ManagedPluginLoader</c> calls
/// <c>Assembly.LoadFrom</c> with no <c>AssemblyLoadContext</c>, so .NET resolves a plugin's
/// dependencies from its containing folder: a sibling DLL beside a loose plugin genuinely can be
/// loaded by it. These tests pin the layout that makes the hash's claim true.</para>
/// </summary>
public class PluginLayoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "plugin-layout-" + Guid.NewGuid().ToString("N"));

    public PluginLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    /// <summary>Writes a plugin's two files into <paramref name="directory"/> and returns it.</summary>
    private static string PlacePlugin(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".dll"), "not a real assembly");
        File.WriteAllText(Path.Combine(directory, name + ".plugin.json"),
            $$"""{"pluginContract":2,"name":"{{name}}","version":"1.0.0","spawns":false,"tools":[]}""");
        return directory;
    }

    /// <summary>
    /// THE CASE THAT DOES NOT WORK TODAY. A plugin in a folder of its own is invisible to a search
    /// that only checks the search folder itself.
    /// </summary>
    [Fact]
    public void APluginInItsOwnDirectoryIsFound()
    {
        PlacePlugin(Path.Combine(_root, "csharp-lsp"), "csharp-lsp");

        var resolved = PluginResolver.Resolve(
            "csharp-lsp",
            new Dictionary<string, PluginConfig> { ["csharp-lsp"] = new("csharp-lsp.dll") },
            [_root],
            _root);

        var found = Assert.IsType<PluginResolver.ResolveResult.Found>(resolved);
        Assert.Equal(Path.Combine(_root, "csharp-lsp"), found.LoadSetDirectory);
    }

    /// <summary>A plugin installed loose keeps resolving — nothing already installed breaks.</summary>
    [Fact]
    public void APluginLooseInTheFolderStillResolves()
    {
        PlacePlugin(_root, "legacy-lsp");

        var resolved = PluginResolver.Resolve(
            "legacy-lsp",
            new Dictionary<string, PluginConfig> { ["legacy-lsp"] = new("legacy-lsp.dll") },
            [_root],
            _root);

        var found = Assert.IsType<PluginResolver.ResolveResult.Found>(resolved);
        Assert.Equal(_root, found.LoadSetDirectory);
    }

    /// <summary>
    /// THE FOLDER WINS. A loose copy and a nested copy of one filename is a configuration the user
    /// built; resolving to the loose one deterministically is what makes Task 5's update path able
    /// to say a nested write would be shadowed.
    /// </summary>
    [Fact]
    public void ALooseCopyShadowsANestedOne()
    {
        PlacePlugin(_root, "both-ways");
        PlacePlugin(Path.Combine(_root, "both-ways-nested"), "both-ways");

        var resolved = PluginResolver.Resolve(
            "both-ways",
            new Dictionary<string, PluginConfig> { ["both-ways"] = new("both-ways.dll") },
            [_root],
            _root);

        var found = Assert.IsType<PluginResolver.ResolveResult.Found>(resolved);
        Assert.Equal(_root, found.LoadSetDirectory);
    }

    /// <summary>
    /// ONE LEVEL, NOT A WALK. A plugin's own dependencies may sit in nested folders, and a recursive
    /// search would find one and treat it as an entry point.
    /// </summary>
    [Fact]
    public void ASearchDoesNotDescendPastOneLevel()
    {
        var deep = Path.Combine(_root, "outer", "inner");
        PlacePlugin(deep, "too-deep");

        var resolved = PluginResolver.Resolve(
            "too-deep",
            new Dictionary<string, PluginConfig> { ["too-deep"] = new("too-deep.dll") },
            [_root],
            _root);

        Assert.IsType<PluginResolver.ResolveResult.NotFound>(resolved);
    }

    /// <summary>
    /// TWO SUBDIRECTORIES, ORDINAL ORDER. Whichever sorts first wins, every run and every machine —
    /// filesystem enumeration order is documented as unspecified.
    /// </summary>
    [Fact]
    public void TwoSubdirectoriesResolveInOrdinalOrder()
    {
        PlacePlugin(Path.Combine(_root, "zebra"), "twice");
        PlacePlugin(Path.Combine(_root, "alpha"), "twice");

        var resolved = PluginResolver.Resolve(
            "twice",
            new Dictionary<string, PluginConfig> { ["twice"] = new("twice.dll") },
            [_root],
            _root);

        var found = Assert.IsType<PluginResolver.ResolveResult.Found>(resolved);
        Assert.Equal(Path.Combine(_root, "alpha"), found.LoadSetDirectory);
    }

    /// <summary>
    /// THE POINT OF THE WHOLE CHANGE. A nested plugin's hash must not move when an unrelated plugin
    /// is installed beside it — the load gate keys an "always" grant on that hash
    /// (<c>Session.cs:513</c>), so a hash that moves silently revokes the grant.
    /// </summary>
    [Fact]
    public void ANestedPluginsHashSurvivesASiblingArriving()
    {
        var mine = PlacePlugin(Path.Combine(_root, "mine"), "mine");
        var before = PluginIdentity.HashLoadSet(mine);

        PlacePlugin(Path.Combine(_root, "theirs"), "theirs");

        Assert.Equal(before, PluginIdentity.HashLoadSet(mine));
    }

    /// <summary>
    /// THE DEFECT, RECORDED RATHER THAN FIXED. A loose plugin's load set is the whole folder, so a
    /// sibling arriving does move its hash. Migrating a user's files without being asked is the
    /// worse option; the warning in Task 3 is how this is surfaced instead.
    /// </summary>
    [Fact]
    public void ALoosePluginsHashStillMovesWhenASiblingArrives()
    {
        PlacePlugin(_root, "loose");
        var before = PluginIdentity.HashLoadSet(_root);

        PlacePlugin(Path.Combine(_root, "newcomer"), "newcomer");

        Assert.NotEqual(before, PluginIdentity.HashLoadSet(_root));
    }

    /// <summary>
    /// A NESTED PLUGIN MUST BE ANNOUNCED. install.sh installs but deliberately does not configure,
    /// so this scan is the only thing that tells a user the plugin is there at all. A nested install
    /// it cannot see is one nobody knows to turn on.
    /// </summary>
    [Fact]
    public void ANestedPluginIsAnnouncedAsUnconfigured()
    {
        PlacePlugin(Path.Combine(_root, "csharp-lsp"), "csharp-lsp");

        var found = CxAgent.UI.PluginDiscovery.FindUnconfigured(
            new Dictionary<string, PluginConfig>(), [_root]);

        var one = Assert.Single(found);
        Assert.Equal("csharp-lsp", one.Name);
        Assert.Equal("csharp-lsp.dll", one.File);
        Assert.Equal(Path.Combine(_root, "csharp-lsp"), one.Folder);
    }

    /// <summary>
    /// THE STARTUP SEARCH, NOT JUST THE CORE ONE. PluginDiscovery.FindLoadSetDirectory is a
    /// deliberate duplicate of PluginResolver's, and it is the copy that loads configured plugins
    /// when cxagent starts. Every other test here goes through PluginResolver, so without this one
    /// the duplicate could be reverted and the suite would stay green while startup was broken.
    /// </summary>
    [Fact]
    public void TheStartupSearchAlsoLooksOneLevelDown()
    {
        PlacePlugin(Path.Combine(_root, "csharp-lsp"), "csharp-lsp");

        var found = CxAgent.UI.PluginDiscovery.FindLoadSetDirectory("csharp-lsp.dll", [_root]);

        Assert.Equal(Path.Combine(_root, "csharp-lsp"), found);
    }

    /// <summary>
    /// AT ANY DEPTH, NOT JUST THE TOP LEVEL. After this design ships the normal way a second plugin
    /// arrives is NESTED — and a nested plugin sits inside a loose one's load set, because
    /// HashLoadSet walks with AllDirectories. A top-level check would miss the common case entirely.
    /// </summary>
    [Fact]
    public void ALoosePluginSeesANestedNewcomerInItsLoadSet()
    {
        PlacePlugin(_root, "loose");
        PlacePlugin(Path.Combine(_root, "newcomer"), "newcomer");

        var shared = PluginIdentity.SharesLoadSetWith(_root, "loose");

        Assert.Equal(["newcomer"], shared);
    }

    /// <summary>A plugin with its directory to itself shares with nobody.</summary>
    [Fact]
    public void ANestedPluginSharesItsLoadSetWithNobody()
    {
        var mine = PlacePlugin(Path.Combine(_root, "mine"), "mine");
        PlacePlugin(Path.Combine(_root, "theirs"), "theirs");

        Assert.Empty(PluginIdentity.SharesLoadSetWith(mine, "mine"));
    }
}
