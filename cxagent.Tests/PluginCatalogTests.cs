using System.Text.Json;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The catalog at <c>plugins/plugins.json</c> against the plugins it describes.
///
/// <para>A CATALOG IS A CLAIM ABOUT SOMETHING ELSE, and nothing else checks it. The plugin picker
/// will show what this file says without loading a DLL to find out — that is the point of a
/// catalog — so an entry that has drifted from its plugin describes something that does not exist,
/// and the reader has no way to notice.</para>
///
/// <para>These read the repository's own files rather than build output: the catalog is a source
/// artifact maintained by hand, and what it must agree with is the sidecar shipped beside the
/// plugin, also by hand.</para>
/// </summary>
public class PluginCatalogTests
{
    /// <summary>The repository root, found by walking up from the test binary until plugins.json is
    /// there — the same shape ResolveHostDll uses, and it fails with a message naming what it looked
    /// for rather than a null.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "plugins", "plugins.json"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            $"plugins/plugins.json not found walking up from '{AppContext.BaseDirectory}'.");
    }

    private static JsonElement Catalog() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "plugins", "plugins.json")))
            .RootElement.Clone();

    private static IEnumerable<JsonElement> Entries() =>
        Catalog().GetProperty("plugins").EnumerateArray();

    [Fact]
    public void EveryEntryDescribesAPluginThatIsActuallyThere()
    {
        var root = RepoRoot();

        foreach (var entry in Entries())
        {
            var name = entry.GetProperty("name").GetString()!;

            // A FIRST-PARTY ENTRY HAS A DIRECTORY; a third-party one would not, and is not checked
            // here because nothing in this repository can verify it.
            if (!entry.TryGetProperty("directory", out var dirEl)) continue;

            var dir = Path.Combine(root, "plugins", dirEl.GetString()!);
            Assert.True(Directory.Exists(dir), $"catalog names directory '{dir}', which is not there.");

            var readme = Path.Combine(root, "plugins", entry.GetProperty("readme").GetString()!);
            Assert.True(File.Exists(readme), $"catalog names readme '{readme}', which is not there.");
        }
    }

    /// <summary>
    /// The catalog's claims about a plugin match the plugin's OWN sidecar — the file cxagent
    /// actually reads at load.
    ///
    /// <para>THESE ARE TWO HAND-MAINTAINED COPIES OF ONE TRUTH, which is the arrangement that drifts.
    /// The sidecar wins: it is what the loader reads and what the approval prompt shows, so a
    /// disagreement is the catalog being wrong.</para>
    /// </summary>
    [Fact]
    public void EveryEntryAgreesWithItsPluginsSidecar()
    {
        var root = RepoRoot();

        foreach (var entry in Entries())
        {
            if (!entry.TryGetProperty("directory", out var dirEl)) continue;

            var name = entry.GetProperty("name").GetString()!;
            var sidecarPath = Path.Combine(root, "plugins", dirEl.GetString()!, $"{name}.plugin.json");
            Assert.True(File.Exists(sidecarPath), $"no sidecar at '{sidecarPath}' for catalog entry '{name}'.");

            var sidecar = JsonDocument.Parse(File.ReadAllText(sidecarPath)).RootElement;

            Assert.Equal(sidecar.GetProperty("name").GetString(), name);
            Assert.Equal(sidecar.GetProperty("version").GetString(),
                entry.GetProperty("version").GetString());
            Assert.Equal(sidecar.GetProperty("spawns").GetBoolean(),
                entry.GetProperty("spawns").GetBoolean());

            var declared = sidecar.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToArray();
            var catalogued = entry.GetProperty("tools").EnumerateArray()
                .Select(t => t.GetString()).ToArray();

            Assert.Equal(declared, catalogued);
        }
    }

    /// <summary>
    /// A release-sourced entry names an asset the release workflow actually builds.
    ///
    /// <para>THE ASSET NAME IS WRITTEN IN TWO PLACES — here and in the workflow that zips it — and a
    /// catalog pointing at a file no release carries is a download that 404s for every user. The
    /// workflow is the one that decides, so this reads it.</para>
    /// </summary>
    [Fact]
    public void ReleaseSourcedEntriesNameAnAssetTheWorkflowBuilds()
    {
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        foreach (var entry in Entries())
        {
            var source = entry.GetProperty("source");
            if (source.GetProperty("kind").GetString() != "release") continue;

            var asset = source.GetProperty("asset").GetString()!;
            Assert.Contains(asset, workflow);
        }
    }
}
