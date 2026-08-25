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
            var asset = Path.Combine(dir, "csharp-lsp.zip");
            File.WriteAllText(asset, "not really a zip, but it hashes the same way");
            var outPath = Path.Combine(dir, "catalog.json");

            var (exit, _, stderr) = Run(
                "--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--asset", asset,
                "--out", outPath);

            Assert.True(exit == 0, $"generator failed: {stderr}");

            var published = JsonDocument.Parse(File.ReadAllText(outPath)).RootElement;
            var entry = published.GetProperty("plugins").EnumerateArray().Single();
            var sha = entry.GetProperty("source").GetProperty("sha256").GetString();

            // The expected value, computed independently of the generator.
            using var stream = File.OpenRead(asset);
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))
                .ToLowerInvariant();

            Assert.Equal(expected, sha);
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
            var asset = Path.Combine(dir, "csharp-lsp.zip");
            File.WriteAllText(asset, "x");
            var outPath = Path.Combine(dir, "catalog.json");

            Run("--catalog", Path.Combine(root, "plugins", "plugins.json"),
                "--asset", asset, "--out", outPath);

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
                "--asset", Path.Combine(dir, "nothing-here.zip"),
                "--out", Path.Combine(dir, "catalog.json"));

            Assert.Equal(1, exit);
            Assert.Contains("nothing-here.zip", stderr);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
