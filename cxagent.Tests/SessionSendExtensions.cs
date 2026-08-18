using CxAgent.Core.Sessions;

namespace CxAgent.Tests;

/// <summary>
/// Runs one turn and waits for it — the shape tests used before <c>AgentHost.SendAsync</c> collapsed
/// into <see cref="Session.Submit"/>.
///
/// <para>WHY A HELPER RATHER THAN 33 REWRITES. Every call site wants the same three lines: send,
/// check that a turn actually began, await it. Spelling that out everywhere would bury what each
/// test is really about, and a site that quietly dropped the "did it begin" check would pass while
/// testing nothing — a turn that was QUEUED instead of started completes no task and asserts
/// nothing.</para>
///
/// <para>IT THROWS ON THE OTHER TWO DISPOSITIONS on purpose. A test whose session was busy, or had no
/// agent, has already diverged from what it meant to exercise; failing there is more useful than an
/// assertion further down that reads as a product bug.</para>
/// </summary>
internal static class SessionSendExtensions
{
    public static async Task SendAndWait(this Session session, string text)
    {
        var disposition = session.Submit(text);

        if (disposition is not Session.SubmitOutcome.Started started)
            throw new InvalidOperationException(
                $"expected a turn to start, got {disposition.GetType().Name}");

        await started.Turn;
    }
}
