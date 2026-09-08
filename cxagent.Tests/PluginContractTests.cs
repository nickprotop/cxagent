using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The single number a plugin author and this host both read off <see cref="PluginContract"/>.</summary>
public class PluginContractTests
{
    [Fact]
    public void The_contract_is_three_and_the_floor_is_still_two()
    {
        Assert.Equal(3, PluginContract.Version);

        // THE FLOOR DOES NOT RISE WITH THE CEILING. Contract 1 cannot express gated:"dynamic", so its
        // tools parse as Never and skip the permission gate — that is why 2 is the floor, and adding a
        // capability above it does not change the reason.
        Assert.Equal(2, PluginContract.Oldest);
    }
}
