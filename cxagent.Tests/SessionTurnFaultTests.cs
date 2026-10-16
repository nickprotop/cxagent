using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>A turn loop that faults outside a lap's own error handling still answers whoever awaits it.</summary>
public class SessionTurnFaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "turn-fault-" + Guid.NewGuid().ToString("N"));
    public SessionTurnFaultTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>
    /// A FAULT BEFORE THE LAP'S TRY — in the loop's own bookkeeping — leaves Result and Outcome answered:
    /// null and Failed. Left pending, a caller awaiting the turn's answer would wait forever.
    /// </summary>
    [Fact]
    public async Task AFaultBeforeTheLap_StillCompletesResultAndOutcome()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
        session.AtLapStartForTest = () => throw new InvalidOperationException("bookkeeping fault");

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Submit("go"));

        Assert.Null(await started.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SendOutcome.Failed, await started.Outcome.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => started.Turn);
        Assert.False(session.TurnOwned);
    }
}
