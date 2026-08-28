using Xunit;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;

namespace CxAgent.Tests;

/// <summary>
/// THE MANAGER OWNS THE ENTRIES ITS MUTATORS CHANGE, so a host that does not give it the resolution
/// gets four methods that always refuse.
///
/// <para>THIS WAS SHIPPED AND INVISIBLE. Every mutator test seeds the manager itself, so all of them
/// passed while the real application built one with no config — and `/plugin disable x` answered
/// "'x' is not a configured plugin" for a plugin that had just loaded from that very entry. A live
/// session found it; nothing in the suite could, because no test asserted what a manager built the
/// way the app builds one actually holds.</para>
/// </summary>
public class ManagerConfigTests
{
    [Fact]
    public void AManagerWithNoResolutionHoldsNoPluginEntries()
    {
        var manager = SessionManager.Over(new SharedServices { GlobalInstructionsDir = "/tmp" });

        Assert.Empty(manager.Config.Plugins);
    }

    /// <summary>Given the resolution, it holds what config declared — which is what makes
    /// SetPluginEnabled and its siblings reachable at all.</summary>
    [Fact]
    public void AManagerGivenTheResolutionHoldsItsPluginEntries()
    {
        var entries = new PluginEntries(new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll"),
        });
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()) with { Entries = entries };

        var manager = SessionManager.Over(
            new SharedServices { GlobalInstructionsDir = "/tmp" }, null, resolution);

        Assert.True(manager.Config.Plugins.ContainsKey("csharp-lsp"));
    }
}
