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

    /// <summary>A single entry carrying "directory" claims to be first-party; only the catalog's own entries are skipped.</summary>
    [Fact]
    public async Task ASingleEntryClaimingFirstParty_IsAProblem_NotASkip()
    {
        var (exit, output, _) = await Run("verify", await PackedEntry(e => e["directory"] = "harness"));

        Assert.Equal(1, exit);
        Assert.Contains("directory", output);
    }

    [Fact]
    public async Task APlatformsListInAnotherOrder_IsTheSamePlatforms()
    {
        var path = await PackedEntry(e =>
        {
            var zip = e["source"]!["url"]!.GetValue<string>();
            var sha = e["source"]!["sha256"]!.GetValue<string>();
            e.Remove("source");
            e["kind"] = "managed";
            e["compatibility"]!["platforms"] = new JsonArray("linux-arm64", "linux-x64");
            e["sources"] = new JsonObject
            {
                ["linux-x64"] = new JsonObject { ["kind"] = "url", ["url"] = zip, ["sha256"] = sha, ["file"] = "cxagent.Tests.PluginFixture.Harness.dll" },
                ["linux-arm64"] = new JsonObject { ["kind"] = "url", ["url"] = zip, ["sha256"] = sha, ["file"] = "cxagent.Tests.PluginFixture.Harness.dll" },
            };
            e["kind"] = "native";
        });

        var (_, output, _) = await Run("verify", path);

        Assert.DoesNotContain("compatibility.platforms", output);
    }

    [Fact]
    public async Task AMalformedFileUrl_IsAProblem_NotACrash()
    {
        var (exit, output, _) = await Run("verify", await PackedEntry(e => e["source"]!["url"] = "file://[not a uri"));

        Assert.Equal(1, exit);
        Assert.Contains("url", output);
    }

    [Fact]
    public async Task AFieldThatIsAbsent_IsMissing_NotQuotedEmpty()
    {
        var (_, output, _) = await Run("verify", await PackedEntry(e => e.Remove("publisher")));

        Assert.Contains("publisher: is missing", output);
    }

    /// <summary>The bot and catalog CI fetch what a user fetches: a path on the runner proves nothing about the upload.</summary>
    [Fact]
    public async Task ALocalFile_IsRefused_WithHttpsOnly()
    {
        var (exit, output, _) = await Run("verify", "--https-only", await PackedEntry());

        Assert.Equal(1, exit);
        Assert.Contains("source.url", output);
        Assert.Contains("https://", output);
    }

    [Fact]
    public async Task AFileUrl_IsRefused_WithHttpsOnly()
    {
        var path = await PackedEntry(e => e["source"]!["url"] = new Uri(e["source"]!["url"]!.GetValue<string>()).AbsoluteUri);

        var (exit, output, _) = await Run("verify", path, "--https-only");

        Assert.Equal(1, exit);
        Assert.Contains("source.url", output);
    }

    /// <summary>--json holds on every failure, so a script reading it never meets a plain line.</summary>
    [Fact]
    public async Task AnUnreadableEntry_WithJson_IsJson()
    {
        var path = Path.Combine(Folders.NewFolder("verify-"), "broken.catalog.json");
        File.WriteAllText(path, "{ this is not JSON");

        var (exit, output, _) = await Run("verify", path, "--json");

        Assert.Equal(1, exit);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var problem = doc.RootElement.GetProperty("problems")[0];
        Assert.Equal("broken.catalog.json", problem.GetProperty("entry").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("message").GetString()));
    }

    /// <summary>
    /// THE CATALOG'S SHAPE, refused before anything is fetched: what PluginCatalogTests requires of
    /// every entry, so an entry verify passes cannot turn master red once merged.
    /// </summary>
    [Theory]
    [InlineData("no-kind", "source.kind")]
    [InlineData("latest", "source.url")]
    [InlineData("release-kind", "source.kind")]
    public async Task AnEntryShapedUnlikeTheCatalog_IsRefused(string shape, string field)
    {
        var path = await PackedEntry(e =>
        {
            var source = e["source"]!.AsObject();
            switch (shape)
            {
                case "no-kind": source.Remove("kind"); break;
                case "latest": source["latest"] = source["url"]!.GetValue<string>(); source.Remove("url"); break;
                case "release-kind": source["kind"] = "release"; break;
            }
        });

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains(field, output);
    }

    [Fact]
    public async Task APlatformCxagentDoesNotRun_IsRefused()
    {
        var path = await PackedEntry(e =>
        {
            var source = e["source"]!.AsObject();
            e.Remove("source");
            source["file"] = e["file"]!.GetValue<string>();
            e["sources"] = new JsonObject { ["linux-musl-x64"] = source };
            e["kind"] = "native";
            e["compatibility"]!["platforms"] = new JsonArray("linux-musl-x64");
        });

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains("sources.linux-musl-x64", output);
        Assert.Contains("linux-x64", output);
    }

    /// <summary>Anyone can paste an entry now: a value of the wrong type is a problem on its field, never a crash.</summary>
    [Theory]
    [InlineData("platforms")]
    [InlineData("tools")]
    public async Task AValueOfTheWrongType_IsAProblem_NotACrash(string field)
    {
        var path = await PackedEntry(e =>
        {
            if (field == "platforms") e["compatibility"]!["platforms"] = new JsonArray(4);
            else e["tools"] = new JsonArray(4);
        });

        var (exit, output, _) = await Run("verify", path);

        Assert.Equal(1, exit);
        Assert.Contains(field, output);
    }
}
