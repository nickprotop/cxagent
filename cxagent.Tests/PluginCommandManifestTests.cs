using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

public class PluginCommandManifestTests
{
    [Fact]
    public void A_manifest_can_declare_a_command()
    {
        var result = PluginManifest.Parse("""
            {"pluginContract":3,"name":"sched","version":"1.0.0","tools":[],
             "commands":[{"name":"schedule","summary":"Wake the agent later",
                          "arguments":[{"name":"when","summary":"When to wake"}]}]}
            """);

        Assert.True(result.IsSuccess);
        var command = Assert.Single(result.Manifest!.Commands);
        Assert.Equal("schedule", command.Name);
        Assert.Equal("when", Assert.Single(command.Args).Name);
    }

    [Fact]
    public void A_manifest_declaring_no_commands_has_an_empty_list_not_null()
    {
        // EVERY CONSUMER ENUMERATES WITHOUT A GUARD — the same promise SessionCommand.Args makes.
        var result = PluginManifest.Parse(
            """{"pluginContract":3,"name":"plain","version":"1.0.0","tools":[]}""");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Manifest!.Commands);
    }
}
