using CxAgent.Core.Agents;
using CxAgent.Core.Llm;
using CxAgent.Core.Sessions;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>One loop per conversation, and nothing left in the mailbox when a turn ends.</summary>
public class TurnOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "turn-own-" + Guid.NewGuid().ToString("N"));
    private SessionManager? _manager;
    private BufferedChatSink? _sink;
    public TurnOwnershipTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { _manager?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Session Open(ILlmProvider provider)
    {
        _manager = SessionManager.Create(new AppPaths(_dir));
        return _manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = _sink = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);
    }

    private static async Task Idle(Session s) => Assert.True(await Poll.Until(() => !s.IsBusy && !s.TurnOwned));

    /// <summary>TWENTY SUBMITS AT ONCE, ONE LOOP: the rest join it, none starts its own.</summary>
    [Fact]
    public async Task ConcurrentSubmits_StartOneLoop()
    {
        var held = new HeldProvider();
        var s = Open(held);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => s.Submit($"m{i}", source: Session.SubmitSource.System))));
        held.Release();
        await Idle(s);

        Assert.Single(outcomes, o => o is Session.SubmitOutcome.Started);
        Assert.Equal(1, held.MaxInFlight);
    }

    /// <summary>
    /// A REPORT IN THE TURN'S TAIL — after the agent's last mailbox check, before the turn is released —
    /// is read by a lap of its own that draws nothing (its note was drawn when it arrived).
    /// </summary>
    [Fact]
    public async Task AReportInTheTurnsTail_StartsAQuietLap()
    {
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "read it", StopReason = "end_turn" });
        var s = Open(model);
        var once = 0;
        s.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
                s.Submit("the late report", source: Session.SubmitSource.System);
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go")).Turn;
        await Idle(s);

        Assert.Equal(2, model.ChatCallCount);
        Assert.Contains("the late report", model.Text());
        Assert.DoesNotContain(_sink!.Notices, n => n.Contains("the late report"));
    }

    /// <summary>A STEER TYPED IN THE TAIL IS NOT LOST: it starts the next lap as the user's turn.</summary>
    [Fact]
    public async Task ASteerInTheTurnsTail_StartsTheUsersLap()
    {
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "first", StopReason = "end_turn" });
        model.EnqueueResponse(new LlmResponse { Text = "second", StopReason = "end_turn" });
        var s = Open(model);
        var once = 0;
        s.BeforeTurnReleasedForTest = () => { if (Interlocked.Exchange(ref once, 1) == 0) s.Submit("also this"); };

        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go")).Turn;
        await Idle(s);

        Assert.Equal(2, model.ChatCallCount);
        Assert.Contains("also this", _sink!.Transcript);
    }

    /// <summary>A NOTE THAT DOES NOT WAKE neither extends a turn nor starts one.</summary>
    [Fact]
    public async Task ALeftNote_NeitherExtendsNorStartsATurn()
    {
        var held = new HeldProvider("answer");
        var s = Open(held);
        s.Submit("go");
        Assert.True(await Poll.Until(() => held.Calls == 1));

        Assert.True(s.Host!.TryLeave("a stopped agent's note"));
        held.Release();
        await Idle(s);
        await Task.Delay(200);

        Assert.Equal(1, held.Calls);
    }

    /// <summary>
    /// A NOTE LEFT WHILE IDLE IS READ BEFORE THE NEXT PROMPT, not after it: it happened first, and the
    /// newest thing in front of the model must be what the user just asked.
    /// </summary>
    [Fact]
    public async Task ALeftNote_IsReadBeforeTheNextPrompt()
    {
        var model = new MockLlmProvider();
        model.EnqueueResponse(new LlmResponse { Text = "ok", StopReason = "end_turn" });
        var s = Open(model);

        Assert.True(s.Host!.TryLeave("agent x was stopped"));
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("what next?")).Turn;

        var contents = model.LastMessages!.Select(m => m.Content ?? "").ToList();
        var note = contents.FindIndex(c => c.Contains("agent x was stopped"));
        var prompt = contents.FindIndex(c => c.Contains("what next?"));
        Assert.True(note >= 0 && note < prompt);
    }

    /// <summary>A WAKING MESSAGE DURING THE LAST REQUEST STILL EXTENDS THE TURN (b0bdc01 holds).</summary>
    [Fact]
    public async Task AWakingMessageDuringTheLastRequest_StillExtendsTheTurn()
    {
        var held = new HeldProvider("a", "b");
        var s = Open(held);
        s.Submit("go");
        Assert.True(await Poll.Until(() => held.Calls == 1));

        Assert.IsType<Session.SubmitOutcome.Queued>(s.Submit("report", source: Session.SubmitSource.System));
        held.Release();
        await Idle(s);

        Assert.Equal(2, held.Calls);
    }
}
