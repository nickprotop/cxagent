using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.UI;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// What a saved configuration's rebind keeps of the tab it re-wires: the mode the user set in that tab
/// and the policy enforcing it — never the mode the app was launched in.
/// </summary>
public class SessionRebindKeepsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rebind-keeps-" + Guid.NewGuid().ToString("N"));
    public SessionRebindKeepsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager Manager, Session Session, PermissionPolicy Policy) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var policy = new PermissionPolicy(_dir, new PermissionRulesStore(new AppPaths(_dir)));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Policy = policy },
            AgentMode.Single);
        return (manager, session, policy);
    }

    // A TAB SWITCHED TO ACCEPT-EDITS STAYS THERE: a model change is not a mode change, and the launch
    // mode coming back would silently narrow — or, launched permissive, silently widen — what it may do.
    [Fact]
    public void ARebind_KeepsTheModeTheTabIsIn_NotTheLaunchMode()
    {
        var (manager, session, _) = Wired();
        using var _m = manager;
        var chosen = new WorkingMode(AgentMode.Single, EditMode.AcceptEdits);
        session.SetMode(chosen);

        var kept = SessionWiring.KeptByRebind(session, launch: WorkingMode.Default);

        Assert.Equal(chosen, kept.Mode);
    }

    // AND THE POLICY ENFORCING IT, which also holds the tab's session-scoped grants.
    [Fact]
    public void ARebind_KeepsTheTabsOwnPolicy()
    {
        var (manager, session, policy) = Wired();
        using var _m = manager;

        Assert.Same(policy, SessionWiring.KeptByRebind(session, WorkingMode.Default).Policy);
    }

    // AN UNWIRED SESSION HAS NO MODE OF ITS OWN YET: it starts in the launch mode.
    [Fact]
    public void AnUnwiredSession_StartsInTheLaunchMode()
    {
        var launch = new WorkingMode(AgentMode.Single, EditMode.AcceptEdits);

        var kept = SessionWiring.KeptByRebind(new Session(_dir), launch);

        Assert.Equal(launch, kept.Mode);
        Assert.Null(kept.Policy);
    }
}
