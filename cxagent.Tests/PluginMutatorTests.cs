using System.Text.Json;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The four per-entry plugin mutators: one owner of the entries, every session rebound from it,
/// and a session mid-turn deferred rather than skipped.
/// </summary>
public class PluginMutatorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "plugmut-" + Guid.NewGuid().ToString("N"));

    private SessionManager? _manager;

    public PluginMutatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _manager?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// EVERY SESSION SEES IT, NOT JUST THE CALLER. With tabs, two sessions is the ordinary case —
    /// and a mutator that rebound only the caller would leave the other tab's /plugin reporting the
    /// old answer. That is the SwapProvider shape: two of three places updated.
    /// </summary>
    [Fact]
    public void DisablingFromOneSessionIsVisibleFromAnother()
    {
        var (manager, a, b) = TwoSessions("csharp-lsp");

        var result = manager.SetPluginEnabled(a, "csharp-lsp", enabled: false);

        Assert.IsType<PluginChangeResult.Applied>(result);
        Assert.False(a.Resolution!.Plugins["csharp-lsp"].Enabled);
        Assert.False(b.Resolution!.Plugins["csharp-lsp"].Enabled);
    }

    /// <summary>
    /// AND A SESSION OPENED AFTERWARDS. The manager hands `config ?? Config` to every new session,
    /// so a mutator that changed only live sessions would be undone by opening a tab.
    /// </summary>
    [Fact]
    public void ASessionOpenedAfterwardsSeesTheChange()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");
        manager.SetPluginEnabled(a, "csharp-lsp", enabled: false);

        var late = OpenAnother(manager);

        Assert.False(late.Resolution!.Plugins["csharp-lsp"].Enabled);
    }

    /// <summary>
    /// ADD IS THE THIRD DOOR AND GETS THE SAME CHECK. config.json's reader and AgentConfig.Resolve
    /// both refuse an entry with no file; a mutator that did not would be the one unvalidated way in.
    /// </summary>
    [Fact]
    public void AnEntryWithNoFileIsRefused()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");

        var result = manager.AddPlugin(a, "broken", new PluginConfig(""));

        var refused = Assert.IsType<PluginChangeResult.Refused>(result);
        Assert.Contains("file", refused.Reason);
        Assert.False(a.Resolution!.Plugins.ContainsKey("broken"));
    }

    /// <summary>Removing a name leaves a second entry naming the same binary alone.</summary>
    [Fact]
    public void RemovingOneOfTwoNamesSharingABinaryKeepsTheOther()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp", "csharp-lsp-omnisharp");

        manager.RemovePlugin(a, "csharp-lsp");

        Assert.False(a.Resolution!.Plugins.ContainsKey("csharp-lsp"));
        Assert.True(a.Resolution!.Plugins.ContainsKey("csharp-lsp-omnisharp"));
    }

    /// <summary>Settings replace wholesale — the plugin is handed the block verbatim.</summary>
    [Fact]
    public void SettingsAreReplacedOnTheNamedEntry()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");
        var settings = JsonDocument.Parse("""{"server":"csharp-ls"}""").RootElement;

        manager.SetPluginSettings(a, "csharp-lsp", settings);

        Assert.Equal("csharp-ls",
            a.Resolution!.Plugins["csharp-lsp"].Settings!.Value.GetProperty("server").GetString());
    }

    /// <summary>
    /// SETTINGS OUTLIVE THE DOCUMENT THEY CAME FROM. A JsonElement is a window onto its
    /// JsonDocument's buffer; store the caller's element and a disposed document leaves this entry
    /// throwing on the next read. The dialog will parse a block, hand it over and dispose — so this
    /// is the ordinary path, not an exotic one.
    /// </summary>
    [Fact]
    public void SettingsSurviveTheirSourceDocumentBeingDisposed()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");

        using (var doc = JsonDocument.Parse("""{"server":"csharp-ls"}"""))
            manager.SetPluginSettings(a, "csharp-lsp", doc.RootElement);
        // doc is disposed here — an unstored clone would now be reading freed memory

        Assert.Equal("csharp-ls",
            a.Resolution!.Plugins["csharp-lsp"].Settings!.Value.GetProperty("server").GetString());
    }

    /// <summary>
    /// A SKIPPED SESSION IS DEFERRED, NOT DISCARDED. A session mid-turn keeps the tool list its
    /// request was built with, but it must not be left on those entries forever — it takes them when
    /// the turn ends. Without this, one tab running a long turn during a change answers /plugin with
    /// a stale state for the rest of the session.
    ///
    /// <para>Drives the deferral directly rather than racing a real turn: DeferPlugins is what the
    /// manager calls for a busy session, and the turn's end is what takes it.</para>
    /// </summary>
    [Fact]
    public void ASessionThatWasBusyTakesTheChangeWhenItsTurnEnds()
    {
        var (manager, a, b) = TwoSessions("csharp-lsp");
        manager.SetPluginEnabled(a, "csharp-lsp", enabled: false);

        // b took it live; prove the deferred path lands the same value.
        b.RebindPlugins(new PluginEntries(new Dictionary<string, PluginConfig>
        {
            ["csharp-lsp"] = new("csharp-lsp.dll"),          // stale: still enabled
        }));
        Assert.True(b.Resolution!.Plugins["csharp-lsp"].Enabled);

        b.DeferPlugins(manager.Config.PluginSet);
        b.CatchUpOnPlugins();

        Assert.False(b.Resolution!.Plugins["csharp-lsp"].Enabled);
    }

    /// <summary>
    /// THE CALLER'S OWN TURN REFUSES. A mutation mid-turn would change what the model is reading
    /// while it reasons — the same rule LoadPlugin and UnwirePluginAsync already enforce.
    /// </summary>
    [Fact]
    public void AMutationIsRefusedWhileTheCallersTurnRuns()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");
        using var busy = a.PretendBusyForTesting();

        var result = manager.SetPluginEnabled(a, "csharp-lsp", enabled: false);

        Assert.IsType<PluginChangeResult.Refused>(result);
        Assert.True(a.Resolution!.Plugins["csharp-lsp"].Enabled);   // unchanged
    }

    /// <summary>
    /// A BUSY BYSTANDER IS SKIPPED, AND THE CHANGE STILL LANDS. One tab mid-turn must not block
    /// another tab's config change — that is the freeze this design refuses — so the mutation
    /// applies, the busy session keeps its view, and its deferral is what reconciles it later.
    /// </summary>
    [Fact]
    public void ABusyBystanderDoesNotBlockTheChange()
    {
        var (manager, a, b) = TwoSessions("csharp-lsp");
        using var busy = b.PretendBusyForTesting();

        var result = manager.SetPluginEnabled(a, "csharp-lsp", enabled: false);

        Assert.IsType<PluginChangeResult.Applied>(result);
        Assert.False(a.Resolution!.Plugins["csharp-lsp"].Enabled);       // caller sees it
        Assert.True(b.Resolution!.Plugins["csharp-lsp"].Enabled);        // busy one still holds its view
    }

    /// <summary>A name that was never configured cannot be enabled — there is nothing to enable.</summary>
    [Fact]
    public void EnablingAnUnknownNameIsRefused()
    {
        var (manager, a, _) = TwoSessions("csharp-lsp");

        var result = manager.SetPluginEnabled(a, "never-configured", enabled: true);

        Assert.IsType<PluginChangeResult.Refused>(result);
    }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };

    /// <summary>
    /// A manager SEEDED with the named plugins, and two sessions opened on its own config.
    ///
    /// <para>THE NO-CONFIG OVERLOAD, DELIBERATELY. A session handed an explicit resolution — the
    /// shape SessionManagerTests uses — never consults <see cref="SessionManager.Config"/>, so it
    /// would sit outside the very path these tests exercise: <c>config ?? Config</c> resolving to
    /// the manager's own is what makes a late-opened session see an earlier mutation.</para>
    /// </summary>
    private (SessionManager Manager, Session A, Session B) TwoSessions(params string[] names)
    {
        var entries = names.ToDictionary(n => n, _ => new PluginConfig("csharp-lsp.dll"),
            StringComparer.Ordinal);
        var resolution = ResolvedConfig.ForTesting(new MockLlmProvider()).WithPlugins(entries);

        _manager = SessionManager.Create(new AppPaths(_dir), config: resolution);
        return (_manager, OpenAnother(_manager), OpenAnother(_manager));
    }

    private Session OpenAnother(SessionManager manager) => manager.Open(_dir, Ports());
}
