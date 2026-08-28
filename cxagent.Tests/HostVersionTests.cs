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
    /// </summary>
    [Fact]
    public void TheHostAssemblyReportsSomethingOtherThanTheFrozenIdentity()
    {
        var host = PluginContract.HostVersionOf(typeof(PluginResolver).Assembly);

        Assert.NotEqual("1.0.0", host);
    }
}
