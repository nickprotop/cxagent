using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Skills;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE DISCOVERY WALKS — a skill's file list, the command folders, a plugin's hash and its removal — go one way:
/// a link that leaves is not followed, and a cycle of links ends.
///
/// <para>STRUCTURAL, NOT TIMED. The old walks follow a cycle until the path is too long, which with two self-links
/// is about 2^40 paths; a test that waited for that would hang the suite. Each assertion here reads what the walk
/// returned — no path through the loop twice, no outside name — and so fails at once on the old code.</para>
/// </summary>
public sealed class DiscoveryWalkTests : IDisposable
{
    private readonly string _top = Path.Combine(Path.GetTempPath(), "cx-walks-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;

    public DiscoveryWalkTests()
    {
        _outside = Path.Combine(_top, "outside");
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "away");
    }

    public void Dispose() { try { Directory.Delete(_top, recursive: true); } catch (IOException) { } }

    [Fact]
    public void ASkillsFileList_EndsOnACycle_AndNamesNothingOutside()
    {
        if (OperatingSystem.IsWindows()) return;
        var skill = Path.Combine(_top, "skill");
        Directory.CreateDirectory(Path.Combine(skill, "references"));
        File.WriteAllText(Path.Combine(skill, "references", "guide.md"), "x");
        Directory.CreateSymbolicLink(Path.Combine(skill, "a"), skill);
        Directory.CreateSymbolicLink(Path.Combine(skill, "b"), skill);
        Directory.CreateSymbolicLink(Path.Combine(skill, "away"), _outside);
        var loader = new SkillLoader(() => new SkillCatalogResult([new SkillInfo("s", "d", skill, "body")], [], skill));

        var text = loader.TryInvoke(new ToolCall
        {
            Name = "skill", Id = "1", Arguments = JsonSerializer.SerializeToElement(new { name = "s" }),
        }, [])!;

        Assert.Contains("guide.md", text);
        Assert.DoesNotContain("a/a", text);
        Assert.DoesNotContain("secret.txt", text);
    }

    [Fact]
    public void ACommandFolderOfSelfLinks_ReadsEachCommandOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var repo = Path.Combine(_top, "repo");
        var commands = Path.Combine(repo, ".cxagent", "commands");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(commands);
        File.WriteAllText(Path.Combine(commands, "review.md"), "Review it.");
        foreach (var name in new[] { "l1", "l2", "l3", "l4" })
            Directory.CreateSymbolicLink(Path.Combine(commands, name), commands);

        var found = InvocableCatalog.Find(new CatalogRoots(repo, null));

        Assert.Single(found.Entries, i => i.Name == "review");
        Assert.True(found.Problems.Count < 10, $"{found.Problems.Count} problems");
    }

    [Fact]
    public void RemovingAPlugin_RemovesALinkAsALink_AndLeavesWhatItPointsAt()
    {
        if (OperatingSystem.IsWindows()) return;
        var plugins = Path.Combine(_top, "plugins");
        var home = Path.Combine(plugins, "x");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "x.dll"), "bin");
        File.WriteAllText(Path.Combine(home, "x.plugin.json"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(home, "data"), _outside);

        var plan = Assert.IsType<RemovalPlan.Removable>(PluginRemover.Plan("x", "x.dll", home, [plugins],
            new Dictionary<string, PluginConfig> { ["x"] = new PluginConfig("x.dll") }));
        PluginRemover.Remove(plan);

        Assert.True(File.Exists(Path.Combine(_outside, "secret.txt")));
        Assert.False(Directory.Exists(Path.Combine(home, "data")));
    }

    [Fact]
    public void ALoadSetWithALinkOutside_IsRefused_NamingTheLink()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = Path.Combine(_top, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "p.dll"), "bin");
        File.CreateSymbolicLink(Path.Combine(set, "dep.dll"), Path.Combine(_outside, "secret.txt"));

        var identity = PluginIdentity.InspectLoadSet(set);

        Assert.Null(identity.Hash);
        Assert.Contains("dep.dll", identity.Refusal);
    }

    [Fact]
    public void ALoadSetWithALinkCycle_IsReported_NotThrown()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = Path.Combine(_top, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "p.dll"), "bin");
        Directory.CreateSymbolicLink(Path.Combine(set, "loop"), set);

        var identity = PluginIdentity.InspectLoadSet(set);

        Assert.Null(identity.Hash);
        Assert.Contains("loop", identity.Refusal);
    }

    [Fact]
    public void ALoadSetReachedThroughALink_HashesAsTheFolderItIs()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = Path.Combine(_top, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "p.dll"), "bin");
        var alias = Path.Combine(_top, "alias");
        Directory.CreateSymbolicLink(alias, set);

        Assert.Equal(PluginIdentity.HashLoadSet(set), PluginIdentity.HashLoadSet(alias));
    }

    [Fact]
    public void AnInsideLink_IsHashedAsItsTargetText()
    {
        if (OperatingSystem.IsWindows()) return;
        var set = Path.Combine(_top, "set");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "p.dll"), "bin");
        File.CreateSymbolicLink(Path.Combine(set, "alias.dll"), Path.Combine(set, "p.dll"));
        var before = PluginIdentity.HashLoadSet(set);

        File.WriteAllText(Path.Combine(set, "p.dll"), "changed");

        // THE CONTENT CHANGED once, under p.dll; the link names the same target, so only p.dll's change moves it.
        Assert.NotEqual(before, PluginIdentity.HashLoadSet(set));
        Assert.Null(PluginIdentity.InspectLoadSet(set).Refusal);
    }
}
