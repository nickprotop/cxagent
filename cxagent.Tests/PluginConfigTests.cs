using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The CONFIG layer of PLUGINS.md's "Name collisions" matrix — rows 2 and 4-plugin, the only ones
/// catchable before anything runs, plus row 9 (a runtime load still catches what config-time
/// validation could not see) and the shape of <c>plugins</c>/<c>pluginPaths</c> themselves.
///
/// <para>NO ASSEMBLIES, ONLY SIDECARS. A fixture here is a bare directory holding a <c>.dll</c> stub
/// (never loaded — <see cref="ProviderConfigLoader"/> must not need to) and a real
/// <c>&lt;name&gt;.plugin.json</c> sidecar, matching <see cref="ManagedPluginLoader"/>'s own
/// stem-based pairing. The stub file only has to EXIST for <c>File.Exists</c> to find it; its bytes
/// are never read.</para>
/// </summary>
public class PluginConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cxagent-plugin-cfg-" + Guid.NewGuid().ToString("N"));
    public PluginConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_dir, "config.json"), json);
    private AppPaths Paths() => new(_dir);
    private static readonly Dictionary<string, string> NoEnv = new();

    /// <summary>Drops a stub entry-point file plus a real sidecar under <c>&lt;ConfigDir&gt;/plugins</c>
    /// — everything <see cref="ProviderConfigLoader.FindPluginSidecar"/> needs to find and read a
    /// plugin's declared tool names without loading anything.</summary>
    private void WritePluginFixture(string fileName, string manifestName, params string[] toolNames)
    {
        var pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, fileName), "not a real assembly");

        var tools = string.Join(",", toolNames.Select(t =>
            $$"""{ "name": "{{t}}", "description": "d", "inputSchema": { "type": "object" } }"""));
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(pluginsDir, stem + ".plugin.json"), $$"""
        { "name": "{{manifestName}}", "version": "1.0.0", "spawns": false, "tools": [ {{tools}} ] }
        """);
    }

    private const string PluginPathsBlock = """ "pluginPaths": ["plugins"], """;

    // A MINIMAL VALID PROVIDER BLOCK, since LoadAndValidate refuses a config with none — every test
    // below is about the plugins block, not providers, so this is boilerplate rather than content.
    private const string ProviderBlock = """
        "providers": { "claude": { "kind": "anthropic", "apiKey": "sk-ant", "model": "claude-x" } },
        "defaultProvider": "claude"
        """;

    // ---- Shape: plugins keyed by name, pluginPaths a sibling ---------------------------------

    [Fact]
    public void Loads_PluginsKeyedByName_And_PluginPathsAsASibling()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_definition");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll", "settings": { "server": "rust-analyzer" } }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.Single(s.Plugins);
        Assert.Equal("lsp-rust.dll", s.Plugins["lsp-rust"].File);
        Assert.True(s.Plugins["lsp-rust"].Enabled);
        Assert.Equal(["plugins"], s.PluginPaths);
    }

    [Fact]
    public void EnabledFalse_IsTheGate_NotAFilter()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_definition");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll", "enabled": false }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);

        Assert.False(s.Plugins["lsp-rust"].Enabled);
    }

    // ---- Row 2: plugin x plugin, config read -----------------------------------------------------

    [Fact]
    public void Row2_TwoPluginsDeclaringTheSameTool_RefusesToStart()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_rename");
        WritePluginFixture("lsp-go.dll", "lsp-go", "lsp_rename");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll" },
            "lsp-go":   { "file": "lsp-go.dll" }
          }
        }
        """);

        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));

        // NAMES THE FILE AND THE KEY, exactly as an unknown provider kind does.
        Assert.Contains(ex.Errors, e => e.Contains("plugins.lsp-go") && e.Contains("lsp_rename")
            && e.Contains("lsp-go.plugin.json") && e.Contains("plugins.lsp-rust"));
    }

    [Fact]
    public void Row2_DoesNotFire_WhenTheColliderIsDisabled()
    {
        // ROW 5: a disabled plugin's names are free — collision validation only reads enabled entries.
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_rename");
        WritePluginFixture("lsp-go.dll", "lsp-go", "lsp_rename");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll" },
            "lsp-go":   { "file": "lsp-go.dll", "enabled": false }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(2, s.Plugins.Count);
    }

    // ---- Row 4 (plugin half): plugin x an ENABLED built-in, config read --------------------------

    [Fact]
    public void Row4Plugin_ATool_NamedAfterABuiltin_RefusesToStart()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "read_file");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll" }
          }
        }
        """);

        var ex = Assert.Throws<ProviderConfigException>(() =>
            ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv));

        Assert.Contains(ex.Errors, e => e.Contains("plugins.lsp-rust") && e.Contains("read_file")
            && e.Contains("built-in"));
    }

    // ---- No false positives ------------------------------------------------------------------

    [Fact]
    public void DistinctToolNames_LoadCleanly()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_definition", "lsp_rename");
        WritePluginFixture("lsp-go.dll", "lsp-go", "lsp_go_definition");
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll" },
            "lsp-go":   { "file": "lsp-go.dll" }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Equal(2, s.Plugins.Count);
    }

    /// <summary>A plugin whose file cannot be found from any configured search folder is not an
    /// error at config read — nothing here can tell a genuine typo from a plugin that only resolves
    /// against a project-local folder Core does not know about yet. The runtime load is where an
    /// unresolvable file is actually reported.</summary>
    [Fact]
    public void UnresolvableFile_IsNotAConfigError()
    {
        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "does-not-exist.dll" }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Single(s.Plugins);
    }

    /// <summary>A malformed sidecar is "cannot check this one", not a config error — refusing to
    /// start over a sidecar the user has not finished writing would take providers and every other
    /// plugin down with it. ManagedPluginLoader.Load is where a broken manifest is actually
    /// reported, when the plugin it belongs to is really loaded.</summary>
    [Fact]
    public void MalformedSidecar_IsNotAConfigError()
    {
        var pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, "lsp-rust.dll"), "not a real assembly");
        File.WriteAllText(Path.Combine(pluginsDir, "lsp-rust.plugin.json"), "{ not valid json");

        WriteConfig($$"""
        {
          {{ProviderBlock}},
          {{PluginPathsBlock}}
          "plugins": {
            "lsp-rust": { "file": "lsp-rust.dll" }
          }
        }
        """);

        var s = ProviderConfigLoader.LoadAndValidate(Paths(), NoEnv);
        Assert.Single(s.Plugins);
    }

    // ---- AgentConfig gets the same validation ------------------------------------------------

    [Fact]
    public void AgentConfig_Resolve_CatchesTheSameRow2Collision()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_rename");
        WritePluginFixture("lsp-go.dll", "lsp-go", "lsp_rename");

        var config = new AgentConfig
        {
            Models = { ["claude"] = new ModelConfig(ProviderKind.Anthropic, "claude-x") { ApiKey = "sk-ant" } },
            PluginPaths = { Path.Combine(_dir, "plugins") },
            Plugins =
            {
                ["lsp-rust"] = new PluginConfig("lsp-rust.dll"),
                ["lsp-go"] = new PluginConfig("lsp-go.dll"),
            },
        };

        var resolved = config.Resolve();

        Assert.False(resolved.HasProvider);
        Assert.Contains(resolved.Errors, e => e.Contains("plugins.lsp-go") && e.Contains("lsp_rename"));
    }

    [Fact]
    public void AgentConfig_Resolve_CarriesPluginsThrough_WhenClean()
    {
        WritePluginFixture("lsp-rust.dll", "lsp-rust", "lsp_definition");

        var config = new AgentConfig
        {
            Models = { ["claude"] = new ModelConfig(ProviderKind.Anthropic, "claude-x") { ApiKey = "sk-ant" } },
            PluginPaths = { Path.Combine(_dir, "plugins") },
            Plugins = { ["lsp-rust"] = new PluginConfig("lsp-rust.dll") },
        };

        var resolved = config.Resolve();

        Assert.True(resolved.HasProvider);
        Assert.Single(resolved.Plugins);
        Assert.Equal(["lsp-rust"], resolved.Plugins.Keys);
    }

    // ---- Row 9: a runtime load still refuses what config-time validation already caught ------------
    //
    // Row 9 says a plugin LOADED AT RUNTIME clashing with any earlier row is refused at load — that
    // is Session.LoadPlugin's job (PluginRegistryTests / PluginLoadGateTests already cover it), not
    // this loader's. What belongs here is only that config-time validation does not somehow let a
    // row-2/row-4 collision through silently — covered by the Refuses tests above.
}
