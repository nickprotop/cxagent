using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Permissions;
using CxAgent.Core.Sessions;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// Everything a conversation needs to reach the tab showing it.
///
/// <para>ONE ROUTINE BECAUSE THERE ARE TWO CALLERS AND THERE WILL BE MORE. The startup path and
/// <see cref="NewSessionCommand"/> both open a session and both have to connect it to a tab, and
/// while they were separate code the second one connected two of eleven events. That is not an
/// oversight anybody could have caught by reading either side: nothing in <c>/sessions new</c> says
/// which subscriptions exist, so the list was reconstructed from memory and came up nine short. A
/// second session ran with no tool rows, no turn recording, no compression notice and no
/// child-worker tracking — each of which was then reported as its own bug.</para>
///
/// <para>THE ASYMMETRY IS THE POINT. The startup path ALSO builds a host, binds the classifier,
/// reports load failures and arms a resume; none of that belongs to a second session, which is why
/// the two paths were written separately in the first place. What is shared is precisely the part
/// below: the sinks, the ports, and the subscriptions. Splitting there keeps the difference
/// visible instead of hiding it behind a flag.</para>
/// </summary>
public static class SessionWiring
{
    /// <summary>What connecting a session to its tab needs from the composition root.</summary>
    /// <param name="System">The window system, for marshalling onto the UI thread.</param>
    /// <param name="Main">The window holding the tab.</param>
    /// <param name="Tab">The tab this session's output belongs to.</param>
    /// <param name="Session">The conversation being wired.</param>
    /// <param name="Manager">Opens the session and holds it beside the others.</param>
    /// <param name="Resolution">The configuration this session resolves against.</param>
    /// <param name="Mode">The working mode it starts in.</param>
    /// <param name="Policy">Its own gate policy, scoped to its folder and carrying its id.</param>
    /// <param name="ConfigDir">Where skills are discovered alongside the session's own folder.</param>
    public readonly record struct Wiring(
        ConsoleWindowSystem System,
        MainWindow Main,
        SessionTab Tab,
        Session Session,
        SessionManager Manager,
        ResolvedConfig Resolution,
        WorkingMode Mode,
        PermissionPolicy Policy,
        string ConfigDir);

    /// <summary>
    /// The two sinks a tab needs, agreeing on where a round starts.
    ///
    /// <para>A ROUND'S TOOLS ARE ONE ROW, and that needs both sinks: the boundaries are the
    /// transcript sink's (they are <c>ISessionObserver</c> members), the rows are the job sink's.
    /// Built together because a pair wired to different tabs renders a round's tools into one
    /// transcript and its text into another.</para>
    /// </summary>
    /// <remarks>
    /// <para>JOBS RENDER INLINE IN THE TRANSCRIPT, not in a side panel — one column, jobs interleaved
    /// with the turns that caused them. <c>JobPanelControl</c> still exists and still works; it is
    /// simply not wired. Both speak <c>IToolObserver</c>, so the choice of sink here is the entire
    /// switch: AgentHost never touches a control.</para>
    ///
    /// <para>NO INLINE FAILURE ACTIONS. Retry/Skip/Diagnose let the user drive the scheduler by hand
    /// while the orchestrator was mid-drive — "a drive operation is already in progress" on screen —
    /// and a hand-skipped job desynchronised the plan from what the orchestrator believed had run.
    /// The failure and its reason reach the model on the next consult, which already has a repair
    /// round.</para>
    ///
    /// <para>A RE-WIRE BUILDS A NEW PAIR AND THE TAB TAKES IT, which is what we want: the clock must
    /// drive whichever sink actually owns the rows on screen.</para>
    /// </remarks>
    public static (ChatTranscriptSink Transcript, InlineJobSink Jobs) Sinks(
        ConsoleWindowSystem system, SessionTab tab, Action? beforeUserTurn = null)
    {
        var sink = new ChatTranscriptSink(system, tab.Chat) { BeforeUserTurn = beforeUserTurn };
        var jobs = new InlineJobSink(system, tab.Chat);

        sink.OnUserTurnAdded = jobs.TurnBegan;
        sink.OnAssistantRoundEnded = jobs.RoundEnded;

        // THE TAB OWNS ITS SINK, so the window's one-second clock ticks THIS tab's running rows.
        // Held as a single window field it ticked whichever session wired it last, leaving the
        // other's elapsed times frozen and both contending on one panel.
        tab.JobSink = jobs;
        return (sink, jobs);
    }

