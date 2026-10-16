using CxAgent.Core.Config;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A saved change and the prompt cache: what the model cannot see is swapped in place with no new host;
/// what it can see waits for the choice the user made — now, at the next clear or compaction, or never
/// for a running tab.
/// </summary>
public class SessionInPlaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inplace-" + Guid.NewGuid().ToString("N"));
    public SessionInPlaceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static ResolvedConfig Config(params (string Name, ILlmProvider Provider)[] instances)
    {
        var registry = ProviderRegistry.FromProviders(instances.ToDictionary(i => i.Name, i => i.Provider), instances[0].Name);
        return new ResolvedConfig(registry.Use(instances[0].Name), new ProviderCatalog(Instances: registry), []);
    }

    private (SessionManager Manager, Session Session) Wired(ResolvedConfig config)
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var ports = new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };
        var session = manager.Open(_dir, config, ports, AgentMode.Single);
        manager.RebindOne = (s, cfg) => { manager.Open(s, cfg, ports, AgentMode.Single); return true; };
        return (manager, session);
    }

    private static ConfigChange Changed(string instance) =>
        new(new HashSet<string> { instance }, new Dictionary<string, string>(), false, $"{instance} updated");

    /// <summary>An edit to <paramref name="instance"/>'s model — one a tab's prompt cache does not survive.</summary>
    private static ConfigChange NewModel(string instance) =>
        Changed(instance) with { ModelChanged = new HashSet<string> { instance } };

    // SAME MODEL, NEW KEY OR ENDPOINT: nothing the model sees changes, so nothing is rebuilt.
    [Fact]
    public void ASameModelChange_SwapsTheProviderInPlace_WithoutANewHost()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;
        var host = session.Host;

        var rotated = new LevelledMock(["on"]);
        manager.ApplyConfig(Config(("local", rotated)), Changed("local"), CacheChoice.ApplyNow);

        Assert.Same(host, session.Host);
        Assert.Same(rotated.State, Assert.IsType<LevelledMock>(session.Provider).State);
    }

    // A NEW MODEL BREAKS THE CACHE: with "only new tabs" a running tab keeps its provider and only sees
    // the new catalog.
    [Fact]
    public void ANewModel_WithNewTabsOnly_LeavesTheTab_AndAdoptsTheCatalog()
    {
        var before = new LevelledMock(["on"]);
        var (manager, session) = Wired(Config(("local", before)));
        using var _m = manager;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("another-model")), ("extra", new LevelledMock(["on"]))),
            NewModel("local"), CacheChoice.NewTabsOnly);

        Assert.Same(before.State, Assert.IsType<LevelledMock>(session.Provider).State);
        Assert.Contains("extra", session.Resolution!.Providers!.InstanceNames);
    }

    [Fact]
    public void ANewModel_AtTheNextClear_WaitsForTheClear()
    {
        var before = new LevelledMock(["on"]);
        var (manager, session) = Wired(Config(("local", before)));
        using var _m = manager;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("another-model"))), NewModel("local"),
            CacheChoice.AtNextClearOrCompression);
        Assert.Same(before.State, Assert.IsType<LevelledMock>(session.Provider).State);
        Assert.True(session.ConfigPending);

        session.ClearContext();

        Assert.Equal("another-model", session.Provider!.ModelId);
        Assert.False(session.ConfigPending);
    }

    [Fact]
    public void ANewModel_ApplyNow_RebindsTheTab()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("another-model"))), NewModel("local"), CacheChoice.ApplyNow);

        Assert.Equal("another-model", session.Provider!.ModelId);
    }

    // A WAITING MOVE APPLIES THE FILE AS IT IS WHEN IT RUNS, not as it was when it was scheduled: a save
    // in between would otherwise be undone for this tab — an old key back, a new instance missing.
    [Fact]
    public void AWaitingMove_AppliesTheLatestSave_NotTheOneThatScheduledIt()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("model-a"))), NewModel("local"),
            CacheChoice.AtNextClearOrCompression);
        manager.ApplyConfig(Config(("local", new MockLlmProvider("model-b")), ("extra", new LevelledMock(["on"]))),
            new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), false, "extra added"),
            CacheChoice.AtNextClearOrCompression);

        session.ClearContext();

        Assert.Equal("model-b", session.Provider!.ModelId);
        Assert.Contains("extra", session.Resolution!.Providers!.InstanceNames);
    }

    // A RENAME SAVED WHILE A MOVE WAITS: the move finds the instance under its new name.
    [Fact]
    public void AWaitingMove_FollowsARenameSavedAfterIt()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("model-a"))), NewModel("local"),
            CacheChoice.AtNextClearOrCompression);
        manager.ApplyConfig(Config(("home", new MockLlmProvider("model-a"))),
            new ConfigChange(new HashSet<string> { "local" }, new Dictionary<string, string> { ["local"] = "home" }, false, "renamed"),
            CacheChoice.ApplyNow);

        session.ClearContext();

        Assert.Equal("model-a", session.Provider!.ModelId);
        Assert.Equal("home", session.InstanceName);
        Assert.False(session.ConfigPending);
    }

    // THE DIALOG LISTS ONLY THE TABS WHOSE CACHE WOULD BREAK.
    [Fact]
    public void BreaksCache_IsTheTabsWhoseModelChanges()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;

        Assert.Empty(manager.BreaksCache(Config(("local", new LevelledMock(["on"]))), Changed("local")));
        Assert.Same(session, Assert.Single(manager.BreaksCache(Config(("local", new MockLlmProvider("another-model"))), NewModel("local"))));
    }

    // A RE-WIRE KEEPS THE DAY THE SESSION STARTED: the date is in the system prompt, and a new one would
    // re-send the whole conversation.
    [Fact]
    public void ARewiredAgent_KeepsTheSessionsStartDate()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        session.StartedOn = yesterday;

        manager.ApplyConfig(Config(("local", new MockLlmProvider("another-model"))), NewModel("local"), CacheChoice.ApplyNow);

        Assert.Equal(yesterday, session.Host!.AgentStartedOn);
    }

    // THE REVIEWER IS THE SESSION'S POLICY, set in place: no new host, so no cache spent on a change the
    // main model never sees.
    [Fact]
    public void ANewReviewer_IsSetOnThePolicy_WithoutANewHost()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        using var _m = manager;
        var policy = new CxAgent.Core.Permissions.PermissionPolicy(_dir, new CxAgent.Core.Permissions.PermissionRulesStore(new AppPaths(_dir)));
        var ports = new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy };
        var session = manager.Open(_dir, Config(("local", new LevelledMock(["on"])), ("judge", new LevelledMock(["on"]))), ports, AgentMode.Single);
        var host = session.Host;
        Assert.Null(policy.Classifier);

        var withReviewer = Config(("local", new LevelledMock(["on"])), ("judge", new LevelledMock(["on"])));
        withReviewer = withReviewer with { Catalog = withReviewer.Catalog with { ClassifierInstance = "judge" } };
        manager.ApplyConfig(withReviewer, new ConfigChange(new HashSet<string>(), new Dictionary<string, string>(), true, "reviewer set"),
            CacheChoice.ApplyNow);

        Assert.NotNull(policy.Classifier);
        Assert.Same(host, session.Host);
    }

    // A TAB WITH A CONVERSATION HAS A CACHE TO LOSE, whether or not its provider reports token usage —
    // the question about it must not be skipped because a server stays silent about counts.
    [Fact]
    public async Task ContextUsed_IsEstimated_WhenTheProviderReportsNoUsage()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;
        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("hello there")).Turn;

        Assert.True(session.ContextUsed > 0);
    }
}
