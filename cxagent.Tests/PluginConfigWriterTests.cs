using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Writing a plugin's entry into config.json — the file the user also edits by hand.
///
/// <para>MOST CONFIGS HAVE NO plugins KEY AT ALL. One written by the setup wizard carries providers,
/// classifier, mcp, agents and orchestrator; nothing has ever written a plugins block. So the common
/// operation is creating one, not editing one.</para>
/// </summary>
public class PluginConfigWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-config-" + Guid.NewGuid().ToString("N"));

    public PluginConfigWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private string Path_ => System.IO.Path.Combine(_dir, "config.json");

    private void Write(string json) => File.WriteAllText(Path_, json);

    private JsonElement Read()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void AConfigWithNoPluginsBlockGainsOne()
    {
        Write("""{ "defaultProvider": "local" }""");

        PluginConfigWriter.Upsert(Path_, "csharp-lsp", new PluginConfig("csharp-lsp.dll"));

        var plugins = Read().GetProperty("plugins");
        Assert.Equal("csharp-lsp.dll", plugins.GetProperty("csharp-lsp").GetProperty("file").GetString());
    }

    /// <summary>
    /// EVERY OTHER KEY SURVIVES. config.json is the user's file; a write that dropped a section they
    /// had configured would be a data loss dressed as a feature.
    /// </summary>
    [Fact]
    public void EverythingElseInTheFileIsPreserved()
    {
        Write("""
            {
              "//providers": ["what this section does"],
              "providers": { "local": { "baseUrl": "http://localhost:8771/v1" } },
              "defaultProvider": "local"
            }
            """);

        PluginConfigWriter.Upsert(Path_, "demo", new PluginConfig("demo.dll"));

        var root = Read();
        Assert.True(root.TryGetProperty("//providers", out _), "the sibling comment key must survive.");
        Assert.Equal("local", root.GetProperty("defaultProvider").GetString());
        Assert.Equal("http://localhost:8771/v1",
            root.GetProperty("providers").GetProperty("local").GetProperty("baseUrl").GetString());
    }

    /// <summary>Two entries can name one binary, so upserting one must not disturb the other.</summary>
    [Fact]
    public void AnotherEntryNamingTheSameFileIsUntouched()
    {
        Write("""
            {
              "plugins": {
                "csharp-lsp": { "file": "csharp-lsp.dll", "settings": { "server": "csharp-ls" } },
                "csharp-lsp-omnisharp": { "file": "csharp-lsp.dll", "enabled": false }
              }
            }
            """);

        PluginConfigWriter.SetEnabled(Path_, "csharp-lsp", false);

        var plugins = Read().GetProperty("plugins");
        Assert.False(plugins.GetProperty("csharp-lsp").GetProperty("enabled").GetBoolean());
        Assert.Equal("csharp-ls",
            plugins.GetProperty("csharp-lsp").GetProperty("settings").GetProperty("server").GetString());
        Assert.False(plugins.GetProperty("csharp-lsp-omnisharp").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void RemovingAnEntryLeavesTheOthers()
    {
        Write("""
            {
              "plugins": {
                "a": { "file": "a.dll" },
                "b": { "file": "b.dll" }
              }
            }
            """);

        PluginConfigWriter.Remove(Path_, "a");

        var plugins = Read().GetProperty("plugins");
        Assert.False(plugins.TryGetProperty("a", out _));
        Assert.True(plugins.TryGetProperty("b", out _));
    }

    /// <summary>
    /// THE FILE IS READ FRESH EVERY TIME. A user with config.json open in an editor is normal, and
    /// a writer holding a copy parsed earlier would discard whatever they changed since.
    /// </summary>
    [Fact]
    public void AChangeMadeBetweenWritesIsNotDiscarded()
    {
        Write("""{ "plugins": { "a": { "file": "a.dll" } } }""");

        PluginConfigWriter.Upsert(Path_, "b", new PluginConfig("b.dll"));

        // The user edits the file behind our back.
        var current = File.ReadAllText(Path_).TrimEnd().TrimEnd('}');
        File.WriteAllText(Path_, current + ", \"defaultProvider\": \"local\" }");

        PluginConfigWriter.Upsert(Path_, "c", new PluginConfig("c.dll"));

        var root = Read();
        Assert.Equal("local", root.GetProperty("defaultProvider").GetString());
        Assert.True(root.GetProperty("plugins").TryGetProperty("c", out _));
    }

    /// <summary>Settings are handed over verbatim, so they round-trip unexamined.</summary>
    [Fact]
    public void SettingsRoundTripUnchanged()
    {
        Write("{}");
        using var settings = JsonDocument.Parse("""{ "server": "csharp-ls", "args": ["-lsp"] }""");

        PluginConfigWriter.Upsert(Path_, "demo",
            new PluginConfig("demo.dll", Enabled: true, Settings: settings.RootElement.Clone()));

        var written = Read().GetProperty("plugins").GetProperty("demo").GetProperty("settings");
        Assert.Equal("csharp-ls", written.GetProperty("server").GetString());
        Assert.Equal("-lsp", written.GetProperty("args")[0].GetString());
    }

    /// <summary>
    /// A CONFIG FULL OF API KEYS MUST NOT BECOME WORLD-READABLE because a plugin row was written.
    /// The temp file is created under the caller's umask, so the mode is set before the rename
    /// rather than after — there is no window in which the finished file is readable by others.
    /// </summary>
    [Fact]
    public void TheWrittenConfigIsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows()) return;

        PluginConfigWriter.Upsert(Path_, "csharp-lsp", new PluginConfig("csharp-lsp.dll"));

        var mode = File.GetUnixFileMode(Path_);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
