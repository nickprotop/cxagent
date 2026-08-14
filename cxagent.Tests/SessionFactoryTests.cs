using CxAgent.Core.Agent;
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
            Tools = new BufferedJobPanel(),
        };

        Assert.NotNull(ports.Observer);
        Assert.NotNull(ports.Tools);
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
                Core.Llm.ProviderResolution.ForTesting(provider),
                new SharedServices(),
                new SessionPorts { Observer = observer, Tools = new BufferedJobPanel() },
                AgentMode.Single);

            Assert.NotNull(session.Host);
            await session.Host!.SendAsync("go", CancellationToken.None);

            Assert.Contains("wired", observer.Transcript, StringComparison.Ordinal);
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

            SessionFactory.Wire(session, Core.Llm.ProviderResolution.ForTesting(new MockLlmProvider("first")),
                new SharedServices(),
                new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
                AgentMode.Single);
            var first = session.Host;

            SessionFactory.Wire(session, Core.Llm.ProviderResolution.ForTesting(new MockLlmProvider("second")),
                new SharedServices(),
                new SessionPorts { Observer = new BufferedChatSink(), Tools = new BufferedJobPanel() },
                AgentMode.Single);

            Assert.NotSame(first, session.Host);
            Assert.Equal("second", session.Provider!.ModelId);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
