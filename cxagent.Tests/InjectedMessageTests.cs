using CxAgent.Core.Sessions;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <see cref="Session.Inject"/> gives the model something the APP knows and it does not — a
/// terminal's transcript, a job that finished — without starting a turn to say it.
///
/// <para>THE PROPERTY UNDER TEST IS THE SILENCE. Anything can append text to a request; what makes
/// this mechanism correct is that injecting produces NO turn, so a user who has just closed a
/// terminal window is not talked at while they are still reading it.</para>
/// </summary>
public class InjectedMessageTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "injected-" + Guid.NewGuid().ToString("N"));

    public InjectedMessageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager manager, Session session, MockLlmProvider llm) Wired()
    {
        var llm = new MockLlmProvider();
        var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(llm),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, llm);
    }

    [Fact]
    public void Injecting_StartsNoTurn()
    {
        // THE WHOLE POINT. The person who closed the terminal may be reading it, thinking, or gone.
        var (manager, session, llm) = Wired();
        using var _1 = manager;

        session.Inject("[cxagent] the user ran a command: exited 0");

        Assert.Null(llm.LastMessages);
    }

    [Fact]
    public async Task TheNextMessage_CarriesTheInjectedTextAheadOfIt()
    {
        var (manager, session, llm) = Wired();
        using var _1 = manager;

        session.Inject("[cxagent] exited 0");
        var outcome = session.Submit("what happened?");
        if (outcome is Session.SubmitOutcome.Started started) await started.Turn;

        var sent = llm.LastMessages!.Last(m => m.Role == "user").Content;
        Assert.Contains("[cxagent] exited 0", sent);
        Assert.Contains("what happened?", sent);
        // ORDER IS THE CONTRACT: it describes what happened BEFORE this was typed.
        Assert.True(sent!.IndexOf("[cxagent]") < sent.IndexOf("what happened?"));
    }

    [Fact]
    public async Task ItIsDeliveredOnlyOnce()
    {
        var (manager, session, llm) = Wired();
        using var _1 = manager;

        session.Inject("[cxagent] exited 0");
        if (session.Submit("first") is Session.SubmitOutcome.Started a) await a.Turn;
        if (session.Submit("second") is Session.SubmitOutcome.Started b) await b.Turn;

        Assert.DoesNotContain("[cxagent] exited 0",
            llm.LastMessages!.Last(m => m.Role == "user").Content);
    }

    [Fact]
    public void ACommand_LeavesItQueued()
    {
        // /clear is not the user speaking TO THE MODEL, so there is nothing for the transcript to
        // arrive ahead of yet. Taking it here would destroy it: prepending would also stop the text
        // starting with a slash, turning the command into prose.
        var (manager, session, llm) = Wired();
        using var _1 = manager;

        session.Inject("[cxagent] exited 0");
        session.Submit("/clear");

        Assert.Null(llm.LastMessages);
        Assert.Equal("[cxagent] exited 0", session.TakeInjected());
    }

    [Fact]
    public void TwoInjections_BothSurvive()
    {
        var (manager, session, _) = Wired();
        using var _1 = manager;

        session.Inject("first event");
        session.Inject("second event");

        var taken = session.TakeInjected();
        Assert.Contains("first event", taken);
        Assert.Contains("second event", taken);
    }

    [Fact]
    public void NothingInjected_ChangesNothing()
    {
        var (manager, session, _) = Wired();
        using var _1 = manager;

        Assert.Null(session.TakeInjected());
        session.Inject("   ");
        Assert.Null(session.TakeInjected());
    }
}
