using System.Text.Json;
using CxAgent.Core.Commands;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Plugins;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A STORE'S PLUGIN IS REGISTERED UNDER <c>store/name</c>, whichever way it loads. Its manifest says
/// <c>calculator</c>, but the user reaches it only as <c>acme/calculator</c> — in config, in F2, in
/// <c>/plugin load</c> — so a session that registered the manifest name would list a plugin the user
/// never named, call the one they did name "not active", and let <c>/plugin unwire calculator</c>
/// stop the store's copy instead of the official one.
///
/// <para>THE REAL FIXTURE FOR EVERY DOOR THAT READS DISK — <c>/plugin load</c> by name, by configured
/// key and by path, and startup — because each builds its own client and context before the session
/// sees the plugin, and a door that named those differently would still pass a fake-only test.</para>
/// </summary>
[Collection("plugin-fixture-sidecar")]
public sealed class StorePluginRegistrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "store-register-" + Guid.NewGuid().ToString("N"));

    public StorePluginRegistrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static readonly string FixtureDll =
        Path.Combine(AppContext.BaseDirectory, "cxagent.Tests.PluginFixture.dll");

    // THE PROJECT'S PLUGINS FOLDER, one of PluginResolver.SearchFolders' defaults, so no pluginPaths
    // entry is needed for `/plugin load` to find what a test drops there.
    private string Plugins => Path.Combine(_dir, ".cxagent", "plugins");

    /// <summary>The well-formed fixture (manifest name <c>well-formed</c>) in
    /// <c>plugins/<paramref name="relative"/></c>; its folder is returned.</summary>
    private string Drop(string relative)
    {
        var home = Path.Combine(Plugins, relative);
        Directory.CreateDirectory(home);
        File.Copy(FixtureDll, Path.Combine(home, "well-formed.dll"), overwrite: true);
        File.Copy(Path.ChangeExtension(FixtureDll, null) + ".plugin.json",
            Path.Combine(home, "well-formed.plugin.json"), overwrite: true);
        return home;
    }

    private void MarkStore(string store)
    {
        Directory.CreateDirectory(Path.Combine(Plugins, store));
        File.WriteAllText(Path.Combine(Plugins, store, PluginStores.Marker), "");
    }

    private Session Wired(out SessionManager manager, out BufferedChatSink sink,
        IReadOnlyDictionary<string, PluginConfig>? plugins = null)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        sink = new BufferedChatSink();
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider())
            .WithPlugins(plugins ?? new Dictionary<string, PluginConfig>());
        return manager.Open(_dir, resolution,
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
    }

    // ---- The one decision: PluginStores.RegisteredName ------------------------------------------

    [Fact]
    public void RegisteredName_IsStoreSlashFolder_UnderAMarkedFolder()
    {
        MarkStore("acme");
        var home = Drop("acme/calculator");
        Assert.Equal("acme/calculator", PluginStores.RegisteredName("calculator", home));
        // WITH A TRAILING SEPARATOR, as a hand-typed path can carry, the folder names are the same.
        Assert.Equal("acme/calculator", PluginStores.RegisteredName("calculator", home + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RegisteredName_IsTheFolderName_NotTheManifestName_InAStore()
    {
        // THE FOLDER IS WHAT THE USER TYPES (FindUnconfigured reports `store/<folder>`), so that is the
        // name, even where a store's folder and its plugin's manifest disagree.
        MarkStore("acme");
        var home = Drop("acme/calc");
        Assert.Equal("acme/calc", PluginStores.RegisteredName("calculator", home));
    }

    /// <summary>
    /// A MARKED FOLDER WHOSE NAMES ARE NOT store/name — made by hand, as nothing cxagent installs is —
    /// registers the manifest name: <c>Acme/calc.v2</c> is a spelling no command accepts, so a plugin
    /// registered under it could be listed but never named again to unwire or reload.
    /// </summary>
    [Theory]
    [InlineData("Acme", "calculator")]
    [InlineData("acme", "calc.v2")]
    public void RegisteredName_IsTheManifestName_WhenTheFoldersAreNotAStoreName(string store, string folder)
    {
        MarkStore(store);
        var home = Drop($"{store}/{folder}");
        Assert.Equal("calculator", PluginStores.RegisteredName("calculator", home));
    }

    [Fact]
    public void RegisteredName_IsTheManifestName_OutsideAStore()
    {
        Assert.Equal("calculator", PluginStores.RegisteredName("calculator", Drop("calculator")));
        Assert.Equal("calculator", PluginStores.RegisteredName("calculator", Plugins));
        // AN UNMARKED NESTED FOLDER IS NOT A STORE: a folder of plugins someone made by hand.
        Assert.Equal("calculator", PluginStores.RegisteredName("calculator", Drop("vendor/calculator")));
    }

    [Fact]
    public void IsStoreFolder_IsTheMarker()
    {
        MarkStore("acme");
        Assert.True(PluginStores.IsStoreFolder(Path.Combine(Plugins, "acme")));
        Drop("vendor/x");
        Assert.False(PluginStores.IsStoreFolder(Path.Combine(Plugins, "vendor")));
        Assert.False(PluginStores.IsStoreFolder(null));
    }

    // ---- Every door that reads disk -------------------------------------------------------------

    [Fact]
    public async Task PluginLoad_ByQualifiedName_Unconfigured_RegistersTheQualifiedName()
    {
        MarkStore("acme"); Drop("acme/well-formed");
        var session = Wired(out var manager, out var sink);
        using var owned = manager;

        Assert.Equal(CommandStatus.Changed, await session.RunPluginCommand("load acme/well-formed", CancellationToken.None));

        Assert.Equal(["acme/well-formed"], session.Plugins.LoadedPluginNames);
        Assert.Contains(sink.Notices, n => n.Contains("plugin 'acme/well-formed' loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PluginLoad_ByConfiguredKey_RegistersTheQualifiedName()
    {
        MarkStore("acme"); Drop("acme/well-formed");
        var session = Wired(out var manager, out _, new Dictionary<string, PluginConfig>
        {
            ["acme/well-formed"] = new("well-formed.dll"),
        });
        using var owned = manager;

        Assert.Equal(CommandStatus.Changed, await session.RunPluginCommand("load acme/well-formed", CancellationToken.None));
        Assert.Equal(["acme/well-formed"], session.Plugins.LoadedPluginNames);
    }

    [Fact]
    public async Task PluginLoad_ByAPathIntoTheStoreFolder_RegistersTheQualifiedName()
    {
        // THE STORE'S COPY, HOWEVER IT WAS REACHED: the load set decides, not the spelling typed.
        MarkStore("acme");
        var home = Drop("acme/well-formed");
        var session = Wired(out var manager, out _);
        using var owned = manager;

        Assert.Equal(CommandStatus.Changed,
            await session.RunPluginCommand($"load {Path.Combine(home, "well-formed.dll")}", CancellationToken.None));
        Assert.Equal(["acme/well-formed"], session.Plugins.LoadedPluginNames);
    }

    [Fact]
    public async Task Startup_FromAQualifiedConfigKey_RegistersTheQualifiedName()
    {
        MarkStore("acme"); Drop("acme/well-formed");
        var session = Wired(out var manager, out _);
        using var owned = manager;

        var notLoaded = await PluginDiscovery.LoadConfiguredAsync(session,
            new Dictionary<string, PluginConfig> { ["acme/well-formed"] = new("well-formed.dll") },
            [Plugins], _dir, _ => { }, CancellationToken.None);

        Assert.Empty(notLoaded);
        Assert.Equal(["acme/well-formed"], session.Plugins.LoadedPluginNames);
    }

    [Fact]
    public async Task AnOfficialPlugin_AndAnUnmarkedNestedOne_KeepTheirManifestName()
    {
        Drop("well-formed");
        var session = Wired(out var manager, out _);
        using var owned = manager;
        Assert.Equal(CommandStatus.Changed, await session.RunPluginCommand("load well-formed.dll", CancellationToken.None));
        Assert.Equal(["well-formed"], session.Plugins.LoadedPluginNames);

        var nested = Drop("vendor/other");
        var second = Wired(out var secondManager, out _);
        using var ownedSecond = secondManager;
        Assert.Equal(CommandStatus.Changed,
            await second.RunPluginCommand($"load {Path.Combine(nested, "well-formed.dll")}", CancellationToken.None));
        Assert.Equal(["well-formed"], second.Plugins.LoadedPluginNames);
    }

    // ---- The session, keyed by the registered name ----------------------------------------------

    private static PluginManifest Manifest(string name, params string[] tools) =>
        new(name, "1.0.0", Instructions: null, Spawns: false,
            tools.Select(t => new PluginToolManifest(t, "does something",
                JsonSerializer.SerializeToElement(new { type = "object" }))).ToList());

    private sealed class FakePlugin : IPlugin
    {
        public bool Stopped { get; private set; }
        public Task<PluginManifest> Load(IPluginContext context, CancellationToken ct) => throw new NotSupportedException();
        public Task Start(CancellationToken ct) => Task.CompletedTask;
        public Task<JobResult> Invoke(string toolName, JobParameters call, IJobContext context, CancellationToken ct) =>
            Task.FromResult(new JobResult { Success = true });
        public Task Stop(CancellationToken ct) { Stopped = true; return Task.CompletedTask; }
    }

    [Fact]
    public async Task TheOfficialPlugin_AndAStoresSameNamedOne_LoadTogether_WhenTheirToolsDiffer()
    {
        MarkStore("acme");
        var storeHome = Path.Combine(Plugins, "acme", "calculator");
        var officialHome = Path.Combine(Plugins, "calculator");
        Directory.CreateDirectory(storeHome);
        Directory.CreateDirectory(officialHome);
        var session = Wired(out var manager, out _);
        using var owned = manager;

        Assert.Equal(CommandStatus.Changed, await session.LoadPlugin(new FakePlugin(), Manifest("calculator", "add"), officialHome));
        Assert.Equal(CommandStatus.Changed, await session.LoadPlugin(new FakePlugin(), Manifest("calculator", "acme_add"), storeHome));

        Assert.Equal(["acme/calculator", "calculator"], session.Plugins.LoadedPluginNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AToolCollision_StillRefusesTheStoresCopy()
    {
        MarkStore("acme");
        var storeHome = Path.Combine(Plugins, "acme", "calculator");
        var officialHome = Path.Combine(Plugins, "calculator");
        Directory.CreateDirectory(storeHome);
        Directory.CreateDirectory(officialHome);
        var session = Wired(out var manager, out var sink);
        using var owned = manager;

        await session.LoadPlugin(new FakePlugin(), Manifest("calculator", "add"), officialHome);
        Assert.Equal(CommandStatus.Reported, await session.LoadPlugin(new FakePlugin(), Manifest("calculator", "add"), storeHome));

        Assert.Equal(["calculator"], session.Plugins.LoadedPluginNames);
        Assert.Contains(sink.Notices, n => n.Contains("plugin 'acme/calculator' was not loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlreadyLoaded_RefersToTheQualifiedName()
    {
        MarkStore("acme");
        var storeHome = Path.Combine(Plugins, "acme", "calculator");
        Directory.CreateDirectory(storeHome);
        var session = Wired(out var manager, out var sink);
        using var owned = manager;

        await session.LoadPlugin(new FakePlugin(), Manifest("calculator", "add"), storeHome);
        var again = new FakePlugin();
        Assert.Equal(CommandStatus.Reported, await session.LoadPlugin(again, Manifest("calculator", "other"), storeHome));

        Assert.True(again.Stopped);
        Assert.Contains(sink.Notices, n => n.Contains("plugin 'acme/calculator' was not loaded — a plugin with that name is already loaded",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unwire_TakesTheQualifiedName_AndTheBareNameNeverReachesTheStoresCopy()
    {
        MarkStore("acme");
        var storeHome = Path.Combine(Plugins, "acme", "calculator");
        Directory.CreateDirectory(storeHome);
        var session = Wired(out var manager, out _);
        using var owned = manager;
        var plugin = new FakePlugin();
        await session.LoadPlugin(plugin, Manifest("calculator", "add"), storeHome);

        Assert.Equal(CommandStatus.Reported, await session.RunPluginCommand("unwire calculator", CancellationToken.None));
        Assert.False(plugin.Stopped);
        Assert.Equal(["acme/calculator"], session.Plugins.LoadedPluginNames);

        Assert.Equal(CommandStatus.Changed, await session.RunPluginCommand("unwire acme/calculator", CancellationToken.None));
        Assert.True(plugin.Stopped);
        Assert.Empty(session.Plugins.LoadedPluginNames);
    }
}
