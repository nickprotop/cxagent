using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using CxAgent.Core.Commands;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// That a session actually SPEAKS through its fan-outs.
///
/// <para>THE FAILURE THIS GUARDS IS SILENCE. A fan-out that is built but never reached looks exactly
/// like one that works, because the port's own observer is still subscriber one and still receives
/// everything — so the app behaves correctly while the second subscriber, the entire point, gets
/// nothing. Only a test that adds a second listener and watches it can tell those apart.</para>
/// </summary>
public class SessionFanOutWiringTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cxagent-fanout-" + Guid.NewGuid().ToString("N"));

    public SessionFanOutWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager Manager, Session Session) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir,
            ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts
            {
                Observer = new BufferedChatSink(),
                ToolObserver = new BufferedJobPanel(),
            },
            AgentMode.Single);
        return (manager, session);
    }

    [Fact]
    public void AWiredSessionExposesBothFanOuts()
    {
        var (manager, session) = Wired();
        using (manager)
        {
            Assert.NotNull(session.Observers);
            Assert.NotNull(session.ToolObservers);

            // The port's own observer is subscriber one — behaviour is unchanged with one listener.
            Assert.Equal(1, session.Observers!.Count);
            Assert.Equal(1, session.ToolObservers!.Count);
        }
    }

    /// <summary>
    /// A SECOND LISTENER RECEIVES WHAT THE SESSION SAYS. This is the property the whole task exists
    /// for: before the fan-out, adding one replaced the first.
    /// </summary>
    [Fact]
    public void ASecondListenerHearsTheSession()
    {
        var (manager, session) = Wired();
        using (manager)
        {
            var second = new BufferedChatSink();
            using var token = session.Observers!.Add(second);

            session.Observers.Said(new Message("hello"));

            Assert.Equal(2, session.Observers.Count);
            Assert.Contains("hello", second.Transcript, StringComparison.Ordinal);
        }
    }

    /// <summary>AND DETACHING LEAVES THE FIRST ALONE — the original port observer must survive a
    /// second listener coming and going, or a front end closing would silence the app.</summary>
    [Fact]
    public void DetachingASecondListenerLeavesTheFirst()
    {
        var (manager, session) = Wired();
        using (manager)
        {
            var token = session.Observers!.Add(new BufferedChatSink());
            token.Dispose();

            Assert.Equal(1, session.Observers.Count);
        }
    }

    /// <summary>A SESSION BUILT DIRECTLY HAS NONE. The ports belong to the factory, so a caller that
    /// wired its own observers by hand owns them — null says so rather than pretending.</summary>
    [Fact]
    public void ASessionBuiltDirectlyHasNoFanOuts()
    {
        var session = new Session(_dir);

        Assert.Null(session.Observers);
        Assert.Null(session.ToolObservers);
    }
}
