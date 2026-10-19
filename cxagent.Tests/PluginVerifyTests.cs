using System.Text.Json.Nodes;
using Xunit;

namespace CxAgent.Tests;

/// <summary><c>cxagent plugin verify</c>: what an install would get, checked against what the entry claims, with no plugin code run.</summary>
public class PluginVerifyTests
{
    private static Task<CliRun.Outcome> Run(params string[] args) => CliRun.Run(args);

    // A PACKED HARNESS PLUGIN, ITS ENTRY POINTING AT THE ZIP ON DISK — what an author verifies before uploading.
    private static async Task<string> PackedEntry(Action<JsonObject>? change = null)
    {
        var outDir = Folders.NewFolder("verify-");
        await Run("pack", Folders.Harness(), "--out", outDir, "--description", "d", "--publisher", "p", "--license", "MIT", "--repository", "https://example.org/r");
        var path = Path.Combine(outDir, "harness.catalog.json");
        var entry = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        entry["source"]!["url"] = Path.Combine(outDir, "harness.zip");
        change?.Invoke(entry);
        File.WriteAllText(path, entry.ToJsonString());
        return path;
    }

    [Fact]
    public async Task APackedPlugin_Verifies()
    {
        var (exit, output, _) = await Run("verify", await PackedEntry());
        Assert.Equal(0, exit);
        Assert.Contains("ok", output);
    }

    [Fact]
    public async Task APlaceholder_IsRefused_BeforeAnyDownload()
    {
        var path = await PackedEntry(e => { e["source"]!["url"] = "<upload URL>"; e["description"] = "<fill in: description>"; });

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains("source.url", output);
        Assert.Contains("description", output);
    }

    [Fact]
    public async Task PlainHttp_IsRefused()
    {
        var (exit, output, _) = await Run("verify", await PackedEntry(e => e["source"]!["url"] = "http://example.org/harness.zip"));
        Assert.Equal(1, exit);
        Assert.Contains("https", output);
    }

    [Fact]
    public async Task AHashThatDiffers_IsRefused()
    {
        var (exit, output, _) = await Run("verify", await PackedEntry(e => e["source"]!["sha256"] = new string('0', 64)));
        Assert.Equal(1, exit);
        Assert.Contains("sha256", output);
    }

    [Fact]
    public async Task AnEntryTheZipContradicts_NamesEachField()
    {
        var path = await PackedEntry(e =>
        {
            e["version"] = "9.9.9";
            e["tools"]!.AsArray().Add(new JsonObject { ["name"] = "extra", ["gated"] = false });
        });

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains("version", output);
        Assert.Contains("extra", output);
    }

    [Fact]
    public async Task AZipThatEscapes_IsRefused()
    {
        var path = await PackedEntry();
        var zipPath = Path.Combine(Path.GetDirectoryName(path)!, "harness.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
            zip.CreateEntry("../escape.txt");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        entry["source"]!["sha256"] = hash;
        File.WriteAllText(path, entry.ToJsonString());

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains("escape", output);
    }

    [Fact]
    public async Task APluginsJson_IsReadToo_AndFirstPartyEntriesAreSkipped()
    {
        var single = await PackedEntry();
        var doc = new JsonObject
        {
            ["plugins"] = new JsonArray(
                JsonNode.Parse(File.ReadAllText(single)),
                new JsonObject { ["name"] = "calculator", ["directory"] = "calculator" }),
        };
        var path = Path.Combine(Path.GetDirectoryName(single)!, "plugins.json");
        File.WriteAllText(path, doc.ToJsonString());

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(0, exit);
        Assert.Contains("calculator: first-party, skipped", output);
    }

    /// <summary>"file" is a name inside the zip, never a path — "../" would point verify at a file outside it.</summary>
    [Fact]
    public async Task AFileThatIsAPath_IsRefused()
    {
        // FROM verify's TEMP FOLDER, "../.." REACHES THE ROOT, so this names a real .NET assembly outside the zip.
        var outside = "../.." + Path.Combine(AppContext.BaseDirectory, "cxagent.Tests.PluginFixture.Harness.dll");
        var (exit, output, _) = await Run("verify", await PackedEntry(e => e["file"] = outside));

        Assert.Equal(1, exit);
        Assert.Contains("file", output);
    }

    /// <summary>The binary and its sidecar must share a stem, or the loader cannot find the sidecar.</summary>
    [Fact]
    public async Task ABinaryItsSidecarDoesNotMatch_IsRefused()
    {
        var path = await PackedEntry();
        var zipPath = Path.Combine(Path.GetDirectoryName(path)!, "harness.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
        {
            var original = zip.GetEntry("cxagent.Tests.PluginFixture.Harness.dll")!;
            using (var from = original.Open())
            using (var to = zip.CreateEntry("renamed.dll").Open())
                from.CopyTo(to);
            original.Delete();
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        entry["source"]!["sha256"] = hash;
        entry["file"] = "renamed.dll";
        File.WriteAllText(path, entry.ToJsonString());

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains("renamed.plugin.json", output);
    }
}