    /// <summary>
    /// Subscribes a session's events so every readout lands on its own tab.
    ///
    /// <para>EVERY HANDLER NAMES ITS SESSION. The window's plain setters write wherever the user is
    /// looking, so a background session finishing a turn rewrote the foreground session's numbers
    /// with its own — real figures about somebody else's work, which is worse than none. The
    /// addressed overloads store against the tab and repaint only when it is in front.</para>
    /// </summary>
    public static void Subscribe(Wiring w, InlineJobSink jobs)
    {
        var (system, main, tab, session) = (w.System, w.Main, w.Tab, w.Session);
        var configDir = w.ConfigDir;

        // NOT MARSHALLED ONTO THE UI THREAD. These two only append to concurrent accumulators and
        // touch no control — the enqueue every other handler needs is for the controls, and paying
        // for a UI-thread hop per tool call would put one on the loop's hot path.
        session.ToolCallFinished += jobs.RecordToolCall;

        // AND WHICH CHILD BELONGS TO WHICH ROW, so a RUNNING worker can show the same timetable its
        // finished row will settle into — growing as calls land, with what the child is doing right
        // now as the last line.
        session.ChildSpawned += spawned => jobs.NoteChild(spawned.JobId, spawned.Child);

        // THE STORE IS WHERE A FINISHED CHILD STILL LIVES. The sink releases its own reference when
        // a spawn settles — a fan-out session must not pin every Agent it ever spawned — so a row
        // that has to follow a resumed agent asks here instead of keeping a second set of
        // references.
        jobs.SubAgents = session.SubAgents;

        // AND THE ROW FOLLOWS agent_send THROUGH THE STORE'S SEND-CLAIM, because the send is
        // invisible everywhere else: it never becomes a job, and the child's first tool report
        // fires only when that call finishes. Routed through the TAB'S CURRENT SINK rather than
        // captured `jobs`: the store lives as long as the session while a sink lives only until the
        // next re-wire (/model, resume), so a captured sink would keep reopening rows on a
        // transcript a newer sink now owns — and the newer sink would draw the same row again.
        session.SubAgents.SendBegan += agentId =>
            system.EnqueueOnUIThread(() => tab.JobSink?.WorkerResumed(agentId));
        session.SubAgents.SendEnded += agentId =>
            system.EnqueueOnUIThread(() => tab.JobSink?.WorkerSettled(agentId));

        session.TokensUpdated += (_, _) => system.EnqueueOnUIThread(() =>
        {
            // THE PARENT'S OWN SPEND, not the event's total. The event carries Ledger.TotalTokens,
            // which is the whole session — children share the ledger — and this readout sits beside
            // an occupancy percentage that is the parent's, so a session-wide figure here read as
            // the parent's and was four times too large.
            var (ownIn, ownOut) = session.OwnSpend;
            main.NoteSessionStats(session, ownIn + ownOut, contextUsed: null);

            // THE SAME EVENT, so the breakdown and the number it breaks down can never disagree.
            // Pushed rather than pulled: the panel refreshes on a clock too, and a stale tally
            // beside a live total is the kind of small inconsistency nobody can explain later.
            //
            // LEDGER IS NULLABLE because a session before its first wire has none; this runs from a
            // token event, which only a wired session raises.
            if (session.Ledger is not { } spend) return;
            main.SetSpend(session, new MainWindow.SpendReading
            {
                ByInstance = spend.ByModel,
                SubAgentTokens = spend.SubAgentTokens,
                SplitByInstance = spend.SplitByModel,
                CacheHitRate = spend.CacheHitRate,
                CacheByAgent = spend.CacheHitRateByAgent,
                CacheWrittenTokens = spend.CacheWrittenTokens,
                CostByInstance = spend.CostByInstance,
                TotalCost = spend.TotalCost,
            });
        });

        session.ContextUsedUpdated += (_, used) => system.EnqueueOnUIThread(() =>
        {
            main.NoteSessionStats(session, spent: null, contextUsed: used);
            main.SetContextUsed(session, used);
        });

        session.ContextCompressed += (_, d) => system.EnqueueOnUIThread(() =>
            main.MarkContextStale(session, d.Before, d.After));

        session.ContextEstimatedUpdated += (_, used) => system.EnqueueOnUIThread(() =>
            main.SetContextUsed(session, used, estimated: true));

        session.TurnCompleted += (_, calls) => system.EnqueueOnUIThread(() =>
        {
            // ALWAYS INTO THIS TAB'S OWN TALLY, and the panel is told only when it is showing it.
            //
            // NOT "record through the panel when in front, into the tab otherwise". That branch has
            // two ways to count one turn, and they drifted immediately: the panel starts life with a
            // tally of its own and only adopts the active tab's on a tab SWITCH, so the first
            // session's turns went into that orphan until the user switched away and back — after
            // which its panel read zero turns for a conversation that had taken two.
            //
            // One writer, one place. RecordTurn is then purely the panel's own side of it — the git
            // invalidation and the repaint — which is why it is still called rather than inlined.
            tab.Tally.Turns++;
            tab.Tally.ToolCalls += calls;
            if (ReferenceEquals(tab, main.ActiveTab)) main.NoteTurnRecorded(calls);

            // THE PARENT'S SPLIT, matching the total beside it. The ledger's InputTokens and
            // OutputTokens include every child, and a bar showing a session-wide ↑/↓ under a
            // parent-only total would be two figures that cannot be added together.
            var (turnIn, turnOut) = session.OwnSpend;
            main.SetTokenSplit(session, turnIn, turnOut);

            // SKILLS, RE-READ EVERY TURN like the agent's own discovery — a skill added or edited
            // mid-session shows up here on the same turn its description reaches the prompt, rather
            // than after a restart. Discovered from THIS session's folder, which is why it is
            // recorded against this session: two tabs in different projects have different
            // catalogues.
            //
            // The LOADED list is derived from the parent's window, so it empties itself when
            // compaction removes a body. That silent stop is the thing worth showing.
            main.SetSkills(session,
                Core.Skills.SkillCatalog.Find(session.WorkingDirectory, configDir).Skills.Count,
                session.LoadedSkills);
        });

        // WHAT THE SESSION ITSELF ANNOUNCES — mode, model, a cleared context. Subscribed HERE rather
        // than once at startup: held over the startup session alone, a second session's /mode,
        // /model and /clear reached no front end at all, so its mode line kept naming the provider
        // it had been opened with while its gate ran on another.
        session.Changed += kind => system.EnqueueOnUIThread(() =>
        {
            if (kind is SessionChangeKind.Mode) main.SetMode(session, session.Mode);

            if (kind is SessionChangeKind.Model && session.Resolution is { } current)
                main.SetResolution(session, current);

            // THE GAUGE, AND THE SCROLLBACK WITH IT. Clearing the transcript is THIS front end's
            // answer to "the messages behind it are gone" — a log writer would draw a divider and
            // keep them, which is why the session announces the fact rather than the remedy.
            if (kind is not SessionChangeKind.ContextCleared) return;

            main.SetContextUsed(session, 0);

            // THIS SESSION'S SCROLLBACK, and the session's own line arrives after it — see
            // Session.ClearContext, which announces before it speaks precisely so a watcher whose
            // reaction wipes the surface does not wipe the explanation with it. Cleared through the
            // window it wiped whichever transcript was in front: /clear in a background session
            // erased the foreground session's history and left its own untouched.
            tab.Chat.Clear();
        });

        // ONCE, AT WIRE-UP. The agent's id is fixed for its life, so there is nothing to wait for
        // and nothing to re-raise.
        tab.AgentId = session.SessionId ?? string.Empty;

        // AND THE MODE THIS SESSION WAS OPENED IN. The session itself is wired with it; the TAB has
        // its own copy, because Shift+Tab changes one conversation and the mode line reports it.
        // Left at the type's default, a tab would advertise always-ask while its gate ran on
        // whatever the process started in — a status line describing rules nobody was using.
        tab.Mode = w.Mode;

        // AND THE CONFIGURATION IT WAS OPENED WITH, so its mode line names its own model from the
        // first frame rather than after the first /model switch.
        tab.Resolution = w.Resolution;
    }

