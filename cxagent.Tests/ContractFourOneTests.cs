using CxAgent.Core.Jobs;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Contract 4.1 — additions an author writes against: the manifest read from the sidecar the user
/// approved, so there is one copy, and the sidecar saying which kind of plugin it describes.
/// </summary>
public class ContractFourOneTests
{
    [Theory]
    [InlineData("\"managed\"", PluginKind.Managed)]
    [InlineData("\"native\"", PluginKind.Native)]
    public void TheSidecarsKind_IsRead(string json, PluginKind expected)
    {
        var parsed = PluginManifest.Parse(
            $$"""{"pluginContract":4,"name":"p","version":"1.0.0","spawns":false,"tools":[],"kind":{{json}}}""");

        Assert.True(parsed.IsSuccess);
        Assert.Equal(expected, parsed.Manifest!.Kind);
    }

    /// <summary>NO KIND IS NULL, so the file's extension decides — every sidecar written before 4.1.</summary>
    [Fact]
    public void NoKind_IsNull_SoTheExtensionDecides()
    {
        var parsed = PluginManifest.Parse("""{"pluginContract":2,"name":"p","version":"1.0.0","spawns":false,"tools":[]}""");

        Assert.Null(parsed.Manifest!.Kind);
    }

    [Fact]
    public void AnUnknownKind_IsAParseError()
    {
        var parsed = PluginManifest.Parse(
            """{"pluginContract":4,"name":"p","version":"1.0.0","spawns":false,"tools":[],"kind":"python"}""");

        Assert.False(parsed.IsSuccess);
        Assert.Contains(parsed.Errors, e => e.Contains("kind") && e.Contains("python"));
    }

    /// <summary>FromSidecar returns the file beside the plugin's assembly — what the user approved.</summary>
    [Fact]
    public void FromSidecar_ReturnsTheSidecarBesideTheAssembly()
    {
        var plugin = FixtureInstance("cxagent.Tests.PluginFixture");
        var onDisk = PluginManifest.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "cxagent.Tests.PluginFixture.plugin.json"))).Manifest!;

        var manifest = PluginManifest.FromSidecar(plugin);

        Assert.Equal(onDisk.Name, manifest.Name);
        Assert.Equal(onDisk.Tools.Select(t => t.Name), manifest.Tools.Select(t => t.Name));
        Assert.Equal(onDisk.Contract, manifest.Contract);
    }

    [Fact]
    public void FromSidecar_WithNoSidecar_SaysWhichFile()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PluginManifest.FromSidecar(new NoSidecarPlugin()));

        Assert.Contains("cxagent.Tests.plugin.json", ex.Message);
    }

    private sealed class NoSidecarPlugin : IPlugin
    {
        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) => throw new NotSupportedException();
        public Task Start(CancellationToken ct) => Task.CompletedTask;
        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task Stop(CancellationToken ct) => Task.CompletedTask;
    }

    private static IPlugin FixtureInstance(string assembly)
    {
        var loaded = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, assembly + ".dll"));
        var type = loaded.GetTypes().Single(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
        return (IPlugin)Activator.CreateInstance(type)!;
    }

    /// <summary>"KIND" ARRIVED IN CONTRACT 4, so a sidecar naming 3 cannot honestly use it — and a host
    /// from before 4 would read a native .dll as .NET. Refused before anything runs.</summary>
    [Fact]
    public void AKind_BelowContractFour_IsRefused()
    {
        var sidecar = PluginManifest.Parse(
            """{"pluginContract":3,"name":"p","version":"1.0.0","spawns":false,"tools":[],"kind":"native"}""").Manifest!;

        Assert.Contains("contract 4", PluginContractUse.Refusal(sidecar));
    }

    [Fact]
    public void AKind_AtContractFour_IsAllowed()
    {
        var sidecar = PluginManifest.Parse(
            """{"pluginContract":4,"name":"p","version":"1.0.0","spawns":false,"tools":[],"kind":"native"}""").Manifest!;

        Assert.Null(PluginContractUse.Refusal(sidecar));
    }
}
