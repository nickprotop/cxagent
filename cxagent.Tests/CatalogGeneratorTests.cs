using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The generator that turns the committed catalog into the published one. Tested through the
/// process boundary rather than by importing it, because that is exactly how the workflow calls
/// it: a bug in argument handling or exit codes is a broken deploy, and a test that bypassed the
/// command line would not see it.
/// </summary>
public class CatalogGeneratorTests
{
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

    /// <summary>
    /// Every --plugin key the real catalog needs: <c>name</c> for a single release source,
    /// <c>name@rid</c> for each release source of a per-platform entry. Read from the catalog itself
    /// rather than hardcoded — the generator refuses an entry nobody stamped, so a hardcoded list goes
    /// stale the moment an entry is added, and these tests would then fail over a plugin they never
    /// heard of instead of testing anything.
    /// </summary>
    private static string[] ReleasePlugins(string root)
    {
        var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "plugins", "plugins.json")));
        var keys = new List<string>();
        foreach (var e in catalog.RootElement.GetProperty("plugins").EnumerateArray())
        {
            var name = e.GetProperty("name").GetString()!;
            if (e.TryGetProperty("sources", out var sources))
                keys.AddRange(sources.EnumerateObject()
                    .Where(s => s.Value.GetProperty("kind").GetString() == "release")
                    .Select(s => $"{name}@{s.Name}"));
            else if (e.TryGetProperty("source", out var source) && source.GetProperty("kind").GetString() == "release")
                keys.Add(name);
        }
        return keys.ToArray();
    }

    /// <summary>A temp file for a stamp key, named like the release asset it stands in for.</summary>
    private static string AssetFor(string dir, string key) => Path.Combine(dir, key.Replace('@', '-') + ".zip");

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var root = RepoRoot();
        var psi = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(root, "site", "build", "catalog.py"));
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// THE WHOLE POINT OF THE GENERATOR: the committed catalog cannot carry a hash of an artifact
    /// built after it was committed, so the published one is where the hash appears.
    /// </summary>
    [Fact]
    public void TheGeneratedCatalogCarriesTheAssetsHash()
    {
        var root = RepoRoot();
        var dir = Directory.CreateTempSubdirectory("catalog-gen-").FullName;
        try
        {
            // EVERY RELEASE-SOURCED ENTRY GETS AN ASSET, or the generator refuses — its
            // forgotten-plugin check doing its job. Different bytes per asset, so a stamp landing
            // on the wrong entry cannot pass.
            var assets = ReleasePlugins(root)
                .ToDictionary(key => key, key => AssetFor(dir, key));
            foreach (var (name, asset) in assets)
                File.WriteAllText(asset, $"not really a zip, but {name}'s hashes the same way");
            var outPath = Path.Combine(dir, "catalog.json");

            var args = new List<string>
            {
                "--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--out", outPath,
            };
            foreach (var (name, asset) in assets)
            {
                args.Add("--plugin");
                args.Add($"{name}={asset}");
            }

            var (exit, _, stderr) = Run(args.ToArray());

            Assert.True(exit == 0, $"generator failed: {stderr}");

            var published = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement;
            foreach (var entry in published.GetProperty("plugins").EnumerateArray())
            {
                var name = entry.GetProperty("name").GetString()!;
                // ONE HASH PER DOWNLOAD: a per-platform entry carries one under each of its sources.
                var stamped = entry.TryGetProperty("sources", out var sources)
                    ? sources.EnumerateObject().Select(s => (Key: $"{name}@{s.Name}", Sha: s.Value.GetProperty("sha256").GetString()))
                    : [(Key: name, Sha: entry.GetProperty("source").GetProperty("sha256").GetString())];
                foreach (var (key, sha) in stamped)
                {
                    if (!assets.TryGetValue(key, out var asset)) continue;

                    // The expected value, computed independently of the generator.
                    using var stream = File.OpenRead(asset);
                    var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
                        .ToLowerInvariant();

                    Assert.Equal(expected, sha);
                }
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The $comment block is 60 lines of maintainer reasoning about how to edit the source file.
    /// A client fetching the catalog over the network pays for it on every request and can do
    /// nothing with it.
    /// </summary>
    [Fact]
    public void TheGeneratedCatalogDropsTheMaintainerComment()
    {
        var root = RepoRoot();
        var dir = Directory.CreateTempSubdirectory("catalog-gen-").FullName;
        try
        {
            // One placeholder file serves every entry: this test is about the $comment, and the
            // generator only hashes whatever path each --plugin names.
            var asset = Path.Combine(dir, "asset.zip");
            File.WriteAllText(asset, "x");
            var outPath = Path.Combine(dir, "catalog.json");

            var args = new List<string>
            {
                "--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--out", outPath,
            };
            foreach (var name in ReleasePlugins(root))
            {
                args.Add("--plugin");
                args.Add($"{name}={asset}");
            }

            Run(args.ToArray());

            var published = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement;
            Assert.False(published.TryGetProperty("$comment", out _));
            Assert.True(published.TryGetProperty("plugins", out _));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A MISSING ASSET FAILS LOUDLY. Publishing a catalog with a null hash would look exactly like
    /// today's committed file, so the one thing this job exists to add would be silently absent.
    /// </summary>
    [Fact]
    public void AMissingAssetFailsRatherThanPublishingANullHash()
    {
        var root = RepoRoot();
        var dir = Directory.CreateTempSubdirectory("catalog-gen-").FullName;
        try
        {
            var (exit, _, stderr) = Run(
                "--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--plugin", $"csharp-lsp={Path.Combine(dir, "nothing-here.zip")}",
                "--out", Path.Combine(dir, "catalog.json"));

            Assert.Equal(1, exit);
            Assert.Contains("nothing-here.zip", stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// CXAGENT'S VERSION IS PUBLISHED SEPARATELY FROM ANY PLUGIN'S, because the two diverge by
    /// design: a plugin that did not change keeps its own number across a release, so the site
    /// reading plugins[0].version showed v0.9.6 on the day v0.9.7 shipped.
    ///
    /// <para>Optional, because a local run has no release to name — and its absence must leave the
    /// key out rather than write an empty one, which the site would render as "v".</para>
    /// </summary>
    [Fact]
    public void TheGeneratedCatalogCarriesCxagentsOwnVersion()
    {
        var root = RepoRoot();
        var dir = Directory.CreateTempSubdirectory("catalog-ver-").FullName;
        try
        {
            var assets = ReleasePlugins(root)
                .ToDictionary(key => key, key => AssetFor(dir, key));
            foreach (var (name, asset) in assets)
                File.WriteAllText(asset, $"bytes for {name}");

            var withVersion = Path.Combine(dir, "with.json");
            var args = new List<string>
            {
                "--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--out", withVersion, "--version", "9.9.9",
            };
            foreach (var (name, asset) in assets) { args.Add("--plugin"); args.Add($"{name}={asset}"); }

            var run = Run(args.ToArray());
            Assert.True(run.Exit == 0, $"generator failed: {run.Stderr}");

            using var stamped = JsonDocument.Parse(File.ReadAllText(withVersion));
            Assert.Equal("9.9.9", stamped.RootElement.GetProperty("cxagentVersion").GetString());

            // AND IT IS NOT A PLUGIN'S NUMBER. The committed catalog's own versions stay whatever
            // they are; stamping the app's version must not touch them.
            foreach (var plugin in stamped.RootElement.GetProperty("plugins").EnumerateArray())
                Assert.NotEqual("9.9.9", plugin.GetProperty("version").GetString());

            // OMITTED LEAVES THE KEY OUT ENTIRELY.
            var without = Path.Combine(dir, "without.json");
            args[args.IndexOf("--out") + 1] = without;
            args.Remove("--version");
            args.Remove("9.9.9");

            var bare = Run(args.ToArray());
            Assert.True(bare.Exit == 0, $"generator failed: {bare.Stderr}");
            using var plain = JsonDocument.Parse(File.ReadAllText(without));
            Assert.False(plain.RootElement.TryGetProperty("cxagentVersion", out _),
                "omitting --version must leave cxagentVersion out, not write an empty one.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string PerPlatformCatalog = """
        {
          "schemaVersion": 2,
          "plugins": [
            {
              "name": "demo",
              "sources": {
                "linux-x64": { "kind": "release", "sha256": null, "latest": "https://github.com/nickprotop/cxagent/releases/latest/download/demo-linux-x64.zip" },
                "win-x64":   { "kind": "release", "sha256": null, "latest": "https://github.com/nickprotop/cxagent/releases/latest/download/demo-win-x64.zip" }
              }
            }
          ]
        }
        """;

    private static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// A PER-PLATFORM ENTRY IS STAMPED PER PLATFORM: each platform's zip is a different file with its
    /// own hash, and a client verifies the one it downloads.
    /// </summary>
    [Fact]
    public void APerPlatformEntryGetsEachPlatformsHash()
    {
        var dir = Directory.CreateTempSubdirectory("catalog-gen-").FullName;
        try
        {
            var catalog = Path.Combine(dir, "plugins.json");
            File.WriteAllText(catalog, PerPlatformCatalog);
            var linux = Path.Combine(dir, "demo-linux-x64.zip");
            var windows = Path.Combine(dir, "demo-win-x64.zip");
            File.WriteAllText(linux, "linux bytes");
            File.WriteAllText(windows, "windows bytes");
            var outPath = Path.Combine(dir, "out.json");

            var (exit, _, stderr) = Run("--catalog", catalog, "--out", outPath,
                "--plugin", $"demo@linux-x64={linux}", "--plugin", $"demo@win-x64={windows}");

            Assert.True(exit == 0, $"generator failed: {stderr}");
            var sources = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement
                .GetProperty("plugins")[0].GetProperty("sources");
            Assert.Equal(Sha(linux), sources.GetProperty("linux-x64").GetProperty("sha256").GetString());
            Assert.Equal(Sha(windows), sources.GetProperty("win-x64").GetProperty("sha256").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A PLATFORM LEFT UNSTAMPED IS A DOWNLOAD NO CLIENT CAN VERIFY, so the catalog is not published.</summary>
    [Fact]
    public void APerPlatformEntryMissingOnePlatformsZip_Fails()
    {
        var dir = Directory.CreateTempSubdirectory("catalog-gen-").FullName;
        try
        {
            var catalog = Path.Combine(dir, "plugins.json");
            File.WriteAllText(catalog, PerPlatformCatalog);
            var linux = Path.Combine(dir, "demo-linux-x64.zip");
            File.WriteAllText(linux, "linux bytes");

            var (exit, _, stderr) = Run("--catalog", catalog, "--out", Path.Combine(dir, "out.json"),
                "--plugin", $"demo@linux-x64={linux}");

            Assert.NotEqual(0, exit);
            Assert.Contains("demo@win-x64", stderr);
            Assert.False(File.Exists(Path.Combine(dir, "out.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