    /// <summary>
    /// The policy, carrying the classifier this session's configuration asks for.
    /// </summary>
    /// <remarks>
    /// THE POLICY IS MUTATED RATHER THAN REBUILT because it is already this session's own — built by
    /// the caller with its root, rules and edit mode. Assigning here keeps "what reviews this
    /// session" beside "what this session may do", which is the same question asked twice.
    /// </remarks>
    private static PermissionPolicy Reviewing(PermissionPolicy policy, ResolvedConfig resolution)
    {
        policy.Classifier = PermissionDecider.ClassifierFor(
            resolution.ClassifierInstance, resolution.Providers, resolution.ClassifierTimeoutSeconds);
        return policy;
    }

    /// <summary>
    /// The ports a session is opened with.
    ///
    /// <para>SHARED SO A SECOND SESSION GETS THE SAME SEAMS AS THE FIRST — the embedder tool slot,
    /// the question hook, and the model-facing command list are each easy to omit when a second
    /// call site is written from memory, and each fails silently when it is.</para>
    /// </summary>
    /// <remarks>
    /// BY VALUE, NOT `in`: the ModelFacingCommands lambda below closes over the manager, and a
    /// closure cannot capture an `in` parameter.
    /// </remarks>
    public static SessionPorts Ports(Wiring w, ISessionObserver observer, IToolObserver tools) =>
        new()
        {
            Observer = observer,
            ToolObserver = tools,

            // THE SEAM FOR EMBEDDER TOOLS, passed explicitly even while empty. This layer is where a
            // tool that needs a transcript, colour or folding would be supplied, because Core has
            // none of the three and must stay ignorant of presentation.
            Tools = [],

            // NAMING THE SESSION THAT ASKS. A question is raised in a composer, and each conversation
            // has its own — passed as a bare method group it landed on whichever tab was in FRONT,
            // so a background session's question appeared above another session's transcript and its
            // answer was given by a user reading something else. The permission gate says which
            // session it is through the request's policy; a question has no such carrier, so the
            // wiring supplies it.
            Ask = (questions, ct) => w.Main.AskQuestionAsync(w.Session, questions, ct),

            // WHAT THE MODEL IS TOLD IT CAN SUGGEST, read per turn from the registry as it actually
            // stands — after this app has overridden Core's declarations and added its own.
            ModelFacingCommands = () =>
                [.. w.Manager.Commands.All
                    .Where(c => c.TellTheModel)
                    .Select(c => (c.Name, c.Summary))],

            // JUDGED BY ITS OWN ROOT AND MODE, AND SAYING WHICH SESSION IT IS. The gate is one per
            // process; this is the session half of the decision, and passing it is what stops a
            // second session being judged against the first one's folder.
            //
            // AND REVIEWED BY ITS OWN CLASSIFIER. Auto mode asks a model whether an action is safe,
            // and which model comes from THIS session's config. Bound on the gate — one slot for the
            // process — a second session's bind replaced the first's, and a session configured with
            // no classifier cleared it for everyone: auto-review silently off, which is the
            // direction that lets a reviewable action through unreviewed.
            Policy = Reviewing(w.Policy, w.Resolution),
        };
}
