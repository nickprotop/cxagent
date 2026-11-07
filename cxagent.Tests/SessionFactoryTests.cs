using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// Assembling a session, and the two lifetimes that assembly needs.
///
/// <para>THE RECORDS ARE NAMED FOR HOW LONG THEY LIVE, not for which layer implements them. An
/// earlier sketch called the ports "presentation" — wrong, and the same mistake three commits were
/// spent fixing on the observer interfaces: ISessionObserver and IToolObserver are Core types over
/// Core's own UserQuestion and QuestionAnswers. The UI implements them; so does BufferedChatSink;
/// so would a server.</para>
///
/// <para>What actually differs is sharing. Two sessions SHARE the history database — that is what
/// makes /stats span sessions, and TwoSessionsTests proves it is safe. They must NOT share a
/// transcript: that is two conversations in one scrollback.</para>
/// </summary>
public class SessionFactoryTests
{
    /// <summary>Every member optional, because a headless session legitimately has none of them —
    /// no logs, no resume buffer, no history, no MCP, no gate.</summary>
    [Fact]
    public void SharedServices_AreAllOptional()
    {
        var shared = new SharedServices();

        Assert.Null(shared.Logs);
        Assert.Null(shared.Resume);
        Assert.Null(shared.History);
        Assert.Null(shared.Mcp);
        Assert.Null(shared.Gate);
        Assert.Null(shared.GlobalInstructionsDir);
    }

    /// <summary>
    /// THE OBSERVERS ARE REQUIRED, THE ASK IS NOT. A session always reports — to a transcript, a
    /// buffer or a log — but a headless run has nobody to ask, and that is a legitimate state
    /// rather than a degraded one.
    /// </summary>
    [Fact]
    public void SessionPorts_RequireObserversButNotAnAsk()
    {
        var ports = new SessionPorts
        {
            Observer = new BufferedChatSink(),
            ToolObserver = new BufferedJobPanel(),
        };

        Assert.NotNull(ports.Observer);
        Assert.NotNull(ports.ToolObserver);
        Assert.Null(ports.Ask);
    }

    /// <summary>
    /// A SESSION ASSEMBLES WITH NO UI AT ALL — the property this whole change exists for. Before
    /// it, starting a session meant 136 lines inside AppBootstrap.WireRunner, so a headless host
    /// had to reimplement or copy them.
    /// </summary>
    [Fact]
    public async Task Wire_BuildsAWorkingSession_WithNoUi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new MockLlmProvider();
            provider.EnqueueResponse(new Core.Llm.LlmResponse { Text = "wired", StopReason = "end_turn" });

            var session = new Session(dir);
            var observer = new BufferedChatSink();

            SessionFactory.Wire(session,
                Core.Llm.ResolvedConfig.ForTesting(provider),
                new SharedServices(),
                new SessionPorts { Observer = observer, ToolObserver = new BufferedJobPanel() },
                AgentMode.Single);

            Assert.NotNull(session.Host);
            await session.SendAndWait("go");

            Assert.Contains("wired", observer.Transcript, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A CLEARED CONVERSATION IS EMPTY, AND SAYS SO: the model's count of the request before the clear is not what the
    /// context holds after it, and a browser's gauge reads this, not the terminal's own.
    /// </summary>
    [Fact]
    public async Task Clearing_forgets_the_last_reported_context_size()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new Session(dir);
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new AnswersWithUsageProvider(8_500)),
                new SharedServices(),
                new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
                AgentMode.Single);
            await session.SendAndWait("go");
            Assert.Equal(8_500, session.ContextUsed);

            session.ClearContext();

            Assert.Null(session.ContextUsed);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// WIRING TWICE REPLACES, and disposes what it replaced — an F5 provider change builds a fresh
    /// host over the same conversation. Session.ReplaceHost owns the disposal, which is the step a
    /// caller forgets when the host is a bare local.
    /// </summary>
    [Fact]
    public void Wire_Twice_ReplacesTheHost()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cxagent-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new Session(dir);

            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider("first")),
                new SharedServices(),
                new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
                AgentMode.Single);
            var first = session.Host;

            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider("second")),
                new SharedServices(),
                new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
                AgentMode.Single);

            Assert.NotSame(first, session.Host);
            Assert.Equal("second", session.Provider!.ModelId);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A WIRE THAT RESTORED A CONVERSATION SAYS SO, once, to the front end that opened it and to the session's other
    /// watchers — the moment the restored past is drawn. A plain wire says nothing.
    /// </summary>
    [Fact]
    public void Only_a_wire_that_restored_a_conversation_reports_it()
    {
        var dir = Directory.CreateTempSubdirectory("cxagent-factory-").FullName;
        try
        {
            var session = new Session(dir);
            var (ports, watchers) = (0, 0);
            session.Restored += _ => watchers++;
            SessionPorts Ports() => new()
            {
                Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel(), Restored = _ => ports++,
            };

            var shared = new SharedServices { Logs = new Core.Storage.LogFileManager(new Core.Storage.AppPaths(Path.Combine(dir, "cfg"))) };
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider()), shared, Ports(), AgentMode.Single);
            Assert.Equal((0, 0), (ports, watchers));

            session.PendResume(new Core.Storage.SessionSnapshot("OLD", [new Core.Models.ChatMessage { Role = "user", Content = "a" }],
                0, 0, DateTimeOffset.UtcNow, Edits: null, WorkingDir: dir));
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider()), shared, Ports(), AgentMode.Single);
            Assert.Equal((1, 1), (ports, watchers));

            // A /model OR CONFIG SWITCH CARRIES THE CONVERSATION THROUGH THE SAME PENDING SNAPSHOT, into a tab that
            // already shows it: drawing the past again would print the whole conversation twice.
            Assert.True(session.CarryToNextWire());
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(new MockLlmProvider()), shared, Ports(), AgentMode.Single);
            Assert.Equal((1, 1), (ports, watchers));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A WATCHER OF THE SESSION'S WORKERS KEEPS WATCHING ACROSS A RE-WIRE. Subscribed through the host, it was dropped
    /// with the host a /model switch replaced, and the browser drew no card for any worker spawned after one.
    /// </summary>
    [Fact]
    public async Task A_worker_spawned_after_a_rewire_is_still_announced()
    {
        var dir = Directory.CreateTempSubdirectory("cxagent-factory-").FullName;
        try
        {
            var provider = new MockLlmProvider();
            var session = new Session(dir);
            SessionPorts Ports() => new() { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() };
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(provider), new SharedServices(), Ports(), AgentMode.FanOut);
            var spawned = 0;
            session.WorkerSpawned += _ => spawned++;

            Assert.True(session.CarryToNextWire());
            SessionFactory.Wire(session, Core.Llm.ResolvedConfig.ForTesting(provider), new SharedServices(), Ports(), AgentMode.FanOut);

            provider.EnqueueResponse(new Core.Llm.LlmResponse
            {
                Text = "",
                ToolCalls = [new Core.Models.ToolCall { Id = "c1", Name = "agent",
                    Arguments = System.Text.Json.JsonDocument.Parse("""{"prompt":"say hi","background":false}""").RootElement.Clone() }],
                StopReason = "tool_use",
            });
            provider.EnqueueResponse(new Core.Llm.LlmResponse { Text = "hi", StopReason = "end_turn" });
            provider.EnqueueResponse(new Core.Llm.LlmResponse { Text = "done", StopReason = "end_turn" });
            await session.SendAndWait("spawn one");

            Assert.Equal(1, spawned);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
