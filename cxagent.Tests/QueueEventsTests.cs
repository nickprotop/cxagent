using CxAgent.Core.Sessions;
using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Storage;
using Xunit;

namespace CxAgent.Tests;

/// <summary>
/// The queue announces itself rather than being polled.
///
/// <para>WHAT THIS REPLACED. The UI read PendingSteer at three separate moments and each site had to
/// remember to keep the transcript block in sync — eleven touch points, and the bug the comments
/// record: "the block sitting in the transcript while SubmitComposer added the real user message
/// right below it — the same text twice".</para>
/// </summary>
public class QueueEventsTests
{
    private static Session Bare() => new(Path.GetTempPath());

    [Fact]
    public void Pending_CarriesTheWholeAndTheIncrement()
    {
        var session = Bare();
        var seen = new List<(string Whole, string Added)>();
        session.Pending += (whole, added) => seen.Add((whole, added));

        session.Steer("first");
        session.Steer("second");

        Assert.Equal(2, seen.Count);
        Assert.Equal(("first", "first"), seen[0]);

        // THE WHOLE IS THE JOIN, the increment is just this line — which is the pair only this
        // moment has, and the reason both are carried.
        Assert.Equal(("first\nsecond", "second"), seen[1]);
    }

    // PER APPEND, NOT COALESCED. The UI already redrew once per line; this reproduces that exactly.
    [Fact]
    public void Pending_FiresOncePerAppend()
    {
        var session = Bare();
        var count = 0;
        session.Pending += (_, _) => count++;

        session.Steer("a");
        session.Steer("b");
        session.Steer("c");

        Assert.Equal(3, count);
    }

    [Fact]
    public void Drained_FiresWhenTheTurnTakesIt_AndCarriesWhatWent()
    {
        var session = Bare();
        string? drained = null;
        session.Drained += text => drained = text;

        session.Steer("one");
        session.Steer("two");
        var taken = session.TakePendingSteer();

        Assert.Equal("one\ntwo", taken);
        Assert.Equal("one\ntwo", drained);
        Assert.Null(session.PendingSteer);
    }

    // SEPARATE FROM Drained even though both empty the queue: a subscriber does opposite things.
    [Fact]
    public void Cancelled_FiresOnCancelPending_AndNotDrained()
    {
        var session = Bare();
        string? cancelled = null;
        var drainedFired = false;
        session.Cancelled += text => cancelled = text;
        session.Drained += _ => drainedFired = true;

        session.Steer("typed but never sent");
        session.CancelPending();

        Assert.Equal("typed but never sent", cancelled);
        Assert.False(drainedFired);
        Assert.Null(session.PendingSteer);
    }

    // SILENT WHEN EMPTY. A subscriber restoring an empty string into a composer would clear whatever
    // the user had typed since.
    [Fact]
    public void CancelPending_OnAnEmptyQueue_SaysNothing()
    {
        var session = Bare();
        var fired = false;
        session.Cancelled += _ => fired = true;

        session.CancelPending();

        Assert.False(fired);
    }

    [Fact]
    public void TakePendingSteer_OnAnEmptyQueue_DoesNotRaiseDrained()
    {
        var session = Bare();
        var fired = false;
        session.Drained += _ => fired = true;

        Assert.Null(session.TakePendingSteer());
        Assert.False(fired);
    }

    /// <summary>
    /// THE EVENT IS RAISED OUTSIDE THE LOCK, which is the property that hides.
    ///
    /// <para>A SUBSCRIBER READING THE QUEUE BACK PROVES NOTHING, and that is worth recording because
    /// it was the first thing tried: C#'s <c>lock</c> is REENTRANT on the same thread, so a handler
    /// that calls straight back into the session acquires the monitor it already holds and returns.
    /// Injecting the bug — moving the raise inside the lock — left all eight tests here green.</para>
    ///
    /// <para>SO THE HANDLER BLOCKS ON ANOTHER THREAD that wants the same lock. That is the real shape:
    /// the UI raises from the render loop while a turn takes from the agent's flow, so a handler doing
    /// synchronous work against the other side is two threads and one monitor. Under the lock this
    /// never returns; outside it, both proceed.</para>
    ///
    /// <para>The timeout is the assertion. A hang takes the whole suite's shutdown with it rather
    /// than reporting a failure, which is how it reached the app last time.</para>
    /// </summary>
    [Fact]
    public void Pending_IsRaisedOutsideTheLock_SoAHandlerMayWaitOnAnotherThread()
    {
        var session = Bare();
        session.Steer("seed");

        var handlerRan = new ManualResetEventSlim(false);
        session.Pending += (_, _) =>
        {
            // ANOTHER THREAD touching the queue while this handler is on the stack. If Pending were
            // raised under _pendingGate, this Task can never acquire it and the Wait never returns.
            var other = Task.Run(() => session.TakePendingSteer());
            handlerRan.Set();
            Assert.True(other.Wait(TimeSpan.FromSeconds(5)), "the lock was held across the handler");
        };

        var done = Task.Run(() => session.Steer("x"));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "Steer deadlocked raising Pending under its own lock");
        Assert.True(handlerRan.IsSet);
    }

    [Fact]
    public void Drained_IsRaisedOutsideTheLock_Too()
    {
        var session = Bare();
        session.Steer("x");

        var handlerRan = new ManualResetEventSlim(false);
        session.Drained += _ =>
        {
            var other = Task.Run(() => session.Steer("from another thread"));
            handlerRan.Set();
            Assert.True(other.Wait(TimeSpan.FromSeconds(5)), "the lock was held across the handler");
        };

        var done = Task.Run(() => session.TakePendingSteer());

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "TakePendingSteer deadlocked raising Drained");
        Assert.True(handlerRan.IsSet);
    }
}

