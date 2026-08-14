using CxAgent.Core.Agent;
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
}
