using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That CORE can load this plugin — the half no test inside the plugin can see.
/// </summary>
public class TriggersPluginLoadTests
{
    /// <summary>
    /// Walks up from the test binary's own folder to find the repo root, rather than string-slicing
    /// <see cref="AppContext.BaseDirectory"/> at "cxagent.Tests" — that slice breaks the moment a
    /// fixture or output path itself contains that substring, and this repo already has fixtures
    /// named <c>cxagent.Tests.PluginFixture*</c> under this very test project.
    /// </summary>
    private static string SidecarPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cxagent.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not find repo root (cxagent.slnx) above " + AppContext.BaseDirectory);

        // THE SOURCE SIDECAR, NOT A BUILD OUTPUT. Reading bin/Debug/ makes this test pass or fail on
        // whether somebody happened to build that configuration: green on a developer machine with a
        // Debug build lying around, and a DirectoryNotFoundException in CI, which builds Release and
        // has no reason to build this plugin at all. The csproj copies this exact file to the output,
        // so the source copy is the same bytes without the dependency on how the tree was built.
        return Path.Combine(dir.FullName, "plugins", "triggers", "triggers.plugin.json");
    }

    [Fact]
    public void The_sidecar_parses_with_Cores_own_parser()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(SidecarPath())).Manifest!;

        Assert.Equal("triggers", manifest.Name);
        Assert.Equal(5, manifest.Tools.Count);
        Assert.Equal(4, manifest.Commands.Count);
    }

    /// <summary>
    /// THE CLIENT IS DECLARED IN THE MANIFEST AND TYPE-TESTED IN THE BINARY — two gates, one
    /// direction. Declaring it without implementing IPluginClientConsumer is a lie the loader
    /// refuses; implementing it without declaring is merely unused.
    /// </summary>
    [Fact]
    public void The_manifest_declares_the_client_capability()
    {
        Assert.True(PluginManifest.Parse(File.ReadAllText(SidecarPath())).Manifest!.Client);
    }

    /// <summary>
    /// ALL FOUR COMMAND NAMES ARE FREE. SessionManager.SeedCommands claims /sessions, /stats, /mcp
    /// and /plugin; a command-name collision refuses the WHOLE plugin, so every name this declares is
    /// another way for the load to fail for a reason nobody chose.
    /// </summary>
    [Fact]
    public void No_declared_command_collides_with_a_built_in()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(SidecarPath())).Manifest!;
        var builtIn = new[]
        {
            "clear", "help", "init", "stats", "agents", "skills", "diff", "mcp", "sessions",
            "model", "mode", "trust", "compress", "plugin",
        };

        Assert.All(manifest.Commands,
            c => Assert.DoesNotContain(c.Name, builtIn));
    }

    /// <summary>
    /// AND ONLY trigger_on_exit ASKS. PluginToolManifest.Gated defaults to Never, so a manifest that
    /// says nothing ships tools that never ask — right for four of these and wrong for the fifth.
    /// </summary>
    [Fact]
    public void Only_the_tool_that_runs_a_command_is_gated()
    {
        var manifest = PluginManifest.Parse(File.ReadAllText(SidecarPath())).Manifest!;

        Assert.Equal(
            ["trigger_on_exit"],
            manifest.Tools.Where(t => t.Gated == PluginGating.Dynamic)
                .Select(t => t.Name).ToArray());
    }
}
