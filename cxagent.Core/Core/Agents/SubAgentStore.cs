using System.Collections.Concurrent;
using System.Text;
using CxAgent.Core.Execution;

namespace CxAgent.Core.Agents;

/// <summary>A kept sub-agent, addressable by the name the model was given at spawn.</summary>
public sealed record StoredAgent(string Name, string TypeName, string? Description,
    DateTimeOffset At, SubAgent Agent);

/// <summary>What a forget came to.</summary>
public enum ForgetOutcome
{
    /// <summary>It is gone, everywhere this store answers for.</summary>
    Forgotten,

    /// <summary>Nothing kept goes by that handle or id.</summary>
    Unknown,

    /// <summary>It is working — a run, a send or a spawn holds it. x stops it first.</summary>
    Working,

    /// <summary>Its report could not reach the parent, and stays with it until it can.</summary>
    ReportOwed,
}

/// <summary>
/// How one stretch of a kept agent's work ended — a spawn's run, a resume, a background run.
///
/// <para>THE STORE'S, NOT THE RUN'S. A background run knows its state but not its word, and a foreground stretch
/// has no run at all; a panel row has to mark either one ✔ or ✗.</para>
/// </summary>
/// <param name="Outcome">The envelope's word — <c>completed</c>, <c>capped</c>, <c>cancelled</c>…; null when the stretch threw before it had one.</param>
/// <param name="Began">When the work started — the announcement, not the queue.</param>
/// <param name="At">When it ended.</param>
public sealed record AgentEnd(string? Outcome, DateTimeOffset Began, DateTimeOffset At)
{
    /// <summary>
    /// Only a completed stretch. A capped one's answer is an account of unfinished work, and a stuck, silent or
    /// failed one is a reason to look — none of them earns the success mark.
    /// </summary>
    public bool Succeeded => Outcome == "completed";
}

/// <summary>
/// The sub-agents one session has spawned, kept so they can be asked more.
///
/// <para>THE CONTEXT IS THE AGENT, OPERATIONALLY, WHICH IS WHY KEEPING IT IS THE WHOLE FEATURE.
/// Everything else a child has — the executor registry, the provider, the type config — is shared or
/// reconstructible; the conversation is not. Re-running the original parameters would rebuild an
/// agent that must REDO the work to reach where this one already is, so asking the kept one skips
/// the file reads, the tool outputs and the dead ends it already ruled out.</para>
///
/// <para>NO BOUND THE STORE CHOOSES, AND THE ARITHMETIC IS THE ARGUMENT. A context is a list of strings already
/// in memory; even a million-token conversation is roughly 4 MB, and a resident context costs nothing per turn.
/// What leaves, leaves because the USER said so: <see cref="Forget"/>, the x on a finished row (spec decision
/// 13). A forgotten handle is kept as a tombstone, so a reach for it is answered "dismissed" rather than "no such
/// agent", and is never minted again.</para>
///
/// <para>ADDRESSED BY NAME RATHER THAN A NUMBER, which is where this diverges from a trigger id
/// deliberately. That is a small integer because a HUMAN retypes it from a listing; an agent handle
/// is typed only by the MODEL, whose failure mode is not mistyping but MISREMEMBERING. A name
/// carries its own meaning and is wrong VISIBLY when it is wrong.</para>
///
/// <para>BUSY IS HELD HERE AND NEVER PERSISTED. A crash mid-turn would otherwise leave a flag on
/// disk and the agent permanently unreachable, with nothing able to clear it.</para>
/// </summary>
public sealed class SubAgentStore
{
    /// <summary>A store over the process-wide shell registry and record, which every production path uses.</summary>
    public SubAgentStore() : this(DetachedProcessRegistry.Default, FinishedShells.Default) { }

    /// <summary>
    /// A store whose stops reach the shells in this registry and record — injectable for the reason
    /// <see cref="Jobs.Builtin.ShellBackgrounding"/>'s registry is: a test's ReapAll must not kill another
    /// test's process.
    /// </summary>
    /// <param name="shells">The registry a stop kills shells in.</param>
    /// <param name="finished">Where a killed shell is recorded.</param>
    /// <param name="clock">When stretches begin and end — a test's own; the system clock otherwise.</param>
    internal SubAgentStore(DetachedProcessRegistry shells, FinishedShells finished, TimeProvider? clock = null)
    {
        _shells = shells;
        _finished = finished;
        _clock = clock ?? TimeProvider.System;
    }

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _since = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentEnd> _ends = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _noted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _userSends = new(StringComparer.Ordinal);

