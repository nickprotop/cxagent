using CxAgent.Core.Llm;
using CxAgent.Core.Permissions;
using CxAgent.Core.Storage;
using CxAgent.Hosting;
using Xunit;

namespace CxAgent.Tests;

/// <summary>The one routine every front end wires sessions through: what it builds, and what it asks the front end for.</summary>
public sealed class SessionComposerTests : IDisposable
{
    private readonly string _cfg = Directory.CreateTempSubdirectory("composer-cfg-").FullName;
    private readonly string _work = Directory.CreateTempSubdirectory("composer-work-").FullName;
    private readonly AppPaths _paths;
    private readonly SessionManager _manager;
    private readonly SessionComposer _composer;
    private readonly ResolvedConfig _config = ResolvedConfig.ForTesting(new MockLlmProvider("m"));

    public SessionComposerTests()
    {
        _paths = new AppPaths(_cfg);
        _manager = SessionManager.Create(_paths);
        _composer = new SessionComposer(new ComposerServices(_manager, _paths, new PermissionRulesStore(_paths), null, null));
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_cfg, recursive: true); } catch (Exception) { }
        try { Directory.Delete(_work, recursive: true); } catch (Exception) { }
    }

    private sealed class FakeFrontEnd : IFrontEnd
    {
        public BufferedChatSink Chat { get; private set; } = new();
        public List<Session> Asked { get; } = [];
        public List<(Session Session, PermissionPolicy Policy)> WiredCalls { get; } = [];

        public SessionSinks SinksFor(Session session)
        {
            Chat = new BufferedChatSink();
            return new SessionSinks(Chat, new BufferedJobPanel());
        }

        public AskUser? AskFor(Session session)
        {
            Asked.Add(session);
            return null;
        }

        public void Wired(Session session, SessionSinks sinks, PermissionPolicy policy) => WiredCalls.Add((session, policy));
    }

    [Fact]
    public void Create_wires_a_session_with_the_front_ends_sinks()
    {
        var front = new FakeFrontEnd();
        var session = _composer.Create(_work, _config, AgentMode.Single, front);

        Assert.NotNull(session.SessionId);
        session.NotifyWarning("reaches the sink");
        Assert.Contains("reaches the sink", front.Chat.Transcript, StringComparison.Ordinal);
        Assert.Single(front.WiredCalls);
        Assert.Same(session, front.Asked.Single());
        Assert.Contains(session, _manager.Sessions);
    }

    [Fact]
    public void Wire_builds_a_policy_scoped_to_the_session()
    {
        var front = new FakeFrontEnd();
        var session = _composer.Create(_work, _config, new WorkingMode(AgentMode.Single, EditMode.Auto), front);
        var policy = front.WiredCalls.Single().Policy;

        Assert.Equal(_work, policy.Root);
        Assert.Equal(session.Id, policy.SessionId);
        Assert.Equal(_paths.LogsDir, policy.LogDir);
        Assert.Equal(EditMode.Auto, policy.Edits);
        Assert.Same(policy, session.Policy);
    }

    [Fact]
    public void A_kept_policy_is_used_as_is()
    {
        var front = new FakeFrontEnd();
        var session = new Session(_work);
        var kept = new PermissionPolicy(_work, new PermissionRulesStore(_paths), EditMode.AcceptEdits);

        var used = _composer.Wire(new WireRequest(session, _config, AgentMode.Single) { Policy = kept }, front);

        Assert.Same(kept, used);
        Assert.Same(kept, session.Policy);
    }

    [Fact]
    public void Rewire_asks_the_front_end_again_and_moves_to_a_new_agent()
    {
        var front = new FakeFrontEnd();
        var session = _composer.Create(_work, _config, AgentMode.Single, front);
        var first = session.SessionId;

        _composer.Wire(new WireRequest(session, _config, AgentMode.Single) { Policy = session.Policy }, front);

        Assert.Equal(2, front.WiredCalls.Count);
        Assert.NotEqual(first, session.SessionId);
        Assert.Single(_manager.Sessions);
    }

    [Fact]
    public async Task No_plugins_configured_loads_nothing_and_says_nothing()
    {
        var session = _composer.Create(_work, _config, AgentMode.Single, new FakeFrontEnd());
        var said = new List<string>();
        await _composer.LoadPluginsAsync(session, _config, said.Add, CancellationToken.None);
        Assert.Empty(said);
    }
}
