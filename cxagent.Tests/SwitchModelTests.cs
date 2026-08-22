using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// /model points a session at a different model WITHOUT rebuilding it.
///
/// <para>WHAT IT REPLACED. The command used to arm a handoff, re-wire the whole session and dispose
/// the outgoing host — rebuilding the agent, its executor registry, its sub-agent factory and its MCP
/// binding in order to change which endpoint gets called, then carrying the context and the ledger
/// back across the gap by hand. Everything but the provider was rebuilt identically, because /model
/// reads the same config file it always did.</para>
/// </summary>
public class SwitchModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "switch-" + Guid.NewGuid().ToString("N"));

    public SwitchModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private static SessionPorts Ports() =>
        new() { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };

    private Session WiredSession(SessionManager manager, ILlmProvider provider) =>
        manager.Open(_dir, ResolvedConfig.ForTesting(provider), Ports(), AgentMode.Single);

    // THE CONVERSATION SURVIVES, which is the whole point. A rebuild had to carry it; there is
    // nothing to carry when nothing is rebuilt.
    [Fact]
    public async Task SwitchModel_KeepsTheConversation()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var first = new MockLlmProvider("model-one");
        first.EnqueueResponse(new LlmResponse { Text = "hello", StopReason = "end_turn" });

        var session = WiredSession(manager, first);
        await session.SendAndWait("say hello");

        var hostBefore = session.Host;
        var contextBefore = session.Host!.Context;
        var messagesBefore = session.Host!.Context.Messages.Count;

        Assert.Equal(CommandStatus.Changed, session.Use(ResolvedConfig.ForTesting(new MockLlmProvider("model-two"), "second").Model));

        // THE SAME OBJECTS, not merely equal ones. A rebuild would produce a new host over a new
        // context and copy the messages across — which is what the old path did, and what made
        // CarryToNextWire necessary. Identity is the assertion that tells the two apart.
        Assert.Same(hostBefore, session.Host);
        Assert.Same(contextBefore, session.Host!.Context);
        Assert.Equal(messagesBefore, session.Host!.Context.Messages.Count);
    }

    // THE SESSION'S OWN COPIES FOLLOW. /model's completions and the panel read InstanceName from the
    // session, so leaving it behind would offer the user the model they just switched away from.
    [Fact]
    public void SwitchModel_MovesTheSessionsProviderAndInstance()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = WiredSession(manager, new MockLlmProvider("model-one"));

        var next = new MockLlmProvider("model-two");
        session.Use(ResolvedConfig.ForTesting(next, "second").Model);

        Assert.Same(next, session.Provider);
        Assert.Equal("second", session.InstanceName);
    }

    // THE WINDOW COMES TOO, and it is the only part with behaviour attached: a session moving to a
    // smaller-context model keeps every message it had and must measure against the new denominator.
    // The turn loop tests pressure before composing each request, so the next turn compacts if it
    // must — nothing is forced here.
    [Fact]
    public void SwitchModel_MovesTheContextWindow()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = WiredSession(manager, new MockLlmProvider("big"));

        var narrow = ResolvedConfig.ForTesting(new MockLlmProvider("small"), "small").WithContextWindow(8_000);
        session.Use(narrow.Model);

        Assert.Equal(8_000, session.Host!.Context.Window);
    }

    // THE NEW MODEL IS THE ONE CALLED. The point of the whole exercise, and the one thing a swap
    // that moved only labels would still get wrong.
    [Fact]
    public async Task SwitchModel_SendsTheNextTurnToTheNewProvider()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = WiredSession(manager, new MockLlmProvider("model-one"));

        var next = new MockLlmProvider("model-two");
        next.EnqueueResponse(new LlmResponse { Text = "from two", StopReason = "end_turn" });
        session.Use(ResolvedConfig.ForTesting(next, "second").Model);

        await session.SendAndWait("who are you");

        Assert.Equal(1, next.ChatCallCount);
    }

    // THE SESSION SAYS SO ITSELF, through the observer every front end already watches. The
    // composition root used to compose this sentence — reading the context window and usage before
    // the switch, in that order — so a second front end would have reimplemented both the wording
    // and the ordering, and the first to get the order wrong reports the new window against the old
    // usage with nothing to catch it.
    [Fact]
    public void SwitchModel_AnnouncesItselfThroughTheObserver()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var sink = new BufferedChatSink();

        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("model-one")),
            new SessionPorts { Observer = sink, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);

        session.Use(ResolvedConfig.ForTesting(new MockLlmProvider("model-two"), "second").WithContextWindow(8_000).Model);

        var notice = Assert.Single(sink.Notices);
        Assert.Contains("second:model-two", notice);
        Assert.Contains("8k window", notice);

        // NOT AN ERROR. A mode or model change is not a fault, and a caller watching for failures
        // must not find one here.
        Assert.Empty(sink.Errors);
    }

    // THE CHILDREN'S DEFAULT MOVES TOO. A sub-agent with no provider of its own inherits from the
    // spawner, which held the model captured at wire time — so every child kept talking to the model
    // the session started on. Confirmed in the usage archive before the fix: every explore run after
    // a /model switch still recorded the old instance, while the switch notice promised "sub-agents
    // use this too unless their type names another provider".
    [Fact]
    public void SwitchModel_MovesTheDefaultFutureChildrenInherit()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("first")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var spawner = new RecordingSpawner();
        session.NoteSpawner(spawner);

        var next = new MockLlmProvider("second");
        session.Use(ResolvedConfig.ForTesting(next, "second").WithContextWindow(9_000).Model);

        Assert.Same(next, spawner.Provider);
        Assert.Equal("second", spawner.InstanceName);
        Assert.Equal(9_000, spawner.ContextWindow);
    }

    private sealed class RecordingSpawner : ISubAgentSpawner
    {
        public ILlmProvider? Provider;
        public string? InstanceName;
        public int? ContextWindow;

        public string ToolName => "spawn";

        public void SwapDefaultProvider(ILlmProvider provider, int? contextWindow, string? instanceName)
        {
            Provider = provider;
            ContextWindow = contextWindow;
            InstanceName = instanceName;
        }

        // NEVER CALLED — this double exists to observe the swap, not to spawn.
        public ToolDefinition Definition => throw new NotSupportedException();

        public Task<string?> TryInvokeAsync(ToolCall call, Action<SubAgent>? onChild,
            CancellationToken ct, string? label = null,
            CxAgent.Core.Jobs.ToolSelection? turnTools = null) => throw new NotSupportedException();
    }

    // THE CATALOG SURVIVES A SWITCH, which is what the split makes structural rather than
    // conventional: SwitchModel takes an ActiveModel, so there is no configuration in scope to
    // replace by accident. Passing a whole ResolvedConfig no longer compiles — verified by hand, and
    // this pins the behaviour that guarantee protects.
    [Fact]
    public void SwitchModel_KeepsTheCatalog()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));

        var catalog = new ProviderCatalog(
            AgentTypes: new Dictionary<string, AgentTypeConfig> { ["surveyor"] = new("read only") },
            ClassifierInstance: "first");

        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("first")).WithCatalog(catalog),
            Ports());

        session.Use(ResolvedConfig.ForTesting(new MockLlmProvider("second"), "second").Model);

        // The model moved…
        Assert.Equal("second", session.InstanceName);

        // …and everything the process was configured with did not.
        Assert.Same(catalog, session.Resolution!.Catalog);
        Assert.Equal("first", session.Resolution.ClassifierInstance);
        Assert.Contains("surveyor", session.Resolution.AgentTypes.Keys);
    }

    // NO HOST, NO SWITCH. A /model before the first wire has nothing to point anywhere, and the
    // caller is told rather than this throwing.
    [Fact]
    public void SwitchModel_WithNoHost_IsRefused()
    {
        var session = new Session(_dir);

        Assert.Equal(CommandStatus.Refused, session.Use(ResolvedConfig.ForTesting(new MockLlmProvider()).Model));
    }
}
