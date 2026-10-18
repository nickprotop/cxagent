using Xunit;
using CxAgent.Core.Plugins;

namespace CxAgent.Tests;

/// <summary>
/// WHICH ASSEMBLY ANSWERS "what version is this host". Two different numbers live in this codebase
/// on purpose: the contract assembly's AssemblyVersion is FROZEN so a managed plugin's binding
/// survives a release, and the host's moves with the git tag. Read the frozen one and every build
/// claims the same version forever — true of nothing, and undetectable without a test, because the
/// number looks perfectly plausible.
/// </summary>
public class HostVersionTests
{
    /// <summary>
    /// The frozen identity is what the loader matches on and what a plugin binds to. It is
    /// deliberately not a release, so nothing may report it as one.
    /// </summary>
    [Fact]
    public void TheContractAssemblyReportsItsFrozenIdentity()
        => Assert.Equal("1.0.0", PluginContract.HostVersionOf(typeof(PluginContract).Assembly));

    /// <summary>
    /// The host's own assembly carries the release the build stamped, which is the number a plugin
    /// is told and a log should show.
    ///
    /// <para>COMPARED WITH THE STAMP ITSELF, read another way — the informational version the same
    /// build writes — not with "anything but 1.0.0". That held only because tests run unstamped, at
    /// 0.0.0, and would have failed the release that is 1.0.0 while proving nothing before it.</para>
    /// </summary>
    [Fact]
    public void TheHostAssemblyReportsTheReleaseItWasStampedWith()
    {
        var core = typeof(PluginResolver).Assembly;
        var stamped = core.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

        Assert.Equal(stamped.Split('+', '-')[0], PluginContract.HostVersionOf(core));
    }
}
