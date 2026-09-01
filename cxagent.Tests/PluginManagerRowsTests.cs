using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// WHERE EVERY ROW GOES, decided with no window. The spec's placement table has thirteen states and
/// a precedence order (NEEDS ATTENTION > UPDATES > INSTALLED > AVAILABLE); deciding them inside a
/// control would make each one cost a rendered dialog to check.
/// </summary>
public class PluginManagerRowsTests
{
    private static CatalogEntry Entry(string name, string version = "1.0.0", int contract = 2) =>
        new(name, name, version, "d", "pub", "MIT", "https://r", "", name + ".dll",
            "managed", contract, [], "https://x/p.zip", new string('a', 64), null, null);

    private static PluginManagerInputs Inputs(
        IReadOnlyDictionary<string, PluginConfig>? configured = null,
        IReadOnlyList<string>? loaded = null,
        IReadOnlyList<PluginDiscovery.UnconfiguredPlugin>? unconfigured = null,
        IReadOnlyList<CatalogEntry>? catalog = null,
        IReadOnlyDictionary<string, string>? versions = null,
        IReadOnlyDictionary<string, int>? contracts = null,
        IReadOnlySet<string>? missing = null,
        IReadOnlySet<string>? unreadable = null,
        IReadOnlyDictionary<string, string>? folders = null) =>
        new(configured ?? new Dictionary<string, PluginConfig>(),
            loaded ?? [],
            unconfigured ?? [],
            catalog ?? [],
            versions ?? new Dictionary<string, string>(),
            contracts ?? new Dictionary<string, int>(),
            missing ?? new HashSet<string>(),
            unreadable ?? new HashSet<string>(),
            folders ?? new Dictionary<string, string>());

    private static PluginRow Row(IReadOnlyList<PluginRow> rows, string name) =>
        Assert.Single(rows, r => r.Name == name);

