using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A folder added in the plugin manager's Sources view, searched from then on without a restart.</summary>
public class SessionManagerPluginPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plugin-paths-" + Guid.NewGuid().ToString("N"));
    public SessionManagerPluginPathsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    /// <summary>
    /// THE MANAGER AND EVERY OPEN TAB HAVE THEM AT ONCE. The plugin manager searches the manager's folders, and
    /// /plugin load and a new tab search the session's — so a folder only one of them knew about would list a
    /// plugin that then could not be loaded.
    /// </summary>
    [Fact]
    public void NewFolders_ReachTheManager_AndEveryOpenTab()
    {
        var registry = ProviderRegistry.FromProviders(new Dictionary<string, ILlmProvider> { ["local"] = new MockLlmProvider() }, "local");
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, new ResolvedConfig(registry.Use("local"), new ProviderCatalog(Instances: registry), []),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.FanOut);

        var host = session.Host;
        var entries = manager.Config.Entries;

        manager.SetPluginPaths(["~/src/plugins", "tools"]);

        Assert.Equal(["~/src/plugins", "tools"], manager.Config.PluginPaths);
        Assert.Equal(["~/src/plugins", "tools"], session.Resolution!.PluginPaths);
        // NOTHING ELSE MOVES: the loaded plugins stay as they are, and no tab is re-wired for a search path.
        Assert.Same(entries, manager.Config.Entries);
        Assert.Same(host, session.Host);
    }
}
