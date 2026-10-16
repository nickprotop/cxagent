using CxAgent.Core.Config;
using CxAgent.Core.Jobs;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// A saved config change reaching a running session: re-wired onto the new config with its
/// conversation and thinking level kept — at once when idle, after the turn when busy.
/// </summary>
public class SessionRebindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rebind-" + Guid.NewGuid().ToString("N"));
    public SessionRebindTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private SessionPorts Ports() => new() { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };

    /// <summary>A config whose registry holds the given instances, the first being default and the session's.</summary>
    private static ResolvedConfig Config(params (string Name, ILlmProvider Provider)[] instances)
    {
        var registry = ProviderRegistry.FromProviders(
            instances.ToDictionary(i => i.Name, i => i.Provider), instances[0].Name);
        return new ResolvedConfig(registry.Use(instances[0].Name), new ProviderCatalog(Instances: registry), []);
    }

    private (SessionManager Manager, Session Session) Wired(ResolvedConfig config)
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, config, Ports(), AgentMode.Single);
        manager.RebindOne = (s, cfg) => { manager.Open(s, cfg, Ports(), AgentMode.Single); return true; };
        return (manager, session);
    }

    // BY THE MOCK'S SHARED STATE, NOT ITS IDENTITY: a rebind re-applies the session's thinking level,
    // which hands back a copy of the provider at that level — the same model, a different object.

    private static async Task Turn(Session session, string text) =>
        await Assert.IsType<Session.SubmitOutcome.Started>(session.Submit(text)).Turn;

    private static ConfigChange Changed(string instance) =>
        new(new HashSet<string> { instance }, new Dictionary<string, string>(), false, $"{instance} updated");

    [Fact]
    public async Task AnIdleSession_IsRebound_KeepingItsConversation()
    {
        var before = new LevelledMock(["off", "on"]);
        var (manager, session) = Wired(Config(("local", before)));
        using var _m = manager;
        await Turn(session, "remember the word heron");
        var count = session.Host!.Context.Snapshot().Count;

        var after = new LevelledMock(["off", "on"]);
        manager.ApplyConfig(Config(("local", after)), Changed("local"));

        Assert.Same(after.State, Assert.IsType<LevelledMock>(session.Provider).State);
        Assert.Equal(count, session.Host!.Context.Snapshot().Count);
    }

    [Fact]
    public async Task ARebindWhileBusy_WaitsForTheTurnToEnd_EvenAFailedOne()
    {
        var before = new LevelledMock(["off", "on"]);
        var (manager, session) = Wired(Config(("local", before)));
        using var _m = manager;

        before.State.Hold = new TaskCompletionSource();
        before.State.Throw = true;
        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("first"));
        while (before.State.Seen.IsEmpty) await Task.Delay(5);

        var after = new LevelledMock(["off", "on"]);
        manager.ApplyConfig(Config(("local", after)), Changed("local"));
        Assert.Same(before.State, Assert.IsType<LevelledMock>(session.Provider).State);

        before.State.Hold.SetResult();
        await started.Turn;
        Assert.Same(after.State, Assert.IsType<LevelledMock>(session.Provider).State);
    }

    [Fact]
    public async Task TheSessionsThinkingLevel_SurvivesTheRebind()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["off", "on"]))));
        using var _m = manager;
        session.SetThinking("off");
        await Turn(session, "hi");

        manager.ApplyConfig(Config(("local", new LevelledMock(["off", "on"], level: "on"))), Changed("local"));

        Assert.Equal("off", session.ThinkingLevel);
    }

    [Fact]
    public void AnUnaffectedSession_AdoptsTheNewCatalog_WithoutARewire()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;
        var host = session.Host;

        var added = Config(("local", session.Provider!), ("extra", new LevelledMock(["on"])));
        manager.ApplyConfig(added, Changed("extra"));

        Assert.Same(host, session.Host);
        Assert.Contains("extra", session.Resolution!.Providers!.InstanceNames);
    }

    [Fact]
    public void AfterARenameRebind_TheSessionFollowsTheNewName()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["on"]))));
        using var _m = manager;

        var renamed = Config(("here", new LevelledMock(["on"])));
        manager.ApplyConfig(renamed, new ConfigChange(new HashSet<string> { "local" },
            new Dictionary<string, string> { ["local"] = "here" }, false, "local is now here"));

        Assert.Equal("here", session.InstanceName);
    }

    [Fact]
    public void AfterARenameRebind_ThinkSave_WritesTheNewName()
    {
        var (manager, session) = Wired(Config(("local", new LevelledMock(["off", "on"]))));
        using var _m = manager;
        string? savedFor = null;
        session.SaveThinkingDefault = _ => { savedFor = session.InstanceName; return true; };

        manager.ApplyConfig(Config(("here", new LevelledMock(["off", "on"]))), new ConfigChange(
            new HashSet<string> { "local" }, new Dictionary<string, string> { ["local"] = "here" }, false, "renamed"));
        session.SetThinking("off", save: true);

        Assert.Equal("here", savedFor);
    }

    [Fact]
    public void ConfigChange_Affects_ByNameOrEveryone()
    {
        var change = Changed("local");
        Assert.True(change.Affects("local"));
        Assert.False(change.Affects("other"));
        Assert.True((change with { Everyone = true }).Affects("other"));
    }
}
