using CxAgent.Core.Agents;
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
    /// 1 while a RunTurnAsync owns this conversation — from the moment Send decides to start one until that
    /// loop has finished its last lap.
    ///
    /// <para>NOT <see cref="IsBusy"/>, WHICH DROPS BETWEEN LAPS. _busy is cleared at the end of every lap and
    /// set again at the top of the next, so a delivery deciding by it in that gap would read "idle" and
    /// start a second RunTurnAsync — two loops appending to one Context.Messages, which corrupts the
    /// conversation rather than throwing. Background agents deliver routinely, so the gap is hit in
    /// ordinary use, not only under contrived timing.</para>
    ///
    /// <para>TAKEN BY COMPARE-AND-SWAP so two senders racing at an idle session cannot both win.</para>
    /// </summary>
    private int _turnOwned;

    /// <summary>
    /// Leaves text for the session agent's next answer without starting a turn; false when there is no
    /// agent or its mailbox is full. See IAgentDelivery.Leave.
    /// </summary>
    public bool LeaveNote(string text) => Host?.TryLeave(text) ?? false;

    /// <summary>Draws one System line in this session's transcript.</summary>
    public void Notify(string line) => Say(new Message(line, Severity.Info));

    /// <summary>Whether a turn loop owns this conversation.</summary>
    public bool TurnOwned => Volatile.Read(ref _turnOwned) == 1;

    /// <summary>Runs just before a finished turn gives up ownership. Tests use it to land a delivery in the
    /// tail window; the app never sets it.</summary>
    internal Action? BeforeTurnReleasedForTest { get; set; }

    /// <summary>Runs at the top of every lap, before the lap's own error handling. Tests use it to fault the
    /// loop's bookkeeping; the app never sets it.</summary>
    internal Action? AtLapStartForTest { get; set; }

    /// <summary>Runs in Send just after it finds the turn owned, before the text is queued. Tests use it to
    /// hold a sender there while the owner releases and finishes its tail; the app never sets it.</summary>
    internal Action? AfterLosingTheTurnForTest { get; set; }

    /// <summary>Runs after a lap has stopped being busy, just before the loop looks for a steer. Tests use it
    /// to land a submit in that window, where the session reads idle but the turn is still owned; the app
    /// never sets it.</summary>
    internal Action? AfterLapForTest { get; set; }

    /// <summary>Who asked for the turn in flight, or null when none is running.</summary>
    /// <remarks>
    /// WRITTEN ON THE TURN'S OWN THREAD beside <c>_busy</c>, and read by unwire from another. Volatile
    /// for the same reason <c>_busy</c> is: a stale read here decides whether someone's work is
    /// cancelled.
    /// </remarks>
    public TurnOriginator? CurrentOriginator => Volatile.Read(ref _originator);

    private TurnOriginator? _originator;

    /// <summary>Marks this session busy until the returned scope is disposed, so a test can exercise
    /// the mid-turn paths without driving a model. NOT a general-purpose control: the turn loop owns
    /// this flag, and anything but a test taking it would be racing the loop for it.</summary>
    internal IDisposable PretendBusyForTesting()
    {
        // OWNED AS WELL AS BUSY: Send decides by ownership, so a session that only looked busy would
        // start a real turn under a test that meant to exercise the queueing paths.
        Volatile.Write(ref _turnOwned, 1);
        Volatile.Write(ref _busy, true);
        return new BusyScope(this);
    }

    private sealed class BusyScope(Session session) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref session._busy, false);
            Volatile.Write(ref session._turnOwned, 0);
        }
    }

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
    /// Whether a submission is the human speaking, which is the one thing that decides what happens
    /// to it when a turn is already running.
    ///
    /// <para>SEPARATE FROM <see cref="TurnOriginator"/>, WHICH CANNOT ANSWER THIS. An originator is a
    /// CANCELLATION LICENCE — <c>UnwirePluginAsync</c> severs a turn when
    /// <c>CurrentOriginator.IsFrom(pluginName)</c> — so <c>SessionAgentDelivery</c> deliberately
    /// stamps <c>User</c> on plugin-caused deliveries to keep an unwire from destroying work the
    /// plugin does not own. A field that means "do not sever this" therefore cannot also mean "this
    /// is the human", and making one field carry both would tie a message's routing to whether
    /// someone may cancel it.</para>
    ///
    /// <para>THE ROUTING IT DECIDES: a busy session steers what the user typed into the running turn,
    /// where it is announced as their turn and drawn as their queued block; anything else goes to the
    /// agent's mailbox for the next lap, announced as nobody's. Both reach the model promptly —
    /// neither waits for the user to speak again — and only the attribution differs.</para>
    /// </summary>
    public enum SubmitSource
    {
        /// <summary>Somebody typed this. The default, because almost every caller is a front end.</summary>
        User,

        /// <summary>
        /// The application has something to say: a command that finished, a job's result, a plugin's
        /// late answer. Nobody typed it, so nothing may present it as though they had.
        /// </summary>
        System,
    }

    /// <summary>
    /// The user's own text has just been sent to the model — a new turn, or a steer into a running one.
    ///
    /// <para>NOT <c>UserTurnAdded</c>. That announces a transcript row: it fires for a plugin's goal,
    /// and for a steer only when the agent TAKES it at a tool barrier — minutes after it was sent, or
    /// never, when the turn is cancelled first. What a listener here wants to know is that the person
    /// spoke, at the moment they did. A delivery, a plugin's goal and a slash command are not that.</para>
    ///
    /// <para>Raised on the submitting thread.</para>
    /// </summary>
    public event Action? UserSpoke;

    /// <summary>
    /// Whether a submission is the user's own words to the model: typed, not delivered, and not a
    /// plugin's goal. <see cref="UserSpoke"/> and the injected-text hand-off ask the same question,
    /// and answering it in one place keeps them from drifting apart.
    /// </summary>
    private static bool IsTheUser(SubmitSource source, TurnOriginator? origin) =>
        source is SubmitSource.User && (origin is null || origin == TurnOriginator.User);

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
    /// <para>EACH OUTCOME IS A DIFFERENT ACTION FOR THE CALLER, which is why this is not a bool: a
    /// started turn needs a spinner and something to await, a queued one needs nothing (the
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

        /// <summary>
        /// There was somewhere to put this and it would not fit — nothing was kept.
        ///
        /// <para>DISTINCT FROM <see cref="NoAgent"/> BECAUSE THE REMEDY DIFFERS. A session with no
        /// model will refuse the next message the same way; a refusal is about this moment, and the
        /// same text sent again once the agent has read what is waiting will land.</para>
        ///
        /// <para>REACHABLE ONLY FROM <see cref="SubmitSource.System"/> ON A BUSY SESSION, which is
        /// the one path with a bounded destination: a steer appends to a single string and a wake
        /// starts a turn, and neither can be full.</para>
        /// </summary>
        /// <param name="Reason">What to tell whoever sent it, in words a model or a user can read.</param>
        public sealed record Refused(string Reason) : SubmitOutcome;

        /// <summary>
        /// A turn was already running, so this joins it rather than starting a second one — at its
        /// next tool barrier when the user typed it, at the top of its next lap otherwise. Either way
        /// the model reads it during the running turn; see <see cref="SubmitSource"/> for why the two
        /// arrive by different doors.
        /// </summary>
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
        /// <param name="Turn">Completes when the turn does.</param>
        /// <param name="Result">
        /// The turn's final assistant text, or null when it produced none.
        ///
        /// <para>SEPARATE FROM <paramref name="Turn"/> BECAUSE MOST CALLERS DO NOT WANT IT. An input
        /// loop waits on the turn and renders through the observer; only a caller that asked for the
        /// answer — a plugin submitting with wantResult — awaits this. Keeping them apart means the
        /// common path allocates nothing extra.</para>
        /// </param>
        /// <param name="Outcome">
        /// How the turn ended — answered, capped at the turn limit, failed, cancelled.
        ///
        /// <para>A HEADLESS CALLER TURNS THIS INTO AN EXIT CODE, and the text alone cannot say it:
        /// a reply may be the answer or a summary of work the turn limit cut short, and a failed turn
        /// and a silent one both leave <paramref name="Result"/> null.</para>
        /// </param>
        public sealed record Started(Task Turn, Task<string?> Result, Task<SendOutcome> Outcome) : SubmitOutcome;

        /// <summary>
        /// The text named a command, which has run. Nothing was sent to the model.
        ///
        /// <para>A CONSUMER THAT IGNORES THIS CASE STILL BEHAVES CORRECTLY: the command ran and
        /// said its own result through the observer, and no turn started, so an input loop that
        /// only looks for <see cref="Started"/> simply moves on to the next prompt. That is the
        /// point — the cost of not knowing this exists is nothing.</para>
        /// </summary>
        /// <param name="Status">What the command reported. <c>Unknown</c> when nothing serviced it.</param>
        public sealed record Handled(CommandStatus Status) : SubmitOutcome;

        /// <summary>
        /// The text went to the side agent, not the main one. <c>Answer</c> is null when the aside
        /// was still answering and refused it.
        /// </summary>
        public sealed record Aside(Task? Answer) : SubmitOutcome;
    }

    /// <summary>
    /// The selection the RUNNING turn was started with, as the caller passed it.
    ///
    /// <para>Kept only to answer one question: did a queued Submit pass something different? Stored
    /// AS PASSED rather than composed, because a composed value carries S1 and S2 terms the caller
    /// never wrote and would compare unequal to an identical re-pass.</para>
    /// </summary>
    private Jobs.ToolSelection? _turnTools;

    /// <summary>
    /// Runs this text as a command if it names one, and otherwise sends it to the model.
    ///
    /// <para>ONE ENTRY POINT. A front end that only calls this gets every command the process
    /// registered, with no dispatch wiring of its own — and one that has never heard of commands
    /// gets them anyway, which is the failure mode worth designing for: the alternative silently
    /// sends "/trust" to a model, which answers something plausible and teaches nobody.</para>
    ///
    /// <para>BEFORE THE BUSY CHECK, deliberately. A command is not a prompt: /trust typed during
    /// a running turn should classify a folder, not join the turn as text. Commands that cannot
    /// run mid-turn refuse for their own reasons and say why, which is more accurate than a
    /// queue.</para>
    ///
    /// <para>NO MANAGER, NO DISPATCH. A session built outside SessionManager.Open has no registry
    /// to consult and behaves exactly as <see cref="SubmitRaw"/>.</para>
    /// </summary>
    public SubmitOutcome Submit(string text, string? echo = null, Jobs.ToolSelection? tools = null,
        TurnOriginator? origin = null, SubmitSource source = SubmitSource.User)
    {
        // BTW MODE TAKES WHAT THE USER TYPES, ALL OF IT, and before commands: the mode is a
        // conversation with the aside and nothing else, so "/clear what does this do?" is a question
        // about /clear, not an instruction to clear. Esc leaves the mode, and commands work again.
        //
        // ONLY THE USER'S. A delivery or a plugin's goal arriving meanwhile is the main conversation's
        // business, and sending it to the aside would lose it.
        if (InAside && IsTheUser(source, origin)) return SendAside(text);

        return Route(new TurnRequest(text, echo, tools, origin, source));
    }

    /// <summary>
    /// <see cref="Submit"/> past btw mode: a command, a command file or skill, or the model.
    ///
    /// <para>ITS OWN METHOD so a held command takes exactly this path and no copy of it — the same
    /// lookups in the same order, the same "unknown" sentence — while skipping the btw check, which
    /// is about what is typed in btw mode and a held command was typed before it.</para>
    /// </summary>
    private SubmitOutcome Route(TurnRequest request)
    {
        var (text, echo, tools, origin, source) = (request.Text, request.Echo, request.Tools,
            request.Origin, request.Source);

        if (Manager is { } manager && text.TrimStart().StartsWith('/'))
        {
            switch (manager.Commands.Run(this, text))
            {
                case CommandRegistry.Dispatch.Ran:
                    return new SubmitOutcome.Handled(CommandStatus.Reported);

                case CommandRegistry.Dispatch.NoHandler:
                    // NoHandler ONLY FIRES FOR A TABLE COMMAND (Run's own fallback), so the table
                    // match — not the registry's, which by definition has nothing for this name —
                    // is what names it here.
                    Say(new Message($"{SessionCommands.Match(text)?.Name} is not "
                        + "available in this application.", Severity.Warning));
                    return new SubmitOutcome.Handled(CommandStatus.Unknown);
            }

            // AN UNRECOGNISED SLASH IS STILL A COMMAND ATTEMPT, never a goal: sending "/celar" to
            // the model as a task is worse than saying it does not exist.
            //
            // THIS SESSION'S REGISTRY, NOT CORE'S TABLE. A command only a front end registered —
            // /exit — is absent from SessionCommands.All, so matching against the table here would
            // call a registered command "unknown" right after Run above dispatched it correctly.
            if (SessionCommands.Match(text, manager.Commands) is null)
            {
                // A COMMAND FILE OR SKILL, AFTER THE REGISTRY HAD NOTHING — so a built-in always
                // wins — and ONLY FOR THE USER'S OWN TYPING: a delivery or a plugin's goal that
                // happens to start with /deploy must never run a file's shell lines.
                if (IsTheUser(source, origin) && FindTyped(text) is { } entry)
                    return RunCommand(new TypedCommand(text, entry));

                Say(new Message($"Unknown command '{text.TrimStart().Split(' ')[0]}'. Available: "
                    + string.Join(", ", manager.Commands.All.Select(c => c.Name)) + ".",
                    Severity.Warning));
                return new SubmitOutcome.Handled(CommandStatus.Unknown);
            }
        }

        // WHAT THE APP HAS TO SAY GOES WITH THE USER'S NEXT MESSAGE, and only once the text is known to
        // be heading for the model. Taking it above the command branch would destroy it: prepending
        // turns "/clear" into prose that no longer starts with a slash, so the command silently becomes
        // a message AND the transcript goes with it into a turn the user never asked for. A command
        // therefore leaves it queued, which is right rather than merely safe — /clear and /stats are
        // not the user speaking to the model.
        //
        // AND ONLY FOR THE USER'S OWN MESSAGE. Injected text was queued to go with it; a delivery
        // waking the session, or a plugin's goal, is not that message, and taking it there sends the
        // queued text early, attached to words the user did not write, hidden behind their echo.
        //
        // WHERE IT IS ATTACHED depends on which arm Send takes — see Send.
        return Send(request, IsTheUser(source, origin));
    }

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
    /// whatever the session already has. See <see cref="Jobs.ToolSelection"/>.
    /// </param>
    /// <param name="origin">
    /// Who asked for this turn. Null means <see cref="TurnOriginator.User"/> — the ordinary case for
    /// every caller but a plugin driving its own session, which passes its own name so unwire can
    /// tell its turn from the user's.
    /// </param>
    /// <param name="source">
    /// Whether the human typed this. Only a busy session reads it, and only to choose between
    /// steering into the running turn and the agent's mailbox — see <see cref="SubmitSource"/>.
    /// </param>
    public SubmitOutcome SubmitRaw(string text, string? echo = null,
        Jobs.ToolSelection? tools = null, TurnOriginator? origin = null,
        SubmitSource source = SubmitSource.User) =>
        Send(new TurnRequest(text, echo, tools, origin, source), attachInjected: false);

    /// <summary>
    /// Sends, or steers into the running turn — <see cref="SubmitRaw"/>'s body, with one decision
    /// added: whether text the app queued with <see cref="Inject"/> goes along.
    ///
    /// <para>ATTACHED PER ARM. Starting a turn, it is prepended here and the echo keeps the
    /// transcript to the user's words. Steering, it is NOT glued to the steer: the steer queue is
    /// what the screen draws as the user's queued block and what Escape hands back to the composer,
    /// so a log pasted into it is both drawn as theirs and returned as if they had typed it. It stays
    /// in its own queue and joins the steer when the steer is delivered — see
    /// <see cref="TakeSteer"/>.</para>
    /// </summary>
    private SubmitOutcome Send(TurnRequest request, bool attachInjected)
    {
        var (text, echo, tools, origin, source) = (request.Text, request.Echo, request.Tools,
            request.Origin, request.Source);
        if (Host is null) return new SubmitOutcome.NoAgent();

        // THE USER SPOKE, raised before either arm below: a steer counts when it is sent, not when the
        // agent gets round to taking it.
        if (IsTheUser(source, origin)) UserSpoke?.Invoke();

        // OWNED, NOT MERELY BUSY — see _turnOwned. Losing the swap means a loop is running or about to, and this
        // text joins it exactly as it would join a busy turn.
        if (Interlocked.CompareExchange(ref _turnOwned, 1, 0) != 0)
        {
            AfterLosingTheTurnForTest?.Invoke();

            // A COMMAND IS HELD, NEVER STEERED. RunCommand checks IsBusy too, but a turn can start
            // between that check and this one; steering here would deliver the raw "/name" line as
            // prose and drop the expansion that was the whole point of the request.
            if (request.Expand is not null) return Hold(request.Text);

            // NOBODY TYPED THIS, SO NOTHING MAY SAY THEY DID. Steer is announced through
            // UserTurnAdded and drawn as the user's own queued block — offered back for editing when
            // a cancel returns it — which for a command's result puts words on the transcript the
            // user never wrote. The mailbox reaches the model on the turn's next lap with no such
            // announcement, so a system fact is read just as promptly and attributed to no one.
            //
            // BEFORE THE TOOL-SELECTION REPORT BELOW, which speaks to whoever passed a selection: a
            // system delivery passes none, and a warning about a selection it never sent would be
            // addressed to nobody.
            if (source is SubmitSource.System)
            {
                // REFUSED RATHER THAN GROWN. The mailbox is bounded (AgentMailbox.MaxDepth), and a
                // full one means the agent has not read the earlier messages — Queued would tell the
                // caller this landed when it did not.
                if (!Host.TryDeliver(text, origin ?? TurnOriginator.User))
                    return new SubmitOutcome.Refused("the agent has messages it has not read yet.");

                // THE ECHO IS THE NOTE, DRAWN AS IT ARRIVES. An idle wake draws a System source's echo
                // when its turn starts; joining a running one starts no turn of its own, so the line is
                // drawn here or not at all. A caller passing none — a shell's exit notice — draws
                // nothing.
                if (echo is { } line) Say(new Message(line, Severity.Info));
                ResumeIfReleasedMeanwhile();
                return new SubmitOutcome.Queued();
            }

            // A PLUGIN'S GOAL WAITS IN THE PLUGIN QUEUE, NEVER THE STEER. Its client queues there itself
            // while the session is busy, but between a lap clearing busy and the loop's own steer check
            // the session reads idle while the turn is still owned, and the goal lands here. Steered, the
            // loop would take it as a later lap of the running turn — under that turn's originator, which
            // for the user's turn defeats UnwirePluginAsync's sever check — and Pending would draw it as
            // the user's own queued block. Queued, it runs as the plugin's turn once this one ends, as a
            // goal queued while busy does.
            if (origin?.PluginName is { } plugin)
            {
                if (!Plugins.SubmitQueue.TryEnqueue(plugin, text, out var refusal))
                    return new SubmitOutcome.Refused(refusal ?? "the session's submit queue is full.");
                RunGoalIfReleasedMeanwhile();
                return new SubmitOutcome.Queued();
            }

            // Steer raises Pending, so a watcher draws its own queued block. Nothing is said here:
            // the block IS the report, and a line saying "queued" beside it would say it twice.
            SteerAs(text, origin ?? TurnOriginator.User);
            ResumeIfReleasedMeanwhile();

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

        if (attachInjected && TakeInjected() is { Length: > 0 } injected)
        {
            echo ??= text;
            text = injected + "\n\n" + text;
        }

        return StartOwnedTurn(request with { Text = text, Echo = echo });
    }

    /// <summary>
    /// Starts a lap for text just queued behind a turn that has released the conversation since this sender
    /// found it owned.
    ///
    /// <para>STORE, THEN LOOK AGAIN — what <see cref="Hold"/> does for a command. Losing the swap and queueing
    /// are two steps, and the owner can release and run its whole tail between them; its tail then finds
    /// nothing, and the text would wait in the mailbox or the steer queue until the user next typed. Looked
    /// at AFTER storing, so one of the two always sees it: either the tail's check comes later and finds it,
    /// or this one does. The swap inside ResumeIfPending keeps it to one loop either way.</para>
    ///
    /// <para>NOTHING THROWN HERE REACHES THE SENDER. Its text is already stored and, when a lap started, being
    /// read — an exception would tell it the send failed, and a caller that believes that sends again, so
    /// the model reads the same words twice. What can throw is a front end's Drained subscriber, and that
    /// is said through the session's observer, as a failed turn is.</para>
    /// </summary>
    private void ResumeIfReleasedMeanwhile()
    {
        try
        {
            if (!TurnOwned) ResumeIfPending(readMail: true);
        }
        catch (Exception ex)
        {
            Say(new Message(ex.Message, Severity.Error));
        }
    }

    /// <summary>
    /// Runs one plugin goal when the turn a sender found owned has released the conversation since.
    ///
    /// <para>STORE, THEN LOOK AGAIN, for the reason <see cref="ResumeIfReleasedMeanwhile"/> does: the owner
    /// can release and run its whole tail between this sender losing the swap and queueing, and the goal
    /// would then wait for some later turn to end. ONE GOAL, as the tail drains one — see RunWhatWaited.</para>
    ///
    /// <para>NOTHING THROWN HERE REACHES THE SENDER: its goal is already queued, and an exception would tell
    /// it otherwise.</para>
    /// </summary>
    private void RunGoalIfReleasedMeanwhile()
    {
        try
        {
            if (!TurnOwned && Plugins.SubmitQueue.DrainOne() is { } goal)
                Submit(goal.Goal, origin: TurnOriginator.Plugin(goal.PluginName));
        }
        catch (Exception ex)
        {
            Say(new Message(ex.Message, Severity.Error));
        }
    }

    /// <summary>
    /// Starts the turn loop for a request, on behalf of a caller that has already taken
    /// <see cref="_turnOwned"/> — <see cref="Send"/>, or <see cref="ResumeIfPending"/>.
    /// </summary>
    private SubmitOutcome.Started StartOwnedTurn(TurnRequest request)
    {
        // RunContinuationsAsynchronously: without it, whoever completes the source — RunTurnAsync,
        // on the turn's own thread — runs the awaiter's continuation inline, which for a plugin
        // awaiting Result means the plugin's own work executes on the turn loop's thread before
        // RunTurnAsync has unwound from the lap that produced the text.
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<SendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        // STARTED OFF THE CALLER'S SYNCHRONIZATION CONTEXT, AND THIS IS A CORRECTNESS REQUIREMENT, not
        // a throughput one. A TUI host installs a context whose Post queues onto the render thread, so
        // calling RunTurnAsync directly makes every await in the turn — the provider call, each tool,
        // the permission gate — resume there. A tool that blocks then blocks the FRAME LOOP: the app
        // stops painting and stops accepting keys, permanently, because the loop is inside an action
        // that will never return. A slow tool becomes a dead application.
        //
        // Observed exactly that way: a glob over a large repository fed `git check-ignore` more paths
        // than a pipe holds, blocked in WriteLine, and took the whole UI down with it. The pipe bug is
        // fixed where it lives, but ANY blocking work in ANY tool would have done the same.
        //
        // SUPPRESSED RATHER THAN Task.Run, BECAUSE THE PROLOGUE MUST STAY SYNCHRONOUS. RunTurnAsync
        // echoes the user's own line — _sink.UserTurnAdded — BEFORE its first await, so Submit
        // returning has always meant "the prompt is on screen". Moving the whole method to another
        // thread breaks that promise: the echo then races the caller, and five tests assert on the
        // transcript the instant Submit returns.
        //
        // What must NOT be inherited is the caller's synchronization context, so the awaits INSIDE
        // resume on the thread pool rather than the render thread. Suppressing the context for the
        // duration of the call does exactly that and nothing else: the synchronous prologue still runs
        // here, on this thread, before this method returns.
        //
        // AND NOT ConfigureAwait(false) ON EVERY AWAIT: there are over two hundred in this path, and a
        // single missed one silently restores the hazard. One boundary that cannot be forgotten beats
        // two hundred that can.
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new SubmitOutcome.Started(RunTurnAsync(request, result, outcome), result.Task, outcome.Task);
        }
        finally
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
        }
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
    ///
    /// <para>OWNERSHIP IS RELEASED HERE AND NOWHERE ELSE: every exit of the loop — answered, failed,
    /// cancelled — comes back through this finally, so no path can leave the conversation owned by a loop
    /// that has stopped.</para>
    /// </summary>
    private async Task RunTurnAsync(TurnRequest request, TaskCompletionSource<string?> result,
        TaskCompletionSource<SendOutcome> outcome)
    {
        // FAILED UNTIL THE LOOP SAYS OTHERWISE: it catches what a turn throws, so anything escaping is a
        // fault in the loop's own bookkeeping, and a failed turn's tail — run the held command — is the
        // nearest honest account of that.
        var end = TurnEnd.Failed;
        try
        {
            end = await RunTurnLoopAsync(request, result, outcome);
        }
        finally
        {
            // ANSWERED HOWEVER THE LOOP LEFT. Every exit of a lap completes these itself; a throw outside
            // a lap's own handling — the loop's bookkeeping — completes nothing, and a caller awaiting the
            // turn's Result would then wait forever. Try, so the ordinary answer already given stands.
            result.TrySetResult(null);
            outcome.TrySetResult(SendOutcome.Failed);

            // RELEASED EVEN IF THE HOOK THROWS: a conversation left owned by a loop that has ended takes
            // every later message as a steer into nothing, for the rest of the session.
            try { BeforeTurnReleasedForTest?.Invoke(); }
            finally { Volatile.Write(ref _turnOwned, 0); }

            // DROPPED AFTER OWNERSHIP IS RELEASED, NOT BEFORE. Hold stores a line whenever it finds the
            // conversation owned, so a drop while still owned leaves a gap where a line is held, nothing
            // drops it, and it runs after some later, unrelated turn. Once released, a Hold either stores
            // before this take and is dropped by it, or runs its line itself as an idle session would
            // (see Hold). Nothing is left in the slot.
            if (end is TurnEnd.Cancelled) DropHeld();

            // WHAT ARRIVED IN THE TAIL GOES FIRST, and what waited behind the turn — the held command,
            // a plugin's goal — waits behind that too: a steer typed during this turn is a later lap of
            // it as far as the user is concerned, and the lap it starts runs this same tail when it ends.
            if (!ResumeIfPending(readMail: end is not TurnEnd.Cancelled) && end is not TurnEnd.Cancelled)
                RunWhatWaited(end);
        }
    }

    /// <summary>How a turn loop ended, which decides what its tail runs.</summary>
    private enum TurnEnd { Finished, Failed, Cancelled }

    /// <summary>
    /// Starts the next lap for anything that arrived after the loop stopped looking. True when a loop owns
    /// the conversation afterwards — started here, or by a sender that got there first.
    ///
    /// <para>THE TAIL WINDOW. The agent checks its mailbox before it returns and the loop checks the steer
    /// queue before it exits — but saving the resume record and clearing busy come after both, and a report
    /// or a steer landing there has no later check to find it: it would wait until the user next typed.
    /// Checked AFTER ownership is released and
    /// retaken by the same swap Send uses, so a sender arriving at this instant and this method cannot both
    /// start a loop.</para>
    ///
    /// <para>NO MAIL AFTER A CANCEL. Esc means stop, and a report waiting in the mailbox at that moment
    /// starting a request of its own would undo it; it is read before the user's next message instead. A
    /// steer is different — CancelTurn has already handed the queue back, so one found here was typed after
    /// the stop, and is the user asking for the next thing.</para>
    /// </summary>
    private bool ResumeIfPending(bool readMail)
    {
        while (PendingSteer is not null || (readMail && (Host?.HasWakingMail ?? false)))
        {
            if (Interlocked.CompareExchange(ref _turnOwned, 1, 0) != 0) return true;   // someone else owns it now

            // TAKEN WITHOUT RAISING ANYTHING, so nothing a front end subscribed can fail between taking the
            // steer and starting its lap. Drained is raised below, once the lap is certain to run.
            var steer = TakeSteerUnannounced(out var steerOrigin);
            var mail = steer is null && readMail ? Host?.TakeMail() : null;

            // THE ORIGINATOR OF WHAT WOKE IT, not User by default. This lap is a turn of its own, and an
            // unwire decides whether it may cancel a turn by its originator: a plugin's goal or mail
            // started here under User would make that plugin unrevocable for as long as the lap runs.
            if (steer is not null)
            {
                // THE LAP STARTS EVEN IF A DRAINED SUBSCRIBER THROWS. The steer has already left the
                // queue, so a handler failing here would otherwise lose the user's words and leave the
                // conversation owned by no loop. Started in the finally, the lap owns the conversation
                // and runs this same tail when it ends; the failure still propagates, to whoever awaits
                // the turn this tail belongs to.
                try { Drained?.Invoke(steer.Shown); }
                finally
                {
                    StartOwnedTurn(new TurnRequest(steer.ForModel, steer.Shown, _turnTools, steerOrigin,
                        SubmitSource.User));
                }
                return true;
            }

            if (mail is not null)
            {
                // QUIET: mail is never drawn when a running turn reads it at a lap's top, and this lap is that
                // same delivery arriving a moment late — drawing the joined text would put the reports
                // themselves in the transcript.
                StartOwnedTurn(new TurnRequest(mail.Text, null, _turnTools, mail.Origin, SubmitSource.System)
                    { Quiet = true });
                return true;
            }

            Volatile.Write(ref _turnOwned, 0);   // both emptied meanwhile: look again
        }
        return false;
    }

    /// <summary>
    /// Runs what waited for the turn to end: the held command, and — after a turn that answered, when the
    /// held command started nothing — one plugin goal.
    ///
    /// <para>AFTER OWNERSHIP IS RELEASED, because both start a turn of their own through Send, and Send
    /// started while this loop still owned the conversation would find it owned and hold or steer them into
    /// a loop that is already over.</para>
    /// </summary>
    private void RunWhatWaited(TurnEnd end)
    {
        // THE HELD COMMAND RUNS BEFORE ANY QUEUED PLUGIN GOAL: the user typed it while watching this
        // turn, and the plugin's goal waits one more turn either way. Looked up again now — a file
        // deleted meanwhile is "unknown", not a stale copy — and expanded now, against the state this
        // turn left. In the MAIN conversation even if btw mode was entered meanwhile: see RunHeld.
        //
        // A FAILED TURN STILL RUNS IT, as an answered one does: the user typed it to come next, and a
        // provider's hiccup on this turn is no reason to throw it away. A cancel is different — that is
        // the user saying stop, and never reaches here.
        //
        // THE PLUGIN'S GOAL WAITS ONLY FOR A TURN THAT EXISTS: the held one's (Started), or one that
        // began meanwhile and held the line again (Queued) — that turn's own tail drains it, and draining
        // here would steer the goal into it. A held line that starts nothing — its file deleted, a
        // refusal — falls through, or the goal would sit until some unrelated turn ended.
        if (TakeHeld() is { } held && RunHeld(held) is SubmitOutcome.Started or SubmitOutcome.Queued)
            return;

        // A FAILED TURN DRAINS NO GOAL: only a turn that answered hands the session on to a plugin.
        if (end is TurnEnd.Failed) return;

        // A DIFFERENT QUEUE FROM THE STEER, and drained only here, once the loop is truly done rather
        // than on every lap. Steer JOINS the running turn — it becomes a later lap of THE SAME
        // RunTurnLoopAsync, still under the originator that loop was called with. Plugins.SubmitQueue
        // starts a NEW turn after this one ends, through Session.Submit, which stamps its OWN originator
        // (Plugin, not whatever this turn carried) — routing a plugin's goal through the steer queue
        // instead would hand it the CALLING turn's originator, which for a user-started turn would let a
        // plugin's work masquerade as the user's and defeat UnwirePluginAsync's sever check. ONE GOAL,
        // NOT A LOOP: draining more than one here would let a plugin monopolise the session by keeping
        // the queue non-empty, turn after turn, ahead of whatever the user typed next.
        if (Plugins.SubmitQueue.DrainOne() is { } drained)
            Submit(drained.Goal, origin: TurnOriginator.Plugin(drained.PluginName));
    }

    /// <summary>
    /// The turn loop itself — every lap of one turn, until nothing is left to send. See
    /// <see cref="RunTurnAsync"/>, which owns the conversation around it.
    /// </summary>
    private async Task<TurnEnd> RunTurnLoopAsync(TurnRequest request, TaskCompletionSource<string?> result,
        TaskCompletionSource<SendOutcome> outcome)
    {
        var text = request.Text;
        var echo = request.Echo;
        var tools = request.Tools;
        var origin = request.Origin;
        var expand = request.Expand;

        // ONLY THE FIRST LAP CAN BE THE APPLICATION'S. Every later lap is text the user typed while
        // this turn ran, and is announced as theirs.
        var announceAsNote = request.Source is SubmitSource.System;

        // A QUIET REQUEST'S FIRST LAP DRAWS NOTHING — see TurnRequest.Quiet. Later laps are steered text,
        // drawn as ever.
        var firstLap = true;

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
            AtLapStartForTest?.Invoke();

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
            // THIS SESSION'S POLICY, so its own classifier's cache is cleared rather than whichever
            // one the gate happens to hold — see PermissionDecider.Classifier.
            if (Services?.Gate is Permissions.PermissionDecider decider)
                decider.ResetTurnState(Policy);

            // THE LEVEL CHOSEN WHILE THE MODEL WORKED takes effect here, at the top of a lap — never
            // inside a tool loop. EVERY LAP, not once per Submit: a lap after the first is a message
            // the user queued during the turn, which starts a new tool loop, so it is the "next
            // message" the change was promised to. On the first lap this still runs before the
            // first await, so the notice lands before the user's own line is echoed.
            ApplyPendingThinking();

            // THE TURN'S SCOPE, created here because the turn is this method's. It was the host's,
            // so the host is not a second way to start one.
            var scope = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (_turnGate) { previous = _turn; _turn = scope; }
            previous?.Dispose();

            Volatile.Write(ref _busy, true);
            Volatile.Write(ref _originator, origin ?? TurnOriginator.User);
            SendResult sent;

            try
            {
                // THE TRANSCRIPT'S OWN TURNS, announced before the model is called so a watcher has a
                // row to stream into. echo is what the USER sees: /init sends paragraphs of briefing
                // and displays "/init", because putting the briefing on the transcript as their own
                // message attributes words to them they never wrote.
                //
                // A SYSTEM NOTE, NOT THE USER'S TURN, for what the application said — an exit notice
                // waking the session is not the user speaking, and the rail would say it was. The model
                // still receives it as a user-role message; only the attribution on screen differs.
                // Nothing is lost by skipping UserTurnAdded here: the inline job rows settle on the
                // round-ended callback AssistantTurnBegan raises on the next line, either way.
                if (!(request.Quiet && firstLap))
                {
                    if (announceAsNote) _sink?.Said(new Message(echo ?? text, Severity.Info));
                    else _sink?.UserTurnAdded(NextTurnId(), echo ?? text);
                }

                var assistantId = NextTurnId();
                _sink?.AssistantTurnBegan(assistantId);
                _sink?.AssistantTurnEnded(assistantId);   // the agent opens its own turns

                // THE COMMAND'S EXPANSION, ON THE FIRST LAP ONLY: later laps are steered text the user
                // typed, sent as itself.
                string? expanded = null;
                var fromCommand = expand is not null;
                if (expand is not null)
                {
                    expanded = await expand(scope.Token);
                    expand = null;

                    // A CANCEL IS A CANCEL EVEN WHEN NOTHING THREW. The real gate answers a prompt
                    // cancelled under it with a deny rather than an exception, so the expansion can
                    // come back with text after Esc — and the host would then store it in the
                    // conversation under a token that is already cancelled. Thrown here, it takes the
                    // cancellation path below: nothing stored, the model never called.
                    scope.Token.ThrowIfCancellationRequested();
                }

                if (fromCommand && string.IsNullOrWhiteSpace(expanded))
                {
                    // A LAP THAT SENT NOTHING, NOT THE END OF THE TURN. Text steered while the
                    // expansion ran is still waiting below, and returning from here would strand it
                    // in the queue and skip the plugin goal a turn's end drains. Failed with no text
                    // is what the caller is told when nothing follows.
                    Say(new Message("nothing to send — the command expanded to no text.", Severity.Warning));
                    sent = new SendResult("", SendOutcome.Failed);
                }
                else
                {
                    if (expanded is not null) text = expanded;

                    // AGENT TYPES CHANGED SINCE THE LAST MESSAGE: said at the front of this one, which is
                    // appended to the conversation anyway — the cached prefix is untouched. The transcript
                    // still shows what was typed.
                    if (TakeTypesNote() is { } note)
                    {
                        echo ??= text;
                        text = note + "\n\n" + text;
                    }

                    // WHAT THE TRANSCRIPT SHOWED goes with the message when it differs from what is
                    // sent, so the session list can name the session by it — see ChatMessage.Shown.
                    sent = await Host!.RunAsync(text, scope.Token, tools,
                        echo is not null && echo != text ? echo : null);
                }
            }
            catch (OperationCanceledException)
            {
                // CANCELLATION ENDS THE DRAIN, and nothing is said: CancelTurn already said
                // "Stopped." and handed the queue back through HandBack. Continuing the loop
                // would send text the user just took back.
                //
                // TRYSETRESULT, NOT SETRESULT: a caller awaiting Result while this fires gets null
                // rather than a hang, and Try tolerates the source already being completed from
                // another exit of this same method — SetResult would throw on the second call.
                result.TrySetResult(null);
                outcome.TrySetResult(SendOutcome.Cancelled);

                // A CANCELLED TURN RUNS NOTHING AFTER IT. CancelTurn has already taken the slot
                // through HandBack, so the drop RunTurnAsync makes for this only names a command held
                // when something other than CancelTurn cancelled the turn — running it would start
                // work after a stop.
                return TurnEnd.Cancelled;
            }
            catch (Exception ex)
            {
                // THE FAILURE PATH. A provider's error — refused, unreachable, a 500 — is rethrown by
                // the agent and reaches here: this is how a turn fails, not a backstop for one that
                // should not.
                //
                // IT MUST SAY SOMETHING. This loop is fire-and-forget from the caller's side —
                // the Task is awaited for its falling edge, not its result — so an exception escaping
                // silently would leave a session that stopped working with nothing on screen.
                Say(new Message(ex.Message, Severity.Error));

                // A CALLER AWAITING Result MUST NOT HANG just because the turn failed rather than
                // answered — null is the same "no text" signal a silent turn produces.
                result.TrySetResult(null);
                outcome.TrySetResult(SendOutcome.Failed);

                // A FAILED TURN STILL RUNS THE COMMAND HELD BEHIND IT — RunTurnAsync's tail, once the
                // conversation is released.
                return TurnEnd.Failed;
            }

            finally
            {
                // RELEASED HOWEVER THE LAP ENDS, including the cancellation that returns above: a turn
                // that died leaving this set would refuse every later prompt and look hung.
                Volatile.Write(ref _busy, false);

                // CLEARED WITH _busy, NOT LATER: CurrentOriginator answers "who is running now", and
                // once a turn is not running the answer is nobody, whichever originator it carried.
                Volatile.Write(ref _originator, null);

                // WHAT THE MANAGER CHANGED WHILE THIS TURN RAN. A plugin mutator skips a busy
                // session deliberately — the tool list is fixed for a request — but skipping is a
                // deferral, not a discard, and a session left on the old entries would answer
                // /plugin with a state the user changed some time ago.
                CatchUpOnPlugins();

                // AND WHAT THE CONFIG BECAME: a saved edit to this session's instance waited for the
                // turn rather than replacing the host under it.
                CatchUpOnConfig();

                // THE SCOPE STAYS UNTIL THE NEXT LAP REPLACES IT, deliberately not disposed here. A
                // cancellation callback can still be running on another thread as this unwinds, and
                // disposing under it throws from a place nobody is catching. Clearing the field is
                // enough: CancelTurn reads null and answers false.
                lock (_turnGate) { if (ReferenceEquals(_turn, scope)) _turn = null; }
            }

            AfterLapForTest?.Invoke();

            // WHOLE OR NOT AT ALL, and never an echo: what goes in on a later lap is exactly what the
            // user typed, so it is displayed as itself.
            if (TakeSteer() is not { } queued)
            {
                // THE LAST LAP'S TEXT IS THE ANSWER TO THIS Submit, not the first: a caller that
                // queued mid-turn asked a follow-up, and the reply to the follow-up is what
                // wantResult means by "the result" — not whatever the turn said before the queue
                // was drained. String.IsNullOrWhiteSpace, not null-check: a Silent turn's SendResult
                // carries "" (see SendOutcome.Silent), which is the same "nothing to report" this
                // member promises null for.
                result.TrySetResult(string.IsNullOrWhiteSpace(sent.Text) ? null : sent.Text);
                outcome.TrySetResult(sent.Outcome);

                // THE HELD COMMAND AND A PLUGIN'S GOAL RUN FROM RunTurnAsync's tail, once this loop has
                // given the conversation up — see RunWhatWaited.
                return TurnEnd.Finished;
            }

            text = queued.ForModel;
            echo = queued.Shown;
            announceAsNote = false;
            firstLap = false;
        }
    }

    /// <summary>
    /// What one call to <see cref="RunTurnAsync"/> was asked to send, and on whose behalf.
    ///
    /// <para>A RECORD BECAUSE THE GROUP IS ONE THING: five positional parameters, three of them
    /// nullable, is the shape where two get transposed and it still compiles.</para>
    /// </summary>
    private sealed record TurnRequest(string Text, string? Echo, Jobs.ToolSelection? Tools,
        TurnOriginator? Origin, SubmitSource Source)
    {
        /// <summary>
        /// A typed command's expansion, run as the turn's first step. It returns the text to send, or
        /// null when there is nothing to send.
        ///
        /// <para>INSIDE THE TURN, after the user's row is drawn, so the session is busy throughout —
        /// a second message steers rather than starting another turn — and Esc cancels it like any
        /// turn.</para>
        /// </summary>
        public Func<CancellationToken, Task<string?>>? Expand { get; init; }

        /// <summary>
        /// A first lap that draws nothing on the transcript.
        ///
        /// <para>FOR MAIL READ IN A TURN'S TAIL. A running turn reads its mailbox at the top of a lap without
        /// drawing anything, and a lap started for mail that missed that read must read it the same way —
        /// otherwise whether a report appears on screen depends on which side of the turn's end it
        /// landed.</para>
        /// </summary>
        public bool Quiet { get; init; }
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
    /// not eat it — and where it goes is not this method's business: <see cref="HandBack"/>
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

        // THE EXPANDING LINE AND THE HELD ONE ARE TAKEN BEFORE CANCEL, NOT AFTER. Cancel runs the
        // token's callbacks on this thread, and a turn can unwind inline from there — an expansion's
        // finally clears its field, and the cancel exit drops the held slot with "was not run" —
        // before Cancel returns. Read afterwards, both are gone and the user's typed command with
        // them; whether that happens depends on how the awaits below happened to be scheduled.
        var expanding = Interlocked.Exchange(ref _expanding, null);
        var held = TakeHeld();
        turn.Cancel();

        Announce(SessionChangeKind.TurnCancelled);
        Say("Stopped.");

        // AFTER the announcement, so a subscriber restoring the text into a composer is drawing over
        // a surface that has already reacted to the stop — announce before you say, and before you
        // hand anything back.
        HandBack(expanding, held);
        return true;
    }
}