    /// <summary>The three states /plugin itself prints, in the words /plugin uses.</summary>
    [Fact]
    public void AConfiguredPluginReportsLoadedDeclaredOrDisabled()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig>
            {
                ["running"] = new("running.dll"),
                ["idle"] = new("idle.dll"),
                ["off"] = new("off.dll", Enabled: false),
            },
            loaded: ["running"]));

        Assert.Equal("active", Row(rows, "running").State);
        Assert.Equal("not active", Row(rows, "idle").State);
        Assert.Equal("not active, no auto load", Row(rows, "off").State);
        Assert.All(rows, r => Assert.Equal(PluginRowSection.Installed, r.Section));
    }

    /// <summary>
    /// A PLUGIN ON DISK THAT CONFIG NEVER NAMED is installed, not available — it is already here.
    /// /plugin cannot show this state at all, because it lists config's entries.
    /// </summary>
    [Fact]
    public void AnUnconfiguredPluginOnDiskIsInstalled()
    {
        var rows = PluginManagerRows.Build(Inputs(
            unconfigured: [new PluginDiscovery.UnconfiguredPlugin("found", "found.dll", "/p/found", 3)]));

        var row = Row(rows, "found");
        Assert.Equal(PluginRowSection.Installed, row.Section);
        // THE ABSENCE, NOT THE SYMPTOM: no entry is a different situation from an entry that says
        // no auto-load, and the button beside it (Add to config) answers this one.
        Assert.Equal("not in config", row.State);
    }

    /// <summary>A configured plugin whose file is gone cannot load, so it needs the user.</summary>
    [Fact]
    public void AConfiguredPluginWithNoFileNeedsAttention()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["ghost"] = new("ghost.dll") },
            missing: new HashSet<string> { "ghost" }));

        var row = Row(rows, "ghost");
        Assert.Equal(PluginRowSection.NeedsAttention, row.Section);
        Assert.Equal("file missing", row.State);
    }

    /// <summary>
    /// CONTRACT MISMATCH SPLITS BY DIRECTION, because the remedies differ: an older plugin needs a
    /// newer build, which the catalog may have; a newer one needs a newer cxagent, which nothing in
    /// this dialog can supply.
    /// </summary>
    [Theory]
    [InlineData(1, "needs a newer build")]
    [InlineData(3, "needs a newer cxagent")]
    public void AContractMismatchSaysWhichWayItIsWrong(int contract, string expected)
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["odd"] = new("odd.dll") },
            contracts: new Dictionary<string, int> { ["odd"] = contract }));

        var row = Row(rows, "odd");
        Assert.Equal(PluginRowSection.NeedsAttention, row.Section);
        Assert.Contains(expected, row.State);
    }

    /// <summary>
    /// BOTH VERSIONS, NO CLAIM ABOUT WHICH IS NEWER. Comparison is ordinal because versions come
    /// from a sidecar an author writes, and parsing them as semver means deciding what to do with
    /// one that is not. A locally built plugin ahead of the catalog must not be offered a
    /// "update" that is a downgrade.
    /// </summary>
    [Fact]
    public void AVersionDifferenceGoesToUpdatesAndStatesBoth()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["p"] = new("p.dll") },
            catalog: [Entry("p", "0.3.0")],
            versions: new Dictionary<string, string> { ["p"] = "0.2.1" }));

        var row = Row(rows, "p");
        Assert.Equal(PluginRowSection.Updates, row.Section);
        Assert.Contains("0.2.1", row.State);
        Assert.Contains("0.3.0", row.State);
    }

    /// <summary>A disabled plugin with an update stays disabled in the words, and moves to
    /// UPDATES — the section is about the update; the state still shows.</summary>
    [Fact]
    public void ADisabledPluginWithAnUpdateStillSaysItWillNotAutoLoad()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["p"] = new("p.dll", Enabled: false) },
            catalog: [Entry("p", "0.3.0")],
            versions: new Dictionary<string, string> { ["p"] = "0.2.1" }));

        var row = Row(rows, "p");
        Assert.Equal(PluginRowSection.Updates, row.Section);
        Assert.Contains("no auto load", row.State);
    }

    /// <summary>
    /// BROKEN BEATS OUT-OF-DATE. A plugin with both a bad contract and an available update belongs
    /// where the user is told it needs them; the update is its ACTION, not its section.
    /// </summary>
    [Fact]
    public void ABrokenContractWinsOverAnAvailableUpdate()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["p"] = new("p.dll") },
            catalog: [Entry("p", "0.3.0")],
            versions: new Dictionary<string, string> { ["p"] = "0.2.1" },
            contracts: new Dictionary<string, int> { ["p"] = 1 }));

        Assert.Equal(PluginRowSection.NeedsAttention, Row(rows, "p").Section);
    }

    /// <summary>
    /// A CATALOG PLUGIN YOU ALREADY HAVE APPEARS ONCE. Two rows for one plugin is what a split
    /// installed/marketplace layout gets wrong, and it is where the update story would be lost.
    /// </summary>
    [Fact]
    public void APluginBothInstalledAndInTheCatalogHasOneRow()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["p"] = new("p.dll") },
            catalog: [Entry("p", "1.0.0")],
            versions: new Dictionary<string, string> { ["p"] = "1.0.0" }));

        Assert.Single(rows);
        Assert.Equal(PluginRowSection.Installed, Row(rows, "p").Section);
    }

    /// <summary>A catalog entry you do not have is available, and carries its entry for the panel
    /// to render without a second lookup.</summary>
    [Fact]
    public void ACatalogOnlyEntryIsAvailable()
    {
        var rows = PluginManagerRows.Build(Inputs(catalog: [Entry("new-thing")]));

        var row = Row(rows, "new-thing");
        Assert.Equal(PluginRowSection.Available, row.Section);
        Assert.NotNull(row.Catalog);
    }

    /// <summary>
    /// A CATALOG ENTRY THIS BUILD CANNOT LOAD STAYS AVAILABLE, listed with its reason. It is not
    /// installed, so it needs nothing FROM the user — hiding it would leave them wondering where a
    /// plugin they read about went.
    /// </summary>
    [Fact]
    public void ACatalogEntryWithAWrongContractIsAvailableWithAReason()
    {
        var rows = PluginManagerRows.Build(Inputs(catalog: [Entry("future", contract: 3)]));

        var row = Row(rows, "future");
        Assert.Equal(PluginRowSection.Available, row.Section);
        Assert.Contains("contract", row.State);
    }

    /// <summary>Rows are ordered by section, then by name within it, so the rail is stable across
    /// rebuilds — a row that jumps after an action is a row the user loses.</summary>
    [Fact]
    public void RowsAreOrderedBySectionThenName()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig>
            {
                ["zebra"] = new("z.dll"),
                ["alpha"] = new("a.dll"),
            },
            catalog: [Entry("beta")]));

        Assert.Equal(["alpha", "zebra", "beta"], rows.Select(r => r.Name).ToArray());
    }

    /// <summary>THE FILTER SEARCHES EVERY SECTION AT ONCE — the argument for one grouped rail
    /// rather than tabs. A name substring narrows installed and available rows alike.</summary>
    [Fact]
    public void FilterNarrowsByNameAcrossSections()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["lsp-host"] = new("h.dll") },
            catalog: [Entry("csharp-lsp"), Entry("unrelated")]));

        var hits = PluginManagerRows.Filter(rows, "lsp");

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, r => r.Section == PluginRowSection.Installed);
        Assert.Contains(hits, r => r.Section == PluginRowSection.Available);
    }

    /// <summary>Empty text matches everything, so clearing the filter box restores the full rail
    /// rather than an empty one.</summary>
    [Fact]
    public void FilterWithEmptyTextReturnsEverything()
    {
        var rows = PluginManagerRows.Build(Inputs(
            configured: new Dictionary<string, PluginConfig> { ["p"] = new("p.dll") },
            catalog: [Entry("q")]));

        Assert.Same(rows, PluginManagerRows.Filter(rows, ""));
        Assert.Same(rows, PluginManagerRows.Filter(rows, null));
    }

    /// <summary>
    /// LOADED BUT NEVER CONFIGURED — what the manager's own [load now] leaves behind, since
    /// installing does not write config. The row has to carry both facts: it is answering tool
    /// calls right now, and nothing will bring it back next session.
    ///
    /// <para>Saying only that it will not auto load describes a running plugin as inert, and the
    /// unconfigured branch claims the name before the loaded-by-path pass can correct it — so the
    /// omission is invisible except in the button set.</para>
    /// </summary>
    [Fact]
    public void AnUnconfiguredPluginThatIsLoadedSaysSo()
    {
        var rows = PluginManagerRows.Build(Inputs(
            loaded: ["found"],
            unconfigured: [new PluginDiscovery.UnconfiguredPlugin("found", "found.dll", "/p/found", 3)]));

        var row = Row(rows, "found");
        Assert.Equal(PluginRowSection.Installed, row.Section);
        Assert.Equal("active, not in config", row.State);
    }
}
