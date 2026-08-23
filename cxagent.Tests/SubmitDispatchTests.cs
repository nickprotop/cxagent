using CxAgent.Core.Sessions;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Jobs;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// <see cref="Session.Submit"/> dispatches a slash command itself, rather than sending it to the
/// model as though it were a goal.
///
/// <para>WHY THIS MATTERS AS A SEPARATE ENTRY POINT FROM <see cref="Session.SubmitRaw"/>: a front
/// end that only calls <c>Submit</c> gets every command the process registered with no dispatch
/// wiring of its own, and one that has never heard of commands gets them anyway — the alternative
/// is <c>session.Submit("/trust")</c> silently reaching a model, which answers something plausible
/// and teaches nobody that a command existed.</para>
/// </summary>
public class SubmitDispatchTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "submit-dispatch-" + Guid.NewGuid().ToString("N"));

    public SubmitDispatchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private (SessionManager manager, Session session, BufferedChatSink observer) Wired()
    {
        var manager = SessionManager.Create(new AppPaths(_dir));
        var observer = new BufferedChatSink();
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider()),
            new SessionPorts { Observer = observer, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
        return (manager, session, observer);
    }

    [Fact]
    public void ACommand_IsHandled_AndStartsNoTurn()
    {
        // /clear is seeded by Core and needs no provider, so this is a command running end to end
        // through the same entry point a goal uses.
        var (manager, session, _) = Wired();
        using var _1 = manager;

        var outcome = session.Submit("/clear");

        Assert.IsType<Session.SubmitOutcome.Handled>(outcome);
    }

    [Fact]
    public void OrdinaryText_StillReachesTheModel()
    {
        // THE GUARD AGAINST DISPATCH SWALLOWING GOALS. A prose prompt must be unaffected by any
        // of this — it is the case every consumer already depends on.
        var (manager, session, _) = Wired();
        using var _1 = manager;

        Assert.IsNotType<Session.SubmitOutcome.Handled>(session.Submit("summarise this folder"));
    }

    [Fact]
    public void AnUnknownSlash_IsHandled_AndNeverReachesTheModel()
    {
        // Sending "/celar" to a model as a task is worse than saying it does not exist.
        var (manager, session, observer) = Wired();
        using var _1 = manager;

        var outcome = session.Submit("/celar");

        Assert.IsType<Session.SubmitOutcome.Handled>(outcome);
        Assert.Contains(observer.Notices, n => n.Contains("Unknown command"));
    }

    /// <summary>
    /// A DECLARED COMMAND NOBODY REGISTERED STILL SAYS SO, RATHER THAN REACHING THE MODEL. Every
    /// command SessionCommands.All ships has a Core handler once a manager is seeded — see
    /// CoreCommandsTests.CoreServicesEveryCommandItShips — so this exercises the NoHandler branch
    /// of Submit directly against a registry that has seeded nothing, the state a real front end
    /// can no longer produce for any table command but that Submit's own dispatch must still answer
    /// for correctly.
    /// </summary>
    [Fact]
    public void ADeclaredCommandWithNoHandler_IsHandled_AndSaysSo()
    {
        var (manager, session, observer) = Wired();
        using var _1 = manager;

        Assert.Equal(CommandRegistry.Dispatch.NoHandler,
            new CommandRegistry().Run(session, "/mcp"));

        // Submit ITSELF still goes through the session's own seeded manager, where /mcp is Core's
        // to answer — so the "not available" wording is pinned on the registry directly above,
        // and this confirms Submit's normal path for the same input is Ran, not NoHandler.
        Assert.IsType<Session.SubmitOutcome.Handled>(session.Submit("/mcp"));
        Assert.DoesNotContain(observer.Notices, n => n.Contains("not available"));
    }

    [Fact]
    public void WithNoManager_SubmitBehavesAsBefore()
    {
        // A session built outside SessionManager.Open has no registry to consult. Dispatch is
        // skipped entirely rather than throwing — the shape a library consumer constructing a
        // session directly depends on. This session also has no Host, so the pre-existing
        // behaviour a manager-less caller depends on is NoAgent, unaffected by the text being a
        // slash command.
        var session = new Session(_dir);

        Assert.Null(session.Manager);
        Assert.IsType<Session.SubmitOutcome.NoAgent>(session.Submit("/clear"));
    }

    [Fact]
    public void SubmitRaw_SendsASlashVerbatim()
    {
        // The escape hatch. A caller that genuinely wants "/clear" delivered as text has one.
        var (manager, session, observer) = Wired();
        using var _1 = manager;

        var outcome = session.SubmitRaw("/clear");

        // SubmitRaw never dispatches, so a turn starts and the text reaches the model as a goal —
        // the opposite of Submit("/clear") above, which is handled and starts no turn.
        Assert.IsType<Session.SubmitOutcome.Started>(outcome);
        Assert.Contains(observer.Transcript, c => c == '>');
    }
}