    /// <summary>Foreground children the parent's turn is waiting on, by handle — what Ctrl+B and a card move or stop.</summary>
    private readonly ConcurrentDictionary<string, ForegroundRun> _foreground = new(StringComparer.Ordinal);

    /// <summary>
    /// Children the user stopped while in the foreground. A background run carries its own mark; a foreground child
    /// has no run to carry one, so the store holds it — read with the run's by <see cref="WasStoppedByUser"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _foregroundStops = new(StringComparer.Ordinal);

    /// <summary>Registers a foreground child the turn is about to wait on, on its own linked token.</summary>
    /// <param name="name">The child's kept name.</param>
    /// <param name="turn">The turn's token, which reaches the child until it is moved.</param>
    /// <param name="movable">False when nothing could take the child over — no way to deliver its later report — so a
    /// move is refused rather than accepted and left waiting.</param>
    internal ForegroundRun BeginForeground(string name, CancellationToken turn, bool movable = true)
    {
        var run = new ForegroundRun(turn, _clock.GetUtcNow(), movable);
        _foreground[name] = run;
        return run;
    }

    /// <summary>Forgets it as foreground — when its call is answered, however.</summary>
    internal void EndForeground(string name) => _foreground.TryRemove(name, out _);

    /// <summary>The foreground children the turn is waiting on, in no particular order.</summary>
    public IReadOnlyList<string> ForegroundNames() => [.. _foreground.Keys];

    /// <summary>When the turn began waiting on its earliest foreground child; null with none.</summary>
    public DateTimeOffset? ForegroundSince() =>
        _foreground.Values.Select(r => (DateTimeOffset?)r.Since).Min();

    /// <summary>Moves a foreground child to the background; false when none is waiting under that name.</summary>
    public bool MoveToBackground(string name) => _foreground.TryGetValue(name, out var run) && run.TryMove();

    /// <summary>
    /// Stops one foreground child — the user's stop, so the model is refused if it tries to resume it (spec decision
    /// 11). False when none is waiting under that name.
    /// </summary>
    public bool StopForeground(string name)
    {
        if (!_foreground.TryGetValue(name, out var run) || !run.TryStop()) return false;
        _foregroundStops[name] = 0;
        StopShellsOf(name);
        return true;
    }

    /// <summary>Whether the user's stop stands against this agent — from its background run or its foreground stop.</summary>
    public bool WasStoppedByUser(string name) =>
        BackgroundOf(name) is { StoppedByUser: true } || _foregroundStops.ContainsKey(name);

    /// <summary>Clears the user's stop — their own send hands the agent back to the model.</summary>
    internal void ClearUserStop(string name)
    {
        BackgroundOf(name)?.ClearUserStop();
        _foregroundStops.TryRemove(name, out _);
    }

