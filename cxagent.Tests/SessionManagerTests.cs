using CxAgent.Core.Agent;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The manager exists to make a SECOND session ordinary. These pin that: two sessions, two folders,
/// one set of shared services, and neither disturbing the other.
/// </summary>
public class SessionManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "sessmgr-" + Guid.NewGuid().ToString("N"));

    public SessionManagerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() };

    [Fact]
    public void Create_BuildsTheSharedHalfOnce()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.NotNull(manager.Shared.Logs);
        Assert.NotNull(manager.Shared.Resume);
        Assert.NotNull(manager.Shared.History);
        Assert.Equal(_dir, manager.Shared.GlobalInstructionsDir);
    }

    // THE GATE IS THE CALLER'S. Core builds logs and stores; asking a human needs a window, so a
    // headless manager has no gate and its sessions are ungated — an ordinary arrangement, not a
    // degraded one.
    [Fact]
    public void Create_WithoutAGate_IsAValidHeadlessManager()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        Assert.Null(manager.Shared.Gate);
    }

    [Fact]
    public void TwoSessions_ShareOneSetOfServicesAndKeepTheirOwnFolders()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        using var manager = SessionManager.Create(new AppPaths(_dir));
        var provider = new MockLlmProvider();

        var one = manager.Open(a, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);
        var two = manager.Open(b, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);

        Assert.Equal(2, manager.Sessions.Count);
        Assert.Equal(a, one.WorkingDirectory);
        Assert.Equal(b, two.WorkingDirectory);
        Assert.NotSame(one.Host, two.Host);
    }

    [Fact]
    public void Close_ForgetsOneSessionAndLeavesTheOther()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var provider = new MockLlmProvider();

        var one = manager.Open(_dir, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);
        var two = manager.Open(_dir, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);

        manager.Close(one);

        Assert.Single(manager.Sessions);
        Assert.Same(two, manager.Sessions[0]);
    }

    // A manager over services somebody else owns must not close them — disposing what you did not
    // create is how a store closes underneath its second user.
    [Fact]
    public void Over_DoesNotOwnWhatItWasGiven()
    {
        var shared = new SharedServices { GlobalInstructionsDir = _dir };
        using (var manager = SessionManager.Over(shared))
            Assert.Same(shared, manager.Shared);

        Assert.Equal(_dir, shared.GlobalInstructionsDir);
    }

    [Fact]
    public void Dispose_ClosesEverySession()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var provider = new MockLlmProvider();
        manager.Open(_dir, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);
        manager.Open(_dir, Core.Llm.ProviderResolution.ForTesting(provider), Ports(), AgentMode.Single);

        manager.Dispose();

        Assert.Empty(manager.Sessions);
    }

    // A ROOT WHOSE ORDERING IS FIXED BY ITS UI still gets an owned session. cxagent builds its
    // session before the window (the startup banner needs its edit mode) and its gate after (a gate
    // needs a window), so Open's ordering is impossible there — and a collection that does not
    // contain the running session reads as authoritative while being wrong.
    [Fact]
    public void Adopt_TakesASessionBuiltBeforeTheManager()
    {
        var session = new Session(_dir);

        using var manager = SessionManager.Create(new AppPaths(_dir));
        manager.Adopt(session);

        Assert.Single(manager.Sessions);
        Assert.Same(session, manager.Sessions[0]);
    }

    [Fact]
    public void Adopt_IsIdempotent()
    {
        var session = new Session(_dir);
        using var manager = SessionManager.Create(new AppPaths(_dir));

        manager.Adopt(session);
        manager.Adopt(session);

        Assert.Single(manager.Sessions);
    }

    // THE HOOK IS THE WHOLE UI DEPENDENCY. Create owns the rules store, so it can hand it to a
    // caller that knows how to ask a human — which is the only part Core cannot supply. Before the
    // gate stopped holding a session's policy this was impossible: there was no session yet to give
    // it one.
    [Fact]
    public void Create_BuildsTheGateFromTheStoreItOwns()
    {
        Core.Permissions.PermissionRulesStore? handed = null;

        using var manager = SessionManager.Create(
            new AppPaths(_dir),
            buildGate: rules => { handed = rules; return Core.Permissions.PermissionGate.DenyAll; });

        Assert.NotNull(manager.Shared.Gate);
        Assert.Same(manager.Rules, handed);
    }

    [Fact]
    public void Create_WithNoHook_LeavesTheSessionsUngated()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        Assert.Null(manager.Shared.Gate);
        Assert.NotNull(manager.Rules);   // the store exists either way — /permissions reads it
    }

    // THE POINT OF MOVING THE DECIDER INTO CORE. A front end that is not a terminal supplies one
    // function — how to ask — and gets the whole pipeline: silent policy, stored rules, the auto
    // classifier's fail-closed behaviour, the trust floor, persistence. Before the move it would
    // have had to reimplement all of that, and a reimplemented fail-closed classifier is how one
    // quietly stops failing closed.
    [Fact]
    public async Task Create_CanBuildAFullyGatedSessionWithNoWindow()
    {
        var asked = 0;

        using var manager = SessionManager.Create(
            new AppPaths(_dir),
            buildGate: rules => Core.Permissions.PermissionDecider.WithPrompt(
                rules,
                notice: null,
                promptHook: (_, _, _) =>
                {
                    asked++;
                    return Task.FromResult(Core.Permissions.PermissionChoice.Deny);
                }));

        var policy = new Core.Permissions.PermissionPolicy(_dir, manager.Rules!);
        var request = new Core.Permissions.PermissionRequest(
            Core.Permissions.PermissionKind.Shell, "rm -rf /", null) { Policy = policy };

        var allowed = await manager.Shared.Gate!.RequestAsync(request, CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(1, asked);
    }
}
