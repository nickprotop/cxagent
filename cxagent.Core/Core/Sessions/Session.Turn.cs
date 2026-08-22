using CxAgent.Core.Commands;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Sessions;

/// <summary>
/// Running a turn: whether one may start, starting it, stopping it, and what happens when it ends.
///
/// <para>ONE TURN AT A TIME, enforced here because nothing else can: two concurrent sends on one
/// agent append to ONE live Context.Messages from two loops, which corrupts the conversation rather
/// than throwing. A caller holding its own flag would have to rediscover that, and getting it wrong
/// is silent.</para>
///
/// <para>THE SESSION OWNS THE WHOLE LIFECYCLE — the busy flag, the cancellation scope, the turn ids
/// and the transcript's own rows — so a host is the thing that owns an agent rather than a second
/// way to run one.</para>
/// </summary>
public sealed partial class Session
{
    /// <summary>
    /// True while a turn is running — the session's own answer, not a flag a front end keeps.
    ///
    /// <para>Whether an action can happen NOW is a fact about this session, so the session is what
    /// knows it. A caller that wants to grey out a menu can read this; a caller that wants to try
    /// anyway gets refused with a reason, which is what the mutating methods below do.</para>
    /// </summary>
    /// <summary>
    /// True while a turn is running on this session.
    ///
    /// <para>OWNED HERE, NOT ASKED OF THE HOST. It was the host's flag and the host's cancellation
    /// scope, with the session forwarding both — which made AgentHost a second public entry point for
    /// starting and stopping a turn. That is tolerable inside one binary and not tolerable in a
    /// library: an app consuming this package would find two ways to run a turn, one of them able to
    /// corrupt the conversation.</para>
    ///
    /// <para>Volatile: written by whichever thread ran the turn, read by a UI thread deciding whether
    /// to accept a command.</para>
    /// </summary>
    public bool IsBusy => Volatile.Read(ref _busy);

    private bool _busy;

    /// <summary>
    /// Mints turn ids for this session's transcript — and now genuinely from the session, which is
    /// what <see cref="ChatMessageId"/> has claimed all along ("MINTED BY THE SESSION, not by
    /// whatever is observing it").
    ///
    /// </summary>
    private long _nextTurnId;

    private ChatMessageId NextTurnId() => new(Interlocked.Increment(ref _nextTurnId));

    /// <summary>The running turn's cancellation scope, or null between turns.</summary>
    private CancellationTokenSource? _turn;
    private readonly object _turnGate = new();

    /// <summary>
    /// Releases the last turn's cancellation scope. Called by <c>SessionManager.Close</c>.
    ///
    /// <para>EVERY OTHER SCOPE IS DISPOSED AS THE NEXT TURN REPLACES IT; the final one has no
    /// successor, so without this it lives as long as the process. Safe at close and not in the
    /// turn's own finally: by the time a session is closed no turn is running, which is the condition
    /// that makes disposing a cancellation source free of the callback race.</para>
    /// </summary>
    internal void DisposeTurnScope()
    {
        CancellationTokenSource? turn;
        lock (_turnGate) { turn = _turn; _turn = null; }
        turn?.Dispose();
    }

    /// <summary>
    /// Refuses an action that cannot run mid-turn, and says why.
    ///
    /// <para>ONE COPY OF THE SENTENCE, and it belongs here rather than in a front end: five call
    /// sites in the composition root each carried their own, which is five chances for the wording
    /// to drift — and a second front end would have written a sixth. The reason is the same every
    /// time: re-wiring or restoring replaces the agent the running turn is appending to, and its
    /// tool results would land in a conversation nobody is reading, which is the orphan shape that
    /// 400s a session permanently.</para>
    /// </summary>
    /// <summary>
    /// What a caller asked to send, and what the session did with it.
    ///
    /// <para>THE THREE OUTCOMES ARE DIFFERENT ACTIONS FOR THE CALLER, which is why this is not a
    /// bool: a started turn needs a spinner and something to await, a queued one needs nothing (the
    /// <see cref="Pending"/> event already drew the block), and no-agent must leave the text in the
    /// composer rather than clearing it.</para>
    ///
    /// <para>NOT <c>SendOutcome</c>, which already exists in this namespace and answers a different
    /// question — how a turn ENDED (completed, capped, silent). Two types with one name meaning
    /// different things is a trap even when the compiler allows it.</para>
    ///
    /// <para>A RECORD SO <see cref="Started"/> CAN CARRY THE TURN. An enum could only say that one
    /// began; a caller then had to go and start it, which is how the prompt came to be held by four
    /// layers at once.</para>
    /// </summary>
    public abstract record SubmitOutcome
    {
        private SubmitOutcome() { }

