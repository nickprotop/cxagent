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

                    // sha256 IS NULL FOR THE BUILT-IN PLUGIN — see the catalog's own note on why
                    // that is left open. What this pins is that a value, once someone sets one, is
                    // actually a sha256 rather than a URL or a placeholder.
                    var hash = source.GetProperty("sha256");
                    if (hash.ValueKind != JsonValueKind.Null)
                        Assert.Equal(64, hash.GetString()!.Length);
                    break;

                default:
                    Assert.Fail($"entry '{name}' has an unknown source kind — a client cannot fetch it.");
                    break;
            }
        }
    }

    /// <summary>
    /// A plugin's declared platforms match what it can actually be downloaded for.
    ///
    /// <para>A MANAGED PLUGIN IS PORTABLE MSIL and says <c>["any"]</c> with a single
    /// <c>source</c> — one build runs everywhere. AN ABI PLUGIN IS NOT: a <c>.so</c> does not load
    /// on Windows, and a publisher may ship two RIDs of the six. Those entries key their downloads
    /// by RID under <c>sources</c>, and this checks the two lists agree.</para>
    ///
    /// <para>A PLATFORM CLAIMED WITH NO DOWNLOAD IS THE FAILURE WORTH CATCHING: a picker filters on
    /// this list, so the plugin is offered to a machine it cannot be installed on, and the error
    /// arrives as a 404 rather than "not available for your platform". The reverse — a download for
    /// a platform not claimed — is a plugin nobody is offered, which is only wasted work, but it
    /// means one of the two lists is wrong either way.</para>
    ///
    /// <para>THE EXAMPLES ARE CHECKED TOO, since they are the only native entries that exist and a
    /// broken example is copied into a real one.</para>
    /// </summary>
    [Fact]
    public void DeclaredPlatformsMatchAvailableDownloads()
    {
        var catalog = Catalog();

        var all = Entries().ToList();
        foreach (var name in new[] { "$example", "$exampleNative" })
            if (catalog.TryGetProperty(name, out var example))
                all.Add(example);

        foreach (var entry in all)
        {
            var name = entry.GetProperty("name").GetString()!;
            var platforms = entry.GetProperty("compatibility").GetProperty("platforms")
                .EnumerateArray().Select(p => p.GetString()!).ToList();

            Assert.NotEmpty(platforms);

            if (platforms is ["any"])
            {
                // Portable: one source, no per-platform keys.
                Assert.True(entry.TryGetProperty("source", out _),
                    $"'{name}' claims any platform but has no single source.");
                Assert.False(entry.TryGetProperty("sources", out _),
                    $"'{name}' claims any platform and yet lists per-platform sources — one of those is wrong.");
                continue;
            }

            Assert.True(entry.TryGetProperty("sources", out var sources),
                $"'{name}' names specific platforms but has no per-platform sources.");

            var keyed = sources.EnumerateObject().Select(p => p.Name).ToList();

            Assert.Equal(platforms.OrderBy(p => p, StringComparer.Ordinal),
                         keyed.OrderBy(p => p, StringComparer.Ordinal));

            // KNOWN RIDs ONLY. An invented name matches nothing a client computes for itself, so the
            // plugin is invisible on every machine rather than obviously wrong.
            string[] known =
                ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];
            foreach (var rid in keyed)
                Assert.Contains(rid, known);

            // Each one must be fetchable and carry its own hash — see EveryEntryCanProduceADownloadUrl
            // for why a url source states its own.
            foreach (var source in sources.EnumerateObject())
            {
                Assert.StartsWith("https://", source.Value.GetProperty("url").GetString());
                Assert.Equal(64, source.Value.GetProperty("sha256").GetString()!.Length);
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