/// <summary>
/// Cancelling a turn is one call, and the session owns every step of it.
///
/// <para>WHAT THIS REPLACED. The turn's cancellation scope was a local in the composition root, so a
/// session could report that it was busy and had no way to stop; "Stopped." was written straight to
/// one front end's transcript rather than through the session's observer; and the queue was drained
/// by a UI helper that hard-coded where the text landed. Three statements, two of which were not
/// that layer's, and a second front end would have had to reproduce all three.</para>
/// </summary>
public class CancelTurnTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cancel-" + Guid.NewGuid().ToString("N"));

    public CancelTurnTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private Session Wired(out BufferedChatSink said, out SessionManager manager)
    {
        manager = SessionManager.Create(new AppPaths(_dir));
        said = new BufferedChatSink();
        return manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = said, ToolObserver = new BufferedJobPanel() }, AgentMode.Single);
    }

    // FALSE WHEN IDLE, and nothing said: a stop arriving just after a turn ended is an ordinary race,
    // and announcing it would report an event that did not happen.
    [Fact]
    public void CancelTurn_WhenIdle_IsFalse_AndSaysNothing()
    {
        var session = Wired(out var said, out var manager);
        using var mgr = manager;

        Assert.False(session.CancelTurn());
        Assert.DoesNotContain("Stopped", said.Transcript, StringComparison.Ordinal);
    }

    // AND IT DOES NOT EAT THE QUEUE. Nothing was cancelled, so text typed for the next turn stays.
    [Fact]
    public void CancelTurn_WhenIdle_LeavesTheQueueAlone()
    {
        var session = Wired(out _, out var manager);
        using var mgr = manager;

        session.Steer("still want this");
        Assert.False(session.CancelTurn());

        Assert.Equal("still want this", session.PendingSteer);
    }

    // THE CASCADE IS AGNOSTIC: the session hands the text back and does not know where it goes.
    [Fact]
    public void CancelPending_HandsTextBack_WithoutKnowingWhereItGoes()
    {
        var session = Wired(out _, out var manager);
        using var mgr = manager;

        string? landed = null;
        session.Cancelled += text => landed = text;   // a composer here; a log elsewhere

        session.Steer("typed mid-turn");
        session.CancelPending();

        Assert.Equal("typed mid-turn", landed);
        Assert.Null(session.PendingSteer);
    }
}

/// <summary>
/// A cancel that arrives while the turn is WAITING on somebody — a permission prompt, or a question
/// the model asked — must resolve that wait rather than wedge on it.
///
/// <para>WHY THIS IS CORE'S PROBLEM AND NOT THE UI'S. The front end routes Escape past a live prompt
/// deliberately: Escape there means "I am not answering that", and the comment on TryDenyPermission
/// records what it cost when it did not — "a denied test-file write ended a drive that had cost two
/// million tokens, and the frozen token counter read as a hang". But routing is a POLICY of one front
/// end. A second one, or a headless driver, can and will call CancelTurn with a prompt outstanding,
/// and Core must not depend on nobody doing that.</para>
/// </summary>
public class CancelWhileWaitingTests
{
    // THE GATE ALREADY FAILS CLOSED, and this pins it from the cancel side rather than the prompt
    // side: a cancelled request resolves as a refusal, so the tool call returns and the turn unwinds.
    // A gate that simply never completed would leave the turn holding an unanswered tool call — the
    // orphan that 400s a session on the next request.
    [Fact]
    public async Task ACancelWhileAPermissionPromptIsUp_ResolvesIt_RatherThanHanging()
    {
        using var cts = new CancellationTokenSource();
        var store = new CxAgent.Core.Permissions.PermissionRulesStore(
            new AppPaths(Directory.CreateTempSubdirectory("cw").FullName));

        var shown = new ManualResetEventSlim(false);
        var gate = CxAgent.Core.Permissions.PermissionDecider.WithPrompt(store, notice: null,
            (_, _, ct) =>
            {
                // Never answers. The only way out is the token.
                shown.Set();
                return Task.Run(() =>
                {
                    ct.WaitHandle.WaitOne();
                    return CxAgent.Core.Permissions.PermissionChoice.Deny;
                }, CancellationToken.None);
            });

        // A POLICY, because a request without one is refused before any prompt goes up — the gate
        // holds no session and cannot judge for one. That guard is right; it just means this test has
        // to stamp what PermissionGatedPlugin stamps in production.
        var root = Directory.CreateTempSubdirectory("cw-root").FullName;
        var request = new CxAgent.Core.Permissions.PermissionRequest(
            CxAgent.Core.Permissions.PermissionKind.Shell, "sleep 999", null)
        {
            Policy = new CxAgent.Core.Permissions.PermissionPolicy(root, store, EditMode.AlwaysAsk),
        };

        var pending = gate.RequestAsync(request, cts.Token);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the prompt never went up");

        cts.Cancel();

        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, completed);   // it resolved rather than wedging
        Assert.False((await pending).Allowed);       // and resolved as a refusal, never a silent allow
    }
}