        /// <summary>No host — nothing was sent and nothing was kept.</summary>
        public sealed record NoAgent : SubmitOutcome;

        /// <summary>A turn was already running, so this was queued for its next tool barrier.</summary>
        /// <param name="ToolsIgnored">
        /// True when this call passed a selection DIFFERENT from the one the running turn began
        /// with. Queued text joins that turn, whose tools were fixed when it started, so a different
        /// selection cannot take effect.
        ///
        /// <para>NOT "a selection was passed". A caller that passes the same one every time — the
        /// normal shape for a front end holding a selection for the session — has had nothing
        /// ignored, and telling it otherwise on every mid-turn correction is noise.</para>
        /// </param>
        public sealed record Queued(bool ToolsIgnored = false) : SubmitOutcome;

        /// <summary>
        /// A turn began. <paramref name="Turn"/> completes when it ends, however it ends.
        ///
        /// <para>HANDED BACK RATHER THAN AWAITED HERE, because callers need it differently: a front
        /// end attaches a continuation for its spinner and its falling edge, a headless driver
        /// awaits it, and a fire-and-forget caller ignores it. Awaiting inside Send would make the
        /// first of those impossible.</para>
        /// </summary>
        public sealed record Started(Task Turn) : SubmitOutcome;
    }

    /// <summary>
    /// The selection the RUNNING turn was started with, as the caller passed it.
    ///
    /// <para>Kept only to answer one question: did a queued Submit pass something different? Stored
    /// AS PASSED rather than composed, because a composed value carries S1 and S2 terms the caller
    /// never wrote and would compare unequal to an identical re-pass.</para>
    /// </summary>
    private Plugins.ToolSelection? _turnTools;

    /// <summary>
    /// Sends this text, or queues it when a turn is already running.
    ///
    /// <para>ONE TURN AT A TIME, and this is what enforces it: two concurrent sends on one agent
    /// append to ONE live Context.Messages from two loops, which corrupts the conversation rather
    /// than throwing.</para>
    ///
    /// <para>QUEUED, NOT REFUSED. A correction typed mid-turn is the normal case, not an error: it is
    /// delivered at the turn's next tool barrier, where the model can still act on it. Refusing would
    /// throw away what someone deliberately typed.</para>
    ///
    /// <para>SUBMIT, NOT <c>SendAsync</c>, because this is SYNCHRONOUS: it returns a receipt, not an
    /// awaitable, and only <c>Started</c> carries a Task. <c>await …SendAsync()</c> would read as
    /// "wait for the turn" while actually waiting for a decision — and on the Queued path there is no
    /// turn to wait for. Submitting is deciding; <c>Started.Turn</c> is the running, and callers want
    /// different halves: a front end attaches a continuation, a headless driver awaits.</para>
    /// </summary>
    /// <param name="text">What to send to the model.</param>
    /// <param name="echo">
    /// What to show instead, when they differ. <c>/init</c> sends several paragraphs of briefing and
    /// displays <c>/init</c> — putting the briefing on the transcript as the user's own message
    /// attributes words to them they never wrote, on every later read of the log.
    /// </param>
    /// <param name="tools">
    /// Which tools this ONE request may use, composed onto the session's selection. Null keeps
    /// whatever the session already has. See <see cref="Plugins.ToolSelection"/>.
    /// </param>
    public SubmitOutcome Submit(string text, string? echo = null,
        Plugins.ToolSelection? tools = null)
    {
        if (Host is null) return new SubmitOutcome.NoAgent();

        // ISBUSY, NOT A CALLER'S FLAG. The host writes it as the turn begins and clears it however
        // the turn ends, including cancellation — so it cannot latch true the way a flag maintained
        // beside the turn can.
        if (IsBusy)
        {
            // Steer raises Pending, so a watcher draws its own queued block. Nothing is said here:
            // the block IS the report, and a line saying "queued" beside it would say it twice.
            Steer(text);

            // A QUEUED MESSAGE IS NOT A SECOND REQUEST. It joins the running turn — taken at its
            // next tool barrier, or drained by the loop below into a later lap of the SAME
            // RunTurnAsync — so it runs under the selection that turn started with. There is
            // nothing to apply.
            //
            // BUT NOT SILENTLY, AND NOT BLINDLY. A caller that passed a DIFFERENT selection had it
            // dropped, and that is worth saying; a caller that passed the same one — the normal
            // shape for a front end holding one for the session — had nothing ignored, and
            // flagging it every time would be noise that trains people past the flag.
            var ignored = tools is not null && !Equals(tools, _turnTools);
            if (ignored)
                Say(new Message("tool selection not applied — a turn is already running, and this "
                    + "joins it.", Severity.Warning));

            return new SubmitOutcome.Queued(ignored);
        }

        // REMEMBERED AS PASSED, not composed: the comparison above asks whether a later caller sent
        // the same thing, and a composed value carries S1 and S2 terms the caller never wrote.
        _turnTools = tools;
        return new SubmitOutcome.Started(RunTurnAsync(text, echo, tools));
    }

