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
    /// A client can build a download URL from any entry, without knowing where the plugin came from.
    ///
    /// <para>THE TWO SOURCE KINDS ANSWER THE SAME QUESTION DIFFERENTLY, and a picker has to handle
    /// both: a <c>url</c> source is fetched as written, a <c>release</c> source is a template plus
    /// the tag being installed, because it tracks the release rather than pinning a version this
    /// file cannot know. What must never happen is an entry offering neither — a plugin nobody can
    /// download, listed as available.</para>
    /// </summary>
    [Fact]
    public void EveryEntryCanProduceADownloadUrl()
    {
        foreach (var entry in Entries())
        {
            var name = entry.GetProperty("name").GetString()!;
            var source = entry.GetProperty("source");

            switch (source.GetProperty("kind").GetString())
            {
                case "url":
                    var url = source.GetProperty("url").GetString();
                    Assert.StartsWith("https://", url);

                    // A URL SOURCE CARRIES ITS OWN HASH. Nothing else can vouch for a file fetched
                    // from somewhere this project does not control.
                    Assert.Equal(64, source.GetProperty("sha256").GetString()!.Length);
                    break;

                case "release":
                    var template = source.GetProperty("urlTemplate").GetString()!;
                    var built = template
                        .Replace("{repo}", source.GetProperty("repo").GetString())
                        .Replace("{asset}", source.GetProperty("asset").GetString())
                        .Replace("{tag}", "v1.2.3");

                    Assert.StartsWith("https://", built);
                    Assert.DoesNotContain("{", built);   // every placeholder was substitutable
                    Assert.Contains("v1.2.3", built);

                    Assert.StartsWith("https://", source.GetProperty("latest").GetString());

                    // sha256 IS STAMPED BY THE RELEASE, so it is null until one has run and a real
                    // hash whenever it is not. What this pins is that a value, once present, is a
                    // sha256 and is paired with the tag it came from — a hash with no tag cannot be
                    // told apart from one left over from an older release.
                    var stamped = source.GetProperty("sha256");
                    if (stamped.ValueKind != JsonValueKind.Null)
                    {
                        Assert.Equal(64, stamped.GetString()!.Length);
                        Assert.NotEqual(JsonValueKind.Null, source.GetProperty("stampedAtTag").ValueKind);
                    }
                    break;

                default:
                    Assert.Fail($"entry '{name}' has an unknown source kind — a client cannot fetch it.");
                    break;
            }
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
