using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Whether a plugin's sidecar declares <c>"client": true</c> is what must gate constructing an
/// <see cref="CxAgent.Core.Plugins.IPluginClient"/> for it — a plugin that never asked must never
/// hold a reference it could stash during its own <c>Load()</c>, before
/// <see cref="CxAgent.Core.Plugins.ManagedPluginLoader"/>'s own
/// <see cref="CxAgent.Core.Plugins.IPluginClientConsumer"/> check has even run. Both
/// <c>Session.RunLoadRequest</c> and <c>PluginDiscovery</c> build the client this way — through
/// <c>/plugin load</c>, which is what these tests drive, matching <see cref="PluginCommandTests"/>'s
/// own wiring.
///
/// <para>NEITHER OF THE TWO EXISTING FIXTURES CAN PROVE THE "DECLARES IT" HALF THROUGH A SUCCESSFUL
/// LOAD: <c>cxagent.Tests.PluginFixture</c> (well-formed) never returns Client=true from its own
/// Load, and <c>cxagent.Tests.PluginFixture.ClaimsClient</c> declares it but deliberately does not
/// implement <see cref="CxAgent.Core.Plugins.IPluginClientConsumer"/>, so <c>ManagedPluginLoader</c>
/// refuses it before this test could observe what the context held. This adds
/// <c>cxagent.Tests.PluginFixture.DeclaresClient</c> — the one combination neither existing fixture
/// is built to reach: a sidecar declaring "client": true, a Load() that agrees, AND a type that
/// implements the marker, so the load actually succeeds.</para>
/// </summary>
[Collection("plugin-fixture-sidecar")]
public class PluginClientDeclarationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugin-client-decl-" + Guid.NewGuid().ToString("N"));

    public PluginClientDeclarationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>Drops one fixture's DLL and its OWN sidecar (not the well-formed one — each fixture
    /// here brings its own "client" declaration) under the project-local plugin folder, matching
    /// <see cref="PluginCommandTests.DropFixture"/>'s search-path reasoning.</summary>
    private static void DropFixture(string into, string fixtureAssemblyName, string fileName)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, fixtureAssemblyName + ".dll");
        var sidecar = Path.ChangeExtension(dll, null) + ".plugin.json";

        into = Path.Combine(into, ".cxagent", "plugins");
        Directory.CreateDirectory(into);
        File.Copy(dll, Path.Combine(into, fileName), overwrite: true);
        File.Copy(sidecar,
            Path.Combine(into, Path.GetFileNameWithoutExtension(fileName) + ".plugin.json"), overwrite: true);
    }

    private Session Wired(out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    [Fact]
    public async Task A_plugin_that_declares_the_client_is_handed_one()
    {
        DropFixture(_dir, "cxagent.Tests.PluginFixture.DeclaresClient", "declares-client.dll");
        var session = Wired(out var manager);
        using var _ = manager;

        var status = await session.RunPluginCommand("load declares-client.dll", CancellationToken.None);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.True(session.Plugins.HasClientForTest("declares-client"));
    }

    [Fact]
    public async Task A_plugin_that_does_not_declare_the_client_gets_none()
    {
        DropFixture(_dir, "cxagent.Tests.PluginFixture", "well-formed.dll");
        var session = Wired(out var manager);
        using var _ = manager;

        var status = await session.RunPluginCommand("load well-formed.dll", CancellationToken.None);

        Assert.Equal(CommandStatus.Changed, status);
        Assert.False(session.Plugins.HasClientForTest("well-formed"));
    }
}
