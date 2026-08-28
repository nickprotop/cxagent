using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// THE HALF CORE DELIBERATELY DOES NOT DO. SessionManager's mutators change the live entries and
/// stop; Core never writes a file. This is what makes a change outlive the session, and it is the
/// app's because persistence is the app's — the same rule that put the writers in cxagent/UI.
/// </summary>
public class PluginConfigPersistenceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-persist-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public PluginConfigPersistenceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """
        {
          "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } },
          "//plugins": ["a comment sibling key"],
          "plugins": {
            "csharp-lsp": { "file": "csharp-lsp.dll", "enabled": true },
            "gone": { "file": "gone.dll", "enabled": true }
          }
        }
        """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private JsonElement Plugins() =>
        JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement.GetProperty("plugins");

    /// <summary>A disabled entry in memory becomes a disabled entry on disk.</summary>
    [Fact]
    public void AnEntryDisabledInMemoryIsWrittenDisabled()
    {
        var live = new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll", Enabled: false),
            ["gone"] = new("gone.dll"),
        };

        PluginConfigPersistence.Sync(ConfigPath, live);

        Assert.False(Plugins().GetProperty("csharp-lsp").GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// AN ENTRY REMOVED IN MEMORY LEAVES THE FILE. Syncing only additions would leave config naming a
    /// plugin the session no longer has, and the next start would resurrect it.
    /// </summary>
    [Fact]
    public void AnEntryRemovedInMemoryIsRemovedFromTheFile()
    {
        var live = new Dictionary<string, PluginConfig> { ["csharp-lsp"] = new("csharp-lsp.dll") };

        PluginConfigPersistence.Sync(ConfigPath, live);

        Assert.False(Plugins().TryGetProperty("gone", out _));
        Assert.True(Plugins().TryGetProperty("csharp-lsp", out _));
    }

    /// <summary>A name new to the file is added.</summary>
    [Fact]
    public void AnEntryAddedInMemoryIsWritten()
    {
        var live = new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll"),
            ["gone"] = new("gone.dll"),
            ["calculator"] = new("calculator.dll"),
        };

        PluginConfigPersistence.Sync(ConfigPath, live);

        Assert.Equal("calculator.dll",
            Plugins().GetProperty("calculator").GetProperty("file").GetString());
    }

    /// <summary>
    /// THE REST OF THE FILE SURVIVES, comment keys included. config.sample.json documents itself with
    /// "//"-prefixed siblings, and a writer that dropped them would silently strip a user's notes.
    /// </summary>
    [Fact]
    public void EverythingElseInTheFileIsUntouched()
    {
        PluginConfigPersistence.Sync(ConfigPath,
            new Dictionary<string, PluginConfig> { ["csharp-lsp"] = new("csharp-lsp.dll") });

        var root = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement;

        Assert.True(root.TryGetProperty("providers", out _));
        Assert.True(root.TryGetProperty("//plugins", out _));
    }

    /// <summary>
    /// A KEY THIS TYPE DOES NOT MODEL SURVIVES. PluginConfig knows file, enabled and settings; a user
    /// may have written others, and a sync that rebuilt each entry from the record would delete them
    /// silently — on every config change, for every plugin, forever.
    /// </summary>
    [Fact]
    public void AnUnmodelledKeyOnAnEntryIsNotDropped()
    {
        File.WriteAllText(ConfigPath,
            "{ \"providers\": { \"p\": { \"kind\": \"anthropic\", \"model\": \"m\", \"apiKey\": \"k\" } },"
          + "  \"plugins\": { \"csharp-lsp\": { \"file\": \"csharp-lsp.dll\","
          + "    \"//enabled\": \"why this one is off\", \"myOwnNote\": \"keep me\" } } }");

        PluginConfigPersistence.Sync(ConfigPath,
            new Dictionary<string, PluginConfig> { ["csharp-lsp"] = new("csharp-lsp.dll", Enabled: false) });

        var entry = Plugins().GetProperty("csharp-lsp");
        Assert.Equal("keep me", entry.GetProperty("myOwnNote").GetString());
        // The "//"-prefixed sibling idiom config.sample.json documents for itself, INSIDE an entry.
        // PluginConfigWriter's own header promises these survive a round trip; a sync that rebuilt
        // entries would break that promise for every plugin at once.
        Assert.Equal("why this one is off", entry.GetProperty("//enabled").GetString());
        Assert.False(entry.GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// AN ENABLED ENTRY CARRIES NO `enabled` KEY. Absent means true to the reader, so writing an
    /// explicit true would add noise to a file a user reads and edits by hand.
    /// </summary>
    [Fact]
    public void ReEnablingRemovesTheKeyRatherThanWritingTrue()
    {
        var off = new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll", Enabled: false),
            ["gone"] = new("gone.dll"),
        };
        var on = new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll"),
            ["gone"] = new("gone.dll"),
        };

        PluginConfigPersistence.Sync(ConfigPath, off);
        PluginConfigPersistence.Sync(ConfigPath, on);

        Assert.False(Plugins().GetProperty("csharp-lsp").TryGetProperty("enabled", out _));
    }

    /// <summary>A config with no plugins block gains one — the common case, since nothing has ever
    /// written one for most users.</summary>
    [Fact]
    public void AConfigWithNoPluginsBlockGainsOne()
    {
        File.WriteAllText(ConfigPath,
            """{ "providers": { "p": { "kind": "anthropic", "model": "m", "apiKey": "k" } } }""");

        PluginConfigPersistence.Sync(ConfigPath,
            new Dictionary<string, PluginConfig> { ["calculator"] = new("calculator.dll") });

        Assert.Equal("calculator.dll",
            Plugins().GetProperty("calculator").GetProperty("file").GetString());
    }
}
