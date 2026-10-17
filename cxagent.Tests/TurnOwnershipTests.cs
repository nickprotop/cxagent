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

    /// <summary>
    /// TEXT QUEUED AFTER THE OWNER LET GO IS STILL READ. The sender finds the turn owned, and before it
    /// queues, the owner releases and runs its whole tail — which finds nothing. The sender looks again
    /// after storing, so neither a report nor a steer waits for the user to type.
    /// </summary>
    [Theory]
    [InlineData(Session.SubmitSource.System)]
    [InlineData(Session.SubmitSource.User)]
    public async Task TextQueuedAfterTheOwnerLetGo_IsStillRead(Session.SubmitSource source)
    {
        var held = new HeldProvider();
        var s = Open(held);
        var first = Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go"));
        Assert.True(await Poll.Until(() => held.Calls == 1));

        var waiting = 0;
        s.AfterLosingTheTurnForTest = () =>
        {
            Volatile.Write(ref waiting, 1);
            first.Turn.Wait(TimeSpan.FromSeconds(5));
        };
        var late = Task.Run(() => s.Submit("the late one", source: source));
        Assert.True(await Poll.Until(() => Volatile.Read(ref waiting) == 1));

        held.Release();
        Assert.IsType<Session.SubmitOutcome.Queued>(await late);
        s.AfterLosingTheTurnForTest = null;
        await Idle(s);

        Assert.Equal(2, held.Calls);
        Assert.Contains("the late one", held.Text());
    }

    /// <summary>
    /// TEXT ACCEPTED IS NOT REPORTED AS A FAILURE. A sender that stored its steer and then started the lap
    /// for it itself — the owner had let go meanwhile — gets Queued even when a Drained subscriber throws:
    /// the text is in, and its lap runs. The subscriber's failure is said, not thrown at the sender.
    /// </summary>
    [Fact]
    public async Task AThrowingDrainedSubscriber_InTheSendersRecheck_DoesNotFailTheSend()
    {
        var held = new HeldProvider();
        var s = Open(held);
        var first = Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go"));
        Assert.True(await Poll.Until(() => held.Calls == 1));
        s.Drained += _ => throw new InvalidOperationException("a subscriber failed");

        var waiting = 0;
        s.AfterLosingTheTurnForTest = () =>
        {
            Volatile.Write(ref waiting, 1);
            first.Turn.Wait(TimeSpan.FromSeconds(5));
        };
        var late = Task.Run(() => s.Submit("the late one"));
        Assert.True(await Poll.Until(() => Volatile.Read(ref waiting) == 1));

        held.Release();
        var outcome = await late;
        s.AfterLosingTheTurnForTest = null;
        await Idle(s);

        Assert.IsType<Session.SubmitOutcome.Queued>(outcome);
        Assert.Equal(2, held.Calls);
        Assert.Contains("the late one", held.Text());
        Assert.Contains(_sink!.Errors, e => e.Contains("a subscriber failed"));
    }

    /// <summary>
    /// A SUBSCRIBER THAT THROWS DOES NOT LEAVE THE TURN OWNED. Taking a steer in the tail raises Drained;
    /// a front end's handler failing there must not wedge every later message into a loop that is gone.
    /// </summary>
    [Fact]
    public async Task AThrowingDrainedSubscriber_DoesNotLeaveTheTurnOwned()
    {
        var held = new HeldProvider();
        held.Release();
        var s = Open(held);
        s.Drained += _ => throw new InvalidOperationException("a subscriber failed");
        var once = 0;
        s.BeforeTurnReleasedForTest = () => { if (Interlocked.Exchange(ref once, 1) == 0) s.Submit("also this"); };

        var first = Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Turn);
        await Idle(s);

        Assert.False(s.TurnOwned);
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("next")).Turn;
    }

    /// <summary>
    /// A SUBSCRIBER THAT THROWS DOES NOT COST THE STEER. The steer has left the queue by the time Drained
    /// is raised, so a handler failing there must not also take the user's words with it: the lap they
    /// asked for still runs, and the failure still reaches whoever awaits the turn.
    /// </summary>
    [Fact]
    public async Task AThrowingDrainedSubscriber_InTheTail_StillReadsTheSteer()
    {
        var held = new HeldProvider();
        held.Release();
        var s = Open(held);
        s.Drained += _ => throw new InvalidOperationException("a subscriber failed");
        var once = 0;
        s.BeforeTurnReleasedForTest = () => { if (Interlocked.Exchange(ref once, 1) == 0) s.Submit("also this"); };

        var first = Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Turn);
        await Idle(s);

        Assert.Equal(2, held.Calls);
        Assert.Contains("also this", held.Text());
    }

    /// <summary>
    /// MAIL WAITING AT ESCAPE STARTS NOTHING: Esc means stop. It is read in front of the user's next
    /// prompt instead.
    /// </summary>
    [Fact]
    public async Task MailWaitingAtEscape_StartsNoRequest_AndIsReadBeforeTheNextPrompt()
    {
        var held = new HeldProvider();
        var s = Open(held);
        s.Submit("go");
        Assert.True(await Poll.Until(() => held.Calls == 1));
        Assert.IsType<Session.SubmitOutcome.Queued>(s.Submit("agent x finished", source: Session.SubmitSource.System));

        Assert.True(s.CancelTurn());
        await Idle(s);
        await Task.Delay(200);
        Assert.Equal(1, held.Calls);

        held.Release();
        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("what next?")).Turn;

        Assert.Equal(2, held.Calls);
        var contents = held.LastMessages!.Select(m => m.Content ?? "").ToList();
        var note = contents.FindIndex(c => c.Contains("agent x finished"));
        var prompt = contents.FindIndex(c => c.Contains("what next?"));
        Assert.True(note >= 0 && note < prompt);
    }

    /// <summary>
    /// A STEER TYPED AFTER THE STOP DOES START A LAP: CancelTurn handed the earlier queue back, so this is
    /// the user asking for the next thing.
    /// </summary>
    [Fact]
    public async Task ASteerTypedAfterEscape_StartsALap()
    {
        var held = new HeldProvider();
        var s = Open(held);
        var once = 0;
        s.BeforeTurnReleasedForTest = () => { if (Interlocked.Exchange(ref once, 1) == 0) s.Submit("after the stop"); };
        s.Submit("go");
        Assert.True(await Poll.Until(() => held.Calls == 1));

        Assert.True(s.CancelTurn());
        Assert.True(await Poll.Until(() => held.Calls == 2));
        held.Release();
        await Idle(s);

        Assert.Equal(2, held.Calls);
        Assert.Contains("after the stop", held.Text());
    }

    /// <summary>
    /// A COMMAND TYPED WHILE THE TURN IS OWNED BUT NOT BUSY is held exactly once and run by the tail — not
    /// run at once (Send would find the turn owned and hold it again, round and round) and not lost.
    /// </summary>
    [Fact]
    public async Task ACommandTypedWhileOwnedButNotBusy_IsHeldOnce_AndRunByTheTail()
    {
        var folder = Path.Combine(_dir, ".cxagent", "commands");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "commit.md"), "Commit it.");
        var held = new HeldProvider();
        held.Release();
        var s = Open(held);
        var heldTimes = 0;
        s.HeldChanged += line => { if (line is not null) Interlocked.Increment(ref heldTimes); };
        var once = 0;
        bool? busyThen = null, ownedThen = null;
        Session.SubmitOutcome? typed = null;
        s.BeforeTurnReleasedForTest = () =>
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            (busyThen, ownedThen) = (s.IsBusy, s.TurnOwned);
            typed = s.Submit("/commit");
        };

        await Assert.IsType<Session.SubmitOutcome.Started>(s.Submit("go")).Turn;
        await Idle(s);

        Assert.Equal(false, busyThen);
        Assert.Equal(true, ownedThen);
        Assert.IsType<Session.SubmitOutcome.Queued>(typed);
        Assert.Equal(1, heldTimes);
        Assert.Null(s.HeldCommand);
        Assert.Equal(2, held.Calls);
        Assert.Contains("Commit it.", held.Text());
    }
}