/// <summary>
/// Cancelling a turn hands back what was queued — the half a live tmux drive cannot pin reliably,
/// because whether text is still queued at the moment Escape lands depends on how fast the model
/// reaches its next tool barrier.
/// </summary>
public class CancelRestoresQueuedTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "restore-" + Guid.NewGuid().ToString("N"));

    public CancelRestoresQueuedTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    // WHAT WAS QUEUED COMES BACK, joined, in the order it was typed — and the session does not know
    // where it goes. A composer here; a log elsewhere.
    [Fact]
    public void CancelPending_ReturnsEverythingQueued_InOrder()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        string? handedBack = null;
        session.Cancelled += text => handedBack = text;

        session.Steer("alpha");
        session.Steer("beta");
        session.CancelPending();

        Assert.Equal("alpha\nbeta", handedBack);
        Assert.Null(session.PendingSteer);
    }

    // AND WHEN THE BARRIER TOOK IT FIRST, there is nothing to hand back — which is what a live drive
    // keeps showing: the model reaches a tool call, the steer is delivered as a real user turn, and a
    // later Escape correctly restores nothing.
    [Fact]
    public void WhenTheTurnAlreadyTookIt_CancelHandsBackNothing()
    {
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(new MockLlmProvider("m")),
            new SessionPorts { Observer = new BufferedChatSink(), ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var handedBack = 0;
        session.Cancelled += _ => handedBack++;

        session.Steer("taken at the barrier");
        Assert.Equal("taken at the barrier", session.TakePendingSteer());

        session.CancelPending();

        Assert.Equal(0, handedBack);
    }
}


/// <summary>
/// <c>/init</c> shows what the user typed, not the briefing it sends.
///
/// <para>The prompt and the echo were two locals in the composition root — the briefing in goalText,
/// the word "/init" in a turnEcho set in one switch branch and read a hundred lines later. Splitting
/// them is how a briefing ends up on the transcript as the user's own words, "on every later read of
/// the log".</para>
/// </summary>
public class InitEchoTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "init-" + Guid.NewGuid().ToString("N"));

    public InitEchoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private sealed class UserTurnRecorder : ISessionObserver
    {
        public List<string> Shown { get; } = [];
        public void UserTurnAdded(ChatMessageId id, string text) { lock (Shown) Shown.Add(text); }
        public void Said(Message message) { }
        public void AssistantTurnBegan(ChatMessageId id) { }
        public void AssistantTextAppended(ChatMessageId id, string text) { }
        public void AssistantReasoningAppended(ChatMessageId id, string text) { }
        public void AssistantTurnEnded(ChatMessageId id) { }
        public void AssistantLabelled(ChatMessageId id, string label) { }
    }

    [Fact]
    public async Task Initialise_ShowsTheCommand_AndSendsTheBriefing()
    {
        var shown = new UserTurnRecorder();
        var provider = new MockLlmProvider("m");
        using var manager = SessionManager.Create(new AppPaths(_dir));
        var session = manager.Open(_dir, ResolvedConfig.ForTesting(provider),
            new SessionPorts { Observer = shown, ToolObserver = new BufferedJobPanel() },
            AgentMode.Single);

        var started = Assert.IsType<Session.SubmitOutcome.Started>(session.Initialise());
        await started.Turn;

        // WHAT THE USER SEES is the command they typed.
        Assert.Equal(["/init"], shown.Shown);
    }
}
