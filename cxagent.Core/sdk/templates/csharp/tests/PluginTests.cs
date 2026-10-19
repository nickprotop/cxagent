using CxAgent.Plugins.Testing;
using Xunit;

public class PluginTests
{
    // THE PLUGIN'S OWN BUILD OUTPUT — the folder a user would install.
    private static readonly string PluginFolder = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Debug", "net10.0"));

    [Fact]
    public async Task ItLoads()
    {
        Assert.Empty(await PluginHarness.CheckAsync(PluginFolder));
    }

    [Fact]
    public async Task ItGreets_WithoutAsking()
    {
        await using var plugin = await PluginHarness.LoadAsync(PluginFolder);

        var call = await plugin.CallAsync("CXP_NAME_greet", new { name = "Ada" });

        Assert.Null(call.Gate);
        Assert.Equal("Hello, Ada!", call.Result!.Output["content"]);
    }

    [Fact]
    public async Task GreetingAdmin_Asks()
    {
        await using var plugin = await PluginHarness.LoadAsync(PluginFolder);

        var call = await plugin.CallAsync("CXP_NAME_greet", new { name = "admin" });

        Assert.Equal("greet admin", call.Gate!.Display);
        Assert.True(call.StoppedAtGate);
    }
}