    /// <summary>
    /// Runs one turn to completion and reports whatever went wrong through this session's observer.
    ///
    /// <para>THE ERROR PATH IS THE POINT. It was handled twice, differently: AgentHost caught and
    /// called <c>_sink.Failed</c>, while the front end's wrapper caught and wrote to its own
    /// transcript. Two channels for one fact, and a second front end would have had to reproduce the
    /// second one — the same split "Stopped." had before <see cref="CancelTurn"/>.</para>
    ///
    /// <para>CANCELLATION IS NOT AN ERROR HERE. <see cref="CancelTurn"/> already said "Stopped." and
    /// handed the queue back; saying anything further would report one event twice.</para>
    /// </summary>
    private async Task RunTurnAsync(string text, string? echo, Plugins.ToolSelection? tools)
    {
        // A LOOP, NOT RECURSION. Text left over after a turn starts another one, and a caller queuing
        // faster than the model answers would grow the stack with a recursive call. This is also why
        // the drain stays inside this method rather than re-entering through a caller.
        //
        // A FALLBACK, NOT THE MAIN PATH. A correction typed mid-turn is normally taken by the turn
        // ITSELF at its next tool barrier (Agent.cs), so by the time a lap finishes there is usually
        // nothing here. What reaches this is text typed after the LAST barrier — during the final
        // provider call, or during a turn that never called a tool — which no barrier will reach.
        while (true)
        {
            // A VERDICT DECIDES ONE ACTION, NOT A TURN, NOT A SESSION. PermissionDecider caches the
            // auto-mode classifier's verdicts (Task 10) and needs to forget them exactly here, at the
            // top of a lap, or a cached ALLOW would silently outlive the turn it was computed for and
            // apply to a later turn with different goal/instructions but a coincidentally identical
            // action. `is PermissionDecider` rather than adding ResetTurnState to IPermissionGate:
            // this is state specific to the one real, stateful gate implementation, not a concept
            // every gate has an opinion on — DenyAll/AllowAll/every test fake (Task 7 counted ~10 of
            // them) would need a no-op override for a method that means nothing to them. A type test
            // at the one real call site is honest about that; widening the interface is not required
            // to make the reset happen and would be churn with no caller ever exercising it on a fake.
            if (Services?.Gate is Permissions.PermissionDecider decider) decider.ResetTurnState();

            // THE TURN'S SCOPE, created here because the turn is this method's. It was the host's,
            // so the host is not a second way to start one.
            var scope = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (_turnGate) { previous = _turn; _turn = scope; }
            previous?.Dispose();

            Volatile.Write(ref _busy, true);
            try
            {
                // THE TRANSCRIPT'S OWN TURNS, announced before the model is called so a watcher has a
                // row to stream into. echo is what the USER sees: /init sends paragraphs of briefing
                // and displays "/init", because putting the briefing on the transcript as their own
                // message attributes words to them they never wrote.
                _sink?.UserTurnAdded(NextTurnId(), echo ?? text);

                var assistantId = NextTurnId();
                _sink?.AssistantTurnBegan(assistantId);
                _sink?.AssistantTurnEnded(assistantId);   // the agent opens its own turns

                await Host!.RunAsync(text, scope.Token, tools);
            }
            catch (OperationCanceledException)
            {
                // CANCELLATION ENDS THE DRAIN, and nothing is said: CancelTurn already said
                // "Stopped." and handed the queue back through CancelPending. Continuing the loop
                // would send text the user just took back.
                return;
            }
            catch (Exception ex)
            {
                // A BACKSTOP, NOT THE REPORTING PATH — verified by silencing it, which changed
                // nothing: AgentHost catches a failing turn and calls _sink.Failed, so an ordinary
                // provider failure never reaches here. What does reach here is a host that rethrows,
                // which today is only cancellation (handled above) but is exactly the kind of thing a
                // later change adds without noticing.
                //
                // IT MUST STILL SAY SOMETHING. This loop is fire-and-forget from the caller's side —
                // the Task is awaited for its falling edge, not its result — so an exception escaping
                // silently would leave a session that stopped working with nothing on screen.
                Say(new Message(ex.Message, Severity.Error));
                return;
            }

            finally
            {
                // RELEASED HOWEVER THE LAP ENDS, including the cancellation that returns above: a turn
                // that died leaving this set would refuse every later prompt and look hung.
                Volatile.Write(ref _busy, false);

                // THE SCOPE STAYS UNTIL THE NEXT LAP REPLACES IT, deliberately not disposed here. A
                // cancellation callback can still be running on another thread as this unwinds, and
                // disposing under it throws from a place nobody is catching. Clearing the field is
                // enough: CancelTurn reads null and answers false.
                lock (_turnGate) { if (ReferenceEquals(_turn, scope)) _turn = null; }
            }

            // WHOLE OR NOT AT ALL, and never an echo: what goes in on a later lap is exactly what the
            // user typed, so it is displayed as itself.
            if (TakePendingSteer() is not { Length: > 0 } queued) return;

            text = queued;
            echo = null;
        }
    }

