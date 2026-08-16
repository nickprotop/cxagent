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
}
