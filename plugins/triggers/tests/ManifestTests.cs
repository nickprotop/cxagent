using System.Text.Json;
using Xunit;

namespace CxAgent.Plugins.Triggers.Tests;

/// <summary>
/// The sidecar is what Core reads before this assembly is loaded at all, so its shape is checked
/// here rather than trusted.
/// </summary>
public class ManifestTests
{
    private static JsonElement Sidecar()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "triggers.plugin.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void It_declares_contract_three()
    {
        Assert.Equal(3, Sidecar().GetProperty("pluginContract").GetInt32());
    }

    /// <summary>
    /// THE CLIENT IS DECLARED, and that is what makes IPluginContext.Client non-null. A plugin that
    /// submits without declaring it gets a null client and fails at the first fire.
    /// </summary>
    [Fact]
    public void It_declares_the_client_capability()
    {
        Assert.True(Sidecar().GetProperty("client").GetBoolean());
    }

    [Fact]
    public void It_declares_five_tools_and_four_commands()
    {
        var root = Sidecar();

        Assert.Equal(5, root.GetProperty("tools").GetArrayLength());
        Assert.Equal(4, root.GetProperty("commands").GetArrayLength());
    }

    /// <summary>
    /// ONLY trigger_on_exit IS GATED, and the line is "does this execute something" rather than
    /// "does this have effects". A scheduled prompt's turn is itself fully governed when it runs, so
    /// gating the scheduling too would ask twice for one thing and train the user to click through.
    /// trigger_on_exit starts a process NOW, with arguments the model composed, and nothing
    /// downstream will ask about it again.
    /// </summary>
    [Fact]
    public void Only_the_tool_that_runs_a_command_is_gated()
    {
        var gated = Sidecar().GetProperty("tools").EnumerateArray()
            .Where(t => t.TryGetProperty("gated", out var g)
                        && g.ValueKind == JsonValueKind.String
                        && g.GetString() == "dynamic")
            // NON-NULL BY CONSTRUCTION: every tool in the sidecar has a name, and a manifest that
            // lost one would fail the count assertions above long before reaching here.
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(["trigger_on_exit"], gated);
    }

    /// <summary>
    /// A COMMAND IS DECLARED WITHOUT ITS SLASH. Core adds it — see PluginRegistry — so a manifest
    /// cannot smuggle a namespace into a command name.
    /// </summary>
    [Fact]
    public void Commands_are_declared_without_a_leading_slash()
    {
        var names = Sidecar().GetProperty("commands").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!)
            .ToArray();

        Assert.All(names, n => Assert.False(n.StartsWith('/'), $"'{n}' carries its own slash"));
        Assert.Equal(
            ["triggers-add", "triggers-list", "triggers-update", "triggers-cancel"],
            names.OrderBy(n => n).ToArray().OrderBy(n => Array.IndexOf(
                new[] { "triggers-add", "triggers-list", "triggers-update", "triggers-cancel" }, n))
                .ToArray());
    }
}
