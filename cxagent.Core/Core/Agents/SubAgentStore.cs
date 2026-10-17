using System.Collections.Concurrent;
using System.Text;
using CxAgent.Core.Execution;

namespace CxAgent.Core.Agents;

/// <summary>A kept sub-agent, addressable by the name the model was given at spawn.</summary>
public sealed record StoredAgent(string Name, string TypeName, string? Description,
    DateTimeOffset At, SubAgent Agent);

/// <summary>
/// The sub-agents one session has spawned, kept so they can be asked more.
///
/// <para>THE CONTEXT IS THE AGENT, OPERATIONALLY, WHICH IS WHY KEEPING IT IS THE WHOLE FEATURE.
/// Everything else a child has — the executor registry, the provider, the type config — is shared or
/// reconstructible; the conversation is not. Re-running the original parameters would rebuild an
/// agent that must REDO the work to reach where this one already is, so asking the kept one skips
/// the file reads, the tool outputs and the dead ends it already ruled out.</para>
///
/// <para>NO BOUND, AND THE ARITHMETIC IS THE ARGUMENT. A context is a list of strings already in
/// memory; even a million-token conversation is roughly 4 MB. A resident context costs NOTHING PER
/// TURN — the parent's window is untouched, which is the entire point of a sub-agent — so there is
/// no per-turn cost to degrade and eviction would be machinery guarding an idle allocation.</para>
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
    internal SubAgentStore(DetachedProcessRegistry shells, FinishedShells finished)
    {
        _shells = shells;
        _finished = finished;
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
            var name = Slug(description, [.. _byName.Keys]);
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
    /// </summary>
    internal bool Ended => _ended;

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
    /// Silences every live shell whose owner matches, and kills it unless the session ended keeping its shells.
    /// </summary>
    private void EndShellsWhere(Func<string, bool> owner)
    {
        foreach (var live in _shells.Live.Where(j => j.Job?.AgentId is { } id && owner(id)))
        {
            _finished.HandoffFor(live.Process.Pid)?.Silence();
            if (_keepShells) continue;
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