    private readonly DetachedProcessRegistry _shells;
    private readonly FinishedShells _finished;
    private readonly ConcurrentDictionary<string, StoredAgent> _byName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);
    private readonly object _naming = new();

    /// <summary>
    /// Keeps this child under a handle slugged from its description, and answers the handle.
    ///
    /// <para>NAMED UNDER A LOCK because two spawns racing would otherwise both read the same
    /// taken-set and mint the same name, silently replacing one child with the other. Spawns ARE
    /// concurrent — SubAgentSpawner has a concurrency slot precisely because several run at once.</para>
    ///
    /// <para>KEPT BEFORE THE RECEIPT IS WRITTEN, which is why no handle needs reserving ahead of the child:
    /// the spawner keeps the child the moment it is built and puts the name this returns in the receipt,
    /// so the handle the model is told is the one the child already has.</para>
    /// </summary>
    public string Keep(SubAgent agent, string? description)
    {
        lock (_naming)
        {
            // A FORGOTTEN HANDLE IS TAKEN FOR GOOD: minted again, the model's stale reach for the old agent would
            // land on the new one.
            var name = Slug(description, [.. _byName.Keys, .. _forgotten.Keys]);
            _byName[name] = new StoredAgent(name, agent.TypeName, description,
                DateTimeOffset.UtcNow, agent);
            return name;
        }
    }

    /// <summary>What this child was kept under, or null when nothing keeps it.</summary>
    public string? NameOf(SubAgent agent)
    {
        lock (_naming)
            return _byName.Values.FirstOrDefault(a => ReferenceEquals(a.Agent, agent))?.Name;
    }

    /// <summary>This session's agent of that name, or null when nothing holds it.</summary>
    public StoredAgent? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The kept child with that AGENT id, or null when nothing holds it.
    ///
    /// <para>BY AGENT RATHER THAN BY HANDLE, for a caller that only ever sees the id: a child's tool
    /// calls carry its agent id and nothing else, so a front end tracking what a child is doing has
    /// no handle to look one up by.</para>
    ///
    /// <para>AND THIS IS WHY A CONSUMER NEED NOT KEEP ITS OWN REFERENCE. Every child kept here is
    /// pinned for the life of the session already, because agent_send resumes it on the context its
    /// spawn left behind; a second set of references elsewhere would pin the same Agents twice.</para>
    /// </summary>
    /// <summary>
    /// The handle a task is kept under, given its handle or its agent's id; anything else comes back as given,
    /// for the caller to answer "no task" about.
    ///
    /// <para>BOTH, BECAUSE THE RECEIPT SHOWS BOTH: a spawn answers <c>&lt;sub_agent id="…" name="…"&gt;</c>, and a
    /// model reaching for "the id" takes the first. Refusing it sent the model to task_list for a name it had
    /// already been given — and a user reading the id off a row is no different.</para>
    /// </summary>
    public string HandleFor(string handleOrId) =>
        _byName.ContainsKey(handleOrId) ? handleOrId : FindByAgentId(handleOrId)?.Name ?? handleOrId;

    public StoredAgent? FindByAgentId(string agentId) =>
        _byName.Values.FirstOrDefault(a => a.Agent.Agent.Id == agentId);

    /// <summary>Every kept agent, oldest first — the order they were spawned in.</summary>
    public IReadOnlyList<StoredAgent> All() => _byName.Values.OrderBy(a => a.At).ToList();

    public bool IsBusy(string name) => _busy.ContainsKey(name);

    /// <summary>Forgotten handles, with the agent id each one had — the tombstone.</summary>
    private readonly ConcurrentDictionary<string, string> _forgotten = new(StringComparer.Ordinal);

    /// <summary>Raised with the forgotten agent's id, so whatever else holds it — the parent's run record, a
    /// transcript's recorded calls, an open tab — lets go.</summary>
    public event Action<string>? Forgotten;

    /// <summary>
    /// Forgets a kept agent everywhere this store answers for: its handle, its runs, its record, its claim.
    ///
    /// <para>CLAIMED FIRST, AND HELD TO THE END. The claim is how busy-ness is discovered, so taking it is the
    /// atomic "is it idle" — and holding it means no send can start on the agent while it is being removed. A send
    /// that loses the claim to this finds the tombstone (see AgentReachTools.SendAsync).</para>
    ///
    /// <para>ITS REPORT FIRST (decision 14), handed to the parent as a note; refused, the agent stays.</para>
    ///
    /// <para>ITS SHELLS END, OR ARE KEPT, as the user chose when asked — a dev server or a watcher it started may be
    /// meant to outlive it, as a tab's commands may outlive the tab. Either way each is silenced first: its exit
    /// would be addressed to an agent that no longer exists.</para>
    /// </summary>
    /// <param name="handleOrId">The agent's handle or agent id.</param>
    /// <param name="keepShells">Leave its running shells running, as the session's.</param>
    public ForgetOutcome Forget(string handleOrId, bool keepShells = false)
    {
        lock (_forgetting) return ForgetLocked(HandleFor(handleOrId), keepShells);
    }

    /// <summary>How many shells this agent started are still running — what deleting it would end.</summary>
    public int LiveShellsOf(string handleOrId) =>
        Find(HandleFor(handleOrId))?.Agent.Agent.Id is { } agentId
            ? _shells.Live.Count(j => j.Job?.AgentId == agentId)
            : 0;

    /// <summary>
    /// The handle a deleted agent had, by its agent id; null for one never deleted.
    ///
    /// <para>FOR THE SHELLS IT LEFT RUNNING: their card still says whose they were, rather than an agent id.</para>
    /// </summary>
    public string? ForgottenHandle(string agentId) =>
        _forgotten.FirstOrDefault(f => f.Value == agentId).Key;

    /// <summary>
    /// Runs <paramref name="decide"/> once no forget is under way, for a send that lost the claim.
    ///
    /// <para>A FORGET HOLDS THE CLAIM FOR ALL OF ITS WORK, and writes its tombstone only after handing the report
    /// over and stopping the shells — so a send that loses the claim to it cannot tell, at that moment, whether the
    /// agent is going or staying. Waiting here for the forget to finish lets it see which: the tombstone, or the
    /// agent still kept because the forget backed off.</para>
    /// </summary>
    internal T AfterAnyForget<T>(Func<T> decide)
    {
        lock (_forgetting) return decide();
    }

    /// <summary>Serialises forgets with a send deciding what its lost claim meant — see <see cref="AfterAnyForget"/>.</summary>
    private readonly object _forgetting = new();

    private ForgetOutcome ForgetLocked(string name, bool keepShells)
    {
        if (Find(name) is not { } kept) return ForgetOutcome.Unknown;
        if (!TryClaim(name)) return ForgetOutcome.Working;

        if (BackgroundOf(name) is { } run && !run.Handoff.HandOverUndelivered())
        {
            Release(name);
            return ForgetOutcome.ReportOwed;
        }

        var agentId = kept.Agent.Agent.Id;
        EndShellsWhere(id => id == agentId, keep: keepShells);
        lock (_naming)
        {
            // THE TOMBSTONE BEFORE THE REMOVAL, so a reach that looks between the two finds one or the other.
            _forgotten[name] = agentId;
            _byName.TryRemove(name, out _);
        }
        _background.TryRemove(name, out _);
        _ends.TryRemove(name, out _);
        _since.TryRemove(name, out _);
        _noted.TryRemove(name, out _);
        _userSends.TryRemove(name, out _);
        _foreground.TryRemove(name, out _);
        _foregroundStops.TryRemove(name, out _);
        _busy.TryRemove(name, out _);

        Forgotten?.Invoke(agentId);
        return ForgetOutcome.Forgotten;
    }

    /// <summary>Whether this handle or agent id belonged to an agent the user forgot.</summary>
    public bool WasForgotten(string handleOrId) =>
        _forgotten.ContainsKey(handleOrId) || _forgotten.Values.Contains(handleOrId);

    /// <summary>What a person is told about a forgotten agent.</summary>
    public static string Dismissed(string handle) => $"'{handle}' was dismissed — it is gone, with its context.";

    /// <summary>
    /// What the model is told when it reaches for one — decision 15: no message when it happened, this answer when
    /// it asks. NAMES THE REMEDY, as every refusal to the model here does.
    /// </summary>
    internal static string DismissedForModel(string handle) =>
        $"error: the user dismissed '{handle}' — it is gone, with its context. Do not try to reach it again; spawn a "
        + "new agent if the work still needs one.";

    /// <summary>When the stretch now under way was announced; null when none is.</summary>
    public DateTimeOffset? WorkingSince(string name) => _since.TryGetValue(name, out var at) ? at : null;

    /// <summary>How this agent's latest stretch ended; null before any has.</summary>
    public AgentEnd? LastEnd(string name) => _ends.GetValueOrDefault(name);

    /// <summary>The word a release that announces none records — see <see cref="EndSend"/>.</summary>
    internal void NoteOutcome(string name, string outcome) => _noted[name] = outcome;

    /// <summary>
    /// The token a send the USER started runs on — the tab's composer, <c>/agents send</c>.
    ///
    /// <para>HELD HERE SO A STOP CAN REACH IT. That send waits in the foreground for its answer, and nothing else
    /// holds its work: a background run has its own token and a foreground spawn the parent's turn.</para>
    ///
    /// <para>AND IT TELLS A PANEL WHO IS WAITING. A claimed child with no run going is a parent waiting on its
    /// answer — read-only, unstoppable from the panel — unless it is the user's own send.</para>
    ///
    /// <para>THE FIRST SEND KEEPS THE SLOT. A second one while it answers goes to the mailbox and returns at once; had
    /// it replaced the first's token, its ending would have taken the first's Stop and count with it. It runs on a
    /// token linked to the first's instead, so a stop still ends both.</para>
    /// </summary>
    internal CancellationToken BeginUserSend(string name)
    {
        var mine = new CancellationTokenSource();
        var held = _userSends.GetOrAdd(name, mine);
        if (ReferenceEquals(held, mine)) return mine.Token;
        mine.Dispose();
        return CancellationTokenSource.CreateLinkedTokenSource(held.Token).Token;
    }

    /// <summary>Forgets the user's send once it has ended, whichever way — only the send that holds the slot.</summary>
    /// <param name="name">The agent the send went to.</param>
    /// <param name="token">The token <see cref="BeginUserSend"/> gave this send.</param>
    internal void EndUserSend(string name, CancellationToken token)
    {
        if (_userSends.TryGetValue(name, out var held) && held.Token == token)
            _userSends.TryRemove(new KeyValuePair<string, CancellationTokenSource>(name, held));
    }

    /// <summary>Whether a send the user started is under way.</summary>
    public bool IsUserSend(string name) => _userSends.ContainsKey(name);

    /// <summary>
    /// Stops a send the user started, with the shells its child started; false when none is going.
    ///
    /// <para>NOT A USER STOP OF THE AGENT. The model never asked for this work, so there is nothing for its next
    /// agent_send to be refused over.</para>
    /// </summary>
    public bool StopUserSend(string name)
    {
        if (!_userSends.TryRemove(name, out var cancel)) return false;
        cancel.Cancel();
        StopShellsOf(name);
        return true;
    }

    /// <summary>
    /// How many kept agents are working: background runs queued or running, and every claimed agent with no such
    /// run — a foreground child the parent waits on, or the user's own send.
    ///
    /// <para>WHAT THE PANEL'S HEADING AND F7 SAY (spec decision 22), so the number matches the running rows. Counted
    /// from the runs as well as the claims, because a background run is claimed and counted once, not twice.</para>
    /// </summary>
    public int Working =>
        _background.Values.Count(r => r.State is BackgroundState.Queued or BackgroundState.Running)
        + _busy.Keys.Count(name => !Going(name));

    /// <summary>
    /// The user's sends that are not also a background run — work a closing tab or a quit ends that nothing is
    /// waiting on, and so worth asking about.
    /// </summary>
    public int UserSendsAlone => _userSends.Keys.Count(name => !Going(name));

    private bool Going(string name) =>
        BackgroundOf(name) is { State: BackgroundState.Queued or BackgroundState.Running };

    /// <summary>
    /// Takes the claim WITHOUT announcing a start, or answers false when another already holds it.
    ///
    /// <para>REFUSED RATHER THAN QUEUED. <c>Agent</c> relies on "ONE TURN AT A TIME is enforced by
    /// <c>Session.Submit</c>", and a stored sub-agent sits behind no such gate — two sends would
    /// corrupt its context exactly as that comment warns. A caller that loses the claim delivers to
    /// the child's mailbox instead.</para>
    ///
    /// <para>SEPARATE FROM THE ANNOUNCEMENT BECAUSE THE TWO HAVE DIFFERENT LIFETIMES. A caller that
    /// must hold the child against other senders while it waits for something else — a concurrency
    /// permit, say — needs the exclusion for the whole wait but must not say the child is working
    /// during it: SendBegan starts the row's timer and rebases its clock, and SendEnded is the
    /// application's one announcement that a stretch of the child's work has STOPPED, which a
    /// listener records as a finished run. Releasing the claim merely to wait would therefore
    /// archive a run that never happened.</para>
    ///
    /// <para>AND THE CLAIM IS ALSO HOW BUSY-NESS IS DISCOVERED, so it must stay a test-and-set: a
    /// separate <see cref="IsBusy"/> read answers about a moment that has already passed.</para>
    /// </summary>
    internal bool TryClaim(string name) => _busy.TryAdd(name, 0);

    /// <summary>Releases a silent claim, announcing nothing.</summary>
    /// <remarks>
    /// PAIRS WITH <see cref="TryClaim"/> AND ONLY WITH IT. A claim whose begin was announced must be
    /// released through <see cref="EndSend"/> instead, or the row is left ticking forever with no
    /// archive row to settle it.
    /// </remarks>
    internal void Release(string name) => _busy.TryRemove(name, out _);

    /// <summary>
    /// Announces a start for a claim already held.
    ///
    /// <para>FOR A CALLER THAT CLAIMED SILENTLY and has now reached the moment the child really
    /// begins working. Pairing it with <see cref="EndSend"/> is what keeps the begin/end announcements
    /// balanced; announcing twice for one claim would start two runs and archive one.</para>
    /// </summary>
    internal void AnnounceBegin(string name)
    {
        _since[name] = _clock.GetUtcNow();

        // ANNOUNCED, BECAUSE NOTHING ELSE MARKS THE START OF A SEND. agent_send never becomes a job,
        // so no row is created for it and no tool report fires until the child's first call FINISHES
        // — which on a slow first call is long after the child started working. A front end showing
        // the child as settled has no earlier moment to start showing it working again.
        //
        // ALSO RAISED WHEN A SPAWN CLAIMS ITS OWN CHILD (SubAgentSpawner takes the same claim so a
        // send cannot corrupt a context the spawn is still appending to). A consumer cannot tell
        // the two apart from here and must not need to: during a spawn its row is already live, so
        // "the agent is working" is simply true both times.
        //
        // Find can miss only for a claim on a name nothing keeps, which is a claim no send can
        // follow — nothing to announce.
        if (Find(name) is { } began) SendBegan?.Invoke(began.Agent.Agent.Id);
    }

    /// <summary>Releases this agent's claim, recording how the stretch ended.</summary>
    /// <param name="name">The handle the child is kept under — the same one the claim was taken with.</param>
    /// <param name="outcome">
    /// The envelope's own word for a spawn, null for a send.
    ///
    /// <para>CARRIED THROUGH THE RELEASE because the release is what announces the stop, and the
    /// listener that writes the account has no other way to learn a capped run was capped: the
    /// spawn method does not see its own envelope until after the claim is released, by which time
    /// the account is already written.</para>
    /// </param>
    public void EndSend(string name, string? outcome = null)
    {
        // ONLY WHEN A CLAIM WAS ACTUALLY RELEASED. EndSend runs in two finallys (the spawner's and
        // agent_send's), so an unconditional announcement would say "stopped" twice for one stop.
        if (!_busy.TryRemove(name, out _)) return;

        // THE RECORD TAKES A NOTED WORD WHEN THE RELEASE NAMES NONE. A foreground resume announces no outcome —
        // listeners settle and archive it as they always have — but its row still has to say how it ended.
        var recorded = outcome ?? (_noted.TryRemove(name, out var noted) ? noted : null);
        _noted.TryRemove(name, out _);
        var began = _since.TryRemove(name, out var since) ? since : _clock.GetUtcNow();
        _ends[name] = new AgentEnd(recorded, began, _clock.GetUtcNow());

        // ANNOUNCED, BECAUSE NOTHING ELSE MARKS THE END OF A SEND. agent_send never becomes a job,
        // so no tool report fires when it returns; and a child raises nothing when it goes idle,
        // because a turn ending and a goal ending look identical from outside. A front end that
        // started showing the child working has no other moment to stop.
        if (Find(name) is { } ended) SendEnded?.Invoke(ended.Agent.Agent.Id, outcome);
    }

    /// <summary>
    /// The latest background run per handle.
    ///
    /// <para>LATEST, NOT ALL: a child resumed in the background several times has one run that matters —
    /// the one task_output reads and task_stop acts on. Earlier runs have ended and reported already.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, BackgroundRun> _background = new(StringComparer.Ordinal);

    /// <summary>Records a background stretch starting for a kept child, replacing any ended one.</summary>
    internal BackgroundRun BeginBackground(BackgroundStart start, Jobs.ResultHandoff handoff)
    {
        var run = new BackgroundRun(start, handoff);
        _background[start.Name] = run;
        return run;
    }

    /// <summary>The latest background run kept under this handle, or null when it never ran in the background.</summary>
    public BackgroundRun? BackgroundOf(string name) => _background.GetValueOrDefault(name);

    /// <summary>Every handle's latest background run, oldest first.</summary>
    public IReadOnlyList<BackgroundRun> Background() =>
        _background.Values.OrderBy(r => r.Started).ToList();

    /// <summary>
    /// Stops the background run kept under this handle, and the shells its child started; false when none
    /// is going — and then its shells are left alone, since an agent that already answered may yet be told
    /// how they end.
    /// </summary>
    public bool Stop(string name, bool byUser)
    {
        if (_background.GetValueOrDefault(name)?.RequestStop(byUser) != true) return false;
        StopShellsOf(name);
        return true;
    }

    /// <summary>
    /// Stops every background run still going, with the shells their children started, and names them —
    /// reporting nothing.
    ///
    /// <para>FOR THE PLACES A SESSION ENDS WITH WORK OUTSTANDING: a tab closed, the app quit. There is no
    /// conversation left to tell, and a report delivered into one that is closing writes into a sink
    /// nobody draws and a mailbox nobody drains. The names are returned so the caller can say what it
    /// stopped.</para>
    ///
    /// <para>SILENCED BEFORE IT IS STOPPED, so the report the stop provokes finds the handoff already
    /// closed to messages.</para>
    ///
    /// <para>EVERY KEPT CHILD'S SHELLS DIE, NOT ONLY THE STOPPED RUNS'. A child that already answered has no
    /// run to stop, but a shell it left running would outlive the session and, on exit, address its notice
    /// to that child — a wake into a conversation nobody can see. Only the stopped runs are named, because
    /// those are the work the caller is reporting it ended.</para>
    ///
    /// <para>MARKED ENDED FIRST, so an exit that races the sweep finds <see cref="Ended"/> already true and the
    /// delivery leaves its notice rather than waking anyone.</para>
    ///
    /// <para>THE SESSION AGENT'S OWN SHELLS END TOO, when the caller says which ids are its: a tab is ended as a
    /// whole, and a shell left running would submit its exit to a conversation that is gone. The store cannot
    /// tell on its own — it keeps children, and the session agent is not one of them.</para>
    ///
    /// <para>KEEPING SHELLS IS THE USER'S CHOICE TO LEAVE COMMANDS RUNNING — a dev server, a watcher — past the
    /// tab. Every shell is then silenced instead of killed, so it runs on and reports to nobody, and the choice
    /// is remembered so the runner's later sweep of a stopped run leaves those shells alone too. The agents
    /// stop either way: an agent's work exists only in a conversation, and this one is ending.</para>
    /// </summary>
    /// <param name="isSessionAgent">Which agent ids are the session's own; null leaves its shells alone.</param>
    /// <param name="keepShells">True to leave every shell running, silenced, rather than kill it.</param>
    public IReadOnlyList<string> StopAll(Func<string, bool>? isSessionAgent = null, bool keepShells = false)
    {
        _keepShells = keepShells;
        _ended = true;
        var stopped = new List<string>();
        foreach (var run in _background.Values)
        {
            run.Handoff.Silence();
            if (run.RequestStop(byUser: true)) stopped.Add(run.Start.Name);
        }

        // THE USER'S OWN SENDS END TOO. Each waits for an answer to show a person in a conversation that is closing,
        // and left running it would answer into a sink nobody draws.
        foreach (var (name, cancel) in _userSends)
        {
            cancel.Cancel();
            stopped.Add(name);
        }
        foreach (var kept in _byName.Values) StopShellsOf(kept.Name);
        if (isSessionAgent is not null) EndShellsWhere(isSessionAgent);
        return stopped;
    }

    /// <summary>
    /// True once <see cref="StopAll"/> has run: the session these children belong to has ended, and none of
    /// them is to be woken again.
    ///
    /// <para>ONE-WAY, because every caller of StopAll is a session ending — a tab closed, the app quit, a
    /// headless run finished — and none of those comes back to the same store.</para>
    ///
    /// <para>PUBLIC because a front end must refuse to open a tab onto a child whose session has ended.</para>
    /// </summary>
    public bool Ended => _ended;

    private volatile bool _ended;

    /// <summary>Set with <see cref="Ended"/> when the user chose to leave the session's commands running.</summary>
    private volatile bool _keepShells;

    /// <summary>
    /// Kills every background shell the child kept under this handle started, recording each as stopped.
    ///
    /// <para>STOPPING AN AGENT STOPS ITS SHELLS. A shell outliving the agent that started it keeps running
    /// work somebody ended, and its exit notice is addressed to the stopped agent: delivered, it wakes that
    /// agent into a turn whose answer reaches the parent as fresh work — a stopped agent reporting
    /// "completed".</para>
    ///
    /// <para>SILENCED BEFORE IT IS KILLED, so the exit the kill provokes is told to nobody: the agent it
    /// would reach is the one just stopped. The record still says how it ended, so task_list shows it
    /// stopped and task_output can read it.</para>
    ///
    /// <para>RECORDED HERE AS WELL AS BY THE EXECUTOR'S OWN EXIT HANDLER, which normally gets there first
    /// inside Kill: the store's promise that a stopped agent's shells read as stopped must not rest on a
    /// subscription in another class. FinishedShells keeps the first record, so a shell that exited on its
    /// own an instant before the kill keeps its real exit code.</para>
    ///
    /// <para>INTERNAL BECAUSE THE RUNNER CALLS IT AGAIN when a stopped run ends: a shell the child's last
    /// tool call detached after the stop's sweep would otherwise survive it.</para>
    /// </summary>
    internal void StopShellsOf(string name)
    {
        if (Find(name)?.Agent.Agent.Id is not { } agentId) return;
        EndShellsWhere(id => id == agentId);
    }

    /// <summary>
    /// Silences every live shell whose owner matches, and kills it unless the session ended keeping its shells or
    /// the caller asked to keep them.
    /// </summary>
    private void EndShellsWhere(Func<string, bool> owner, bool keep = false)
    {
        foreach (var live in _shells.Live.Where(j => j.Job?.AgentId is { } id && owner(id)))
        {
            _finished.HandoffFor(live.Process.Pid)?.Silence();
            if (keep || _keepShells) continue;
            live.Process.Kill();
            _finished.Finished(live.Process.Pid, live.Process.ExitCode, killed: true);
        }
    }

    /// <summary>
    /// Raised with a child's AGENT id when a send-claim on it is taken, and when it is released.
    ///
    /// <para>THE AGENT ID RATHER THAN THE HANDLE, because a consumer that tracks children tracks
    /// them by the id their tool calls carry — a handle is this store's own naming and means nothing
    /// to a panel keyed on agents.</para>
    /// </summary>
    public event Action<string>? SendBegan;

    /// <inheritdoc cref="SendBegan"/>
    /// <remarks>
    /// CARRIES THE OUTCOME WORD ALONGSIDE THE ID, null unless the release named one. The release is
    /// the only announcement of a stop, so a listener writing the run's account has nowhere else to
    /// learn that a capped run was capped rather than completed.
    /// </remarks>
    public event Action<string, string?>? SendEnded;

    /// <summary>
    /// A handle from a description: lower-cased, non-alphanumerics folded to single hyphens.
    ///
    /// <para>SUFFIXED ON COLLISION RATHER THAN REPLACING, because two agents genuinely may be
    /// described the same way and the second must not silently take the first one's name — the
    /// model would then send to one believing it had reached the other.</para>
    ///
    /// <para>CAPPED, because a description is a sentence and a handle is retyped by a model into
    /// every later call. Forty characters keeps it meaningful without making it a paragraph.</para>
    /// </summary>
    public static string Slug(string? description, IReadOnlyCollection<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var ch in (description ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "agent";
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        if (!taken.Contains(slug)) return slug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