    /// <summary>
    /// Stops the running turn and hands back anything queued behind it. True when a turn was stopped.
    ///
    /// <para>THE WHOLE CASCADE IN ONE CALL, because the three steps only make sense together and were
    /// three steps that only make sense together: cancel the turn, say so, and return what was typed
    /// while it ran. Doing two of the three leaves either a silent stop or text eaten by a run the
    /// user chose to abandon.</para>
    ///
    /// <para>ANYTHING QUEUED GOES BACK, not to the bin. That text was never sent, so cancelling must
    /// not eat it — and where it goes is not this method's business: <see cref="CancelPending"/>
    /// raises <see cref="Cancelled"/> and a subscriber decides. The UI puts it in the composer above
    /// whatever has been typed since; a log writer would record it.</para>
    ///
    /// <para>SAID THROUGH THE SESSION'S OWN OBSERVER. The root wrote "Stopped." straight to its
    /// transcript, which meant a headless embedder driving a session was never told a turn had
    /// stopped while every other state change — mode, model, resume — came through Say. Two channels
    /// for one kind of fact.</para>
    ///
    /// <para>FALSE WHEN IDLE, and NOTHING is said or returned then: a stop arriving just after a turn
    /// ended is an ordinary race, and announcing it would report an event that did not happen.</para>
    /// </summary>
    public bool CancelTurn()
    {
        CancellationTokenSource? turn;
        lock (_turnGate) turn = _turn;

        // NOT UNDER THE LOCK. Cancel runs registered callbacks synchronously on this thread, and a
        // callback reaching back into the session would deadlock against a turn trying to finish.
        if (turn is null || turn.IsCancellationRequested) return false;

        turn.Cancel();

        Announce(SessionChangeKind.TurnCancelled);
        Say("Stopped.");

        // AFTER the announcement, so a subscriber restoring the text into a composer is drawing over
        // a surface that has already reacted to the stop — announce before you say, and before you
        // hand anything back.
        CancelPending();
        return true;
    }
}
