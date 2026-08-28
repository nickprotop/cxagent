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
    /// The release-sourced plugin names, read from the catalog itself rather than hardcoded.
    /// The generator refuses to publish any release entry nobody stamped, so a hardcoded list
    /// here goes stale the moment an entry is added — and these tests would then fail over a
    /// plugin they never heard of instead of testing anything.
    /// </summary>
    private static string[] ReleasePlugins(string root)
    {
        var catalog = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "plugins", "plugins.json")));
        return catalog.RootElement.GetProperty("plugins").EnumerateArray()
            .Where(e => e.TryGetProperty("source", out var source)
                        && source.GetProperty("kind").GetString() == "release")
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();
    }

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
                .ToDictionary(name => name, name => Path.Combine(dir, $"{name}.zip"));
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
                if (!assets.TryGetValue(name, out var asset)) continue;

                var sha = entry.GetProperty("source").GetProperty("sha256").GetString();

                // The expected value, computed independently of the generator.
                using var stream = File.OpenRead(asset);
                var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
                    .ToLowerInvariant();

                Assert.Equal(expected, sha);
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
}
