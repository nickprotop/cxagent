using CxAgent.Core.Commands;
using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using CxAgent.Core.Agents;

namespace CxAgent.Core.Sessions;

/// <summary>
/// ONE SESSION: a conversation, the agent running it, and everything that belongs to that agent
/// rather than to the process.
///
/// <para>WHY THIS TYPE EXISTS. Every field below already existed — as a local variable in
/// <c>AppBootstrap.Run</c>, captured by a closure. That works for exactly one session and stops
/// working for two: a local is one slot, so a second session would need a second copy of a
/// 1,400-line method. Naming the state is what makes a second one possible.</para>
///
/// <para>THE COMMENTS IN THAT METHOD ALREADY REASONED IN SESSIONS — "owned by the SESSION, not by
/// any one AgentHost", "the same session continuing on another model". The concept was load-bearing
/// long before it had a type; this is that concept written down.</para>
///
/// <para>NOT THE WHOLE OF A SESSION YET. The UI wiring, the composer state and the settings dialog
/// still live in the composition root, because they are about a WINDOW rather than a conversation
/// and a second session in one window is a different question from a second session in one process.
/// What is here is what a headless session would also need — which is the test of whether a field
/// belongs.</para>
/// </summary>
public sealed partial class Session
{
    /// <param name="workingDirectory">
    /// The folder this session works in. GIVEN, NOT READ: two sessions in one process cannot each
    /// have their own by consulting <c>Environment.CurrentDirectory</c>, and the promise the system
    /// prompt makes to the model — "relative paths resolve from the working directory" — is only
    /// true if this is the directory every tool call resolves against.
    /// </param>
    /// <remarks>
    /// TAKES ONLY ITS FOLDER. Everything else arrives with the first wire, which is what lets the
    /// session be constructed BEFORE the permission gate — the gate needs this root string, and a
    /// session that also demanded a plugin registry would have to be built after the gate that
    /// needs the root the session holds. One of the two had to stop asking for more than it needs.
    /// </remarks>
    public Session(string workingDirectory)
    {
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Where this session works. Fixed for its life — a session that moved would invalidate
    /// its own permission grants, which are scoped to a folder.</summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// The agent running this conversation, or null before the first wire.
    ///
    /// <para>REPLACED, NOT MUTATED, on an F5/F7 re-wire: a provider change builds a fresh host over
    /// the same conversation. <see cref="ReplaceHost"/> disposes the outgoing one, which is the step
    /// that was easy to forget while this was a bare local.</para>
    /// </summary>
    internal AgentHost? Host { get; private set; }

    /// <summary>
    /// True once this session has an agent to talk to.
    ///
    /// <para>THE QUESTION CALLERS ACTUALLY ASK. They were writing <c>session.Host is null</c>, which
    /// answers it correctly and makes the host part of the vocabulary for a fact about the session.
    /// A consumer should never need to name AgentHost to find out whether a session can run.</para>
    /// </summary>
    public bool HasAgent => Host is not null;

    // ---- what a consumer reads about a running session --------------------------------------------
    //
    // FORWARDED, NOT DUPLICATED. Every member below existed on AgentHost and a front end reached
    // through Host to get it — which made the host part of a consumer's vocabulary for facts the
    // session owns. That is the difference between a library with a surface and a library with an
    // interior somebody has to learn, and it is the last thing standing between this and a package.
    //
    // NULL-SAFE AT EVERY ONE, because a session before its first wire is a real state rather than a
    // programming error: SessionManager.Open builds the session, then wires it.
    //
    // THE EVENTS ATTACH TO WHATEVER HOST EXISTS WHEN YOU SUBSCRIBE, and that is a trap worth naming
    // rather than hiding. ReplaceHost builds a fresh host on an F5/F7 re-wire, and a handler
    // registered against the previous one is silently attached to an object nothing raises any more.
    // Subscribe AFTER wiring — the composition root does, inside WireRunner and immediately after
    // ReplaceHost, which is why the properties above forward live while these do not.
    //
    // The alternative — Session raising its own events and forwarding from each host as it arrives —
    // trades this for permanent subscription bookkeeping on every re-wire. Worth doing if a second
    // consumer trips over it; not worth doing pre-emptively for one that does not.

    /// <summary>What this session has spent, across its own turns and its children's.</summary>
    public Llm.TokenLedger? Ledger => Host?.Ledger;

    /// <summary>This session's agent id — what <c>--resume</c> takes.</summary>
    public string? SessionId => Host?.SessionId;

    /// <summary>
    /// True when there is something to come back to.
    ///
    /// <para>A session is written per turn, so one where nothing was said was never stored — and
    /// pointing a user at it would hand them a command that reports "no session matches" and makes
    /// resume look broken on its first use.</para>
    /// </summary>
    public bool HasSavedTurn => Host?.HasSavedTurn ?? false;

    /// <summary>
    /// Records that this session ended properly, so it is not offered back as unfinished.
    ///
    /// <para>REACHING THIS CALL IS THE ONLY EVIDENCE AVAILABLE that the process was not killed
    /// mid-session — which is precisely what makes an unfinished row mean something.</para>
    /// </summary>
    public void MarkFinished() => Host?.MarkSessionFinished();

    /// <summary>What this agent alone spent, excluding its children.</summary>
    public (int Input, int Output) OwnSpend => Host?.OwnSpend ?? (0, 0);

    /// <summary>The skills loaded into this session's briefing.</summary>
    public IReadOnlyList<string> LoadedSkills => Host?.LoadedSkills ?? [];

    /// <summary>Total tokens after each provider call.</summary>
    public event EventHandler<int>? TokensUpdated
    {
        add { if (Host is { } h) h.TokensUpdated += value; }
        remove { if (Host is { } h) h.TokensUpdated -= value; }
    }

    /// <summary>How much of the context window is in use.</summary>
    public event EventHandler<int>? ContextUsedUpdated
    {
        add { if (Host is { } h) h.ContextUsedUpdated += value; }
        remove { if (Host is { } h) h.ContextUsedUpdated -= value; }
    }

    /// <summary>The estimate between provider calls, when no measured figure has arrived yet.</summary>
    public event EventHandler<int>? ContextEstimatedUpdated
    {
        add { if (Host is { } h) h.ContextEstimatedUpdated += value; }
        remove { if (Host is { } h) h.ContextEstimatedUpdated -= value; }
    }

    /// <summary>Compaction happened: what the context was, and what it became.</summary>
    public event EventHandler<(int Before, int After)>? ContextCompressed
    {
        add { if (Host is { } h) h.ContextCompressed += value; }
        remove { if (Host is { } h) h.ContextCompressed -= value; }
    }

    /// <summary>A turn finished, carrying how many turns it took.</summary>
    public event EventHandler<int>? TurnCompleted
    {
        add { if (Host is { } h) h.TurnCompleted += value; }
        remove { if (Host is { } h) h.TurnCompleted -= value; }
    }

    /// <summary>The provider currently in use, tracked alongside <see cref="Host"/> so a diagnose
    /// action calls the CURRENT provider rather than whichever was resolved at startup.</summary>
    public ILlmProvider? Provider { get; private set; }

    /// <summary>The instance name <c>/model</c> switches BY — config's key, not the driver's display
    /// name. Two entries can serve one model, so the name is what identifies the endpoint.</summary>
    public string? InstanceName { get; private set; }

    /// <summary>The plugin registry for the current wiring, or null before the first wire. Rebuilt
    /// per re-wire so an F7 rebinding dispatches through the NEW resolution rather than the bindings
    /// that existed at launch.</summary>
    public PluginRegistry? Plugins { get; private set; }

    /// <summary>
    /// A ledger that must survive the next re-wire, or null for the usual fresh start.
    ///
    /// <para>ONLY A MODEL SWITCH SETS THIS. A reconfiguration is a new spend context; a switch is
    /// the same session continuing on another instance — and the ledger already tallies by
    /// <c>instance:model</c>, which is exactly the question switching creates.</para>
    /// </summary>
    private TokenLedger? _carriedLedger;

    /// <summary>
    /// A crashed session waiting to be restored, or null.
    ///
    /// <para>CONSUMED ONCE, like the carried ledger, so a provider swap later in the session does
    /// not silently re-restore a context the user has already moved past.</para>
    /// </summary>
    private SessionSnapshot? _pendingResume;

    /// <summary>Hands the next wire a ledger to continue from. See <see cref="_carriedLedger"/>.</summary>
    internal void CarryLedger(TokenLedger ledger) =>
        Interlocked.Exchange(ref _carriedLedger, ledger);

    /// <summary>Takes the carried ledger and clears it — see <see cref="_carriedLedger"/> for why it
    /// must be consumed exactly once.</summary>
    internal TokenLedger? TakeCarriedLedger() =>
        Interlocked.Exchange(ref _carriedLedger, null);

    /// <summary>Arms a resume for the next wire to pick up.</summary>
    /// <remarks>
    /// PUBLIC, unlike the rest of the wiring: a consumer arms a resume itself. <c>--resume</c> finds
    /// a snapshot before the first wire and hands it over, so this is the one member of the assemble
    /// group an app outside Core legitimately calls.
    /// </remarks>
    public void PendResume(SessionSnapshot snapshot) => _pendingResume = snapshot;

    /// <summary>Takes the pending resume and clears it — consumed once, see <see cref="_pendingResume"/>.</summary>
    internal SessionSnapshot? TakePendingResume()
    {
        var pending = _pendingResume;
        _pendingResume = null;
        return pending;
    }


    /// <summary>
    /// Swaps in a newly wired host, disposing the one it replaces.
    ///
    /// <para>DISPOSING IS THE POINT. A re-wire that merely reassigned would leak the outgoing host —
    /// and its subscriptions — for the rest of the process's life. That is a step a caller has to
    /// remember when the host is a bare local, and cannot forget when it goes through here.</para>
    /// </summary>
    internal void ReplaceHost(AgentHost host, ILlmProvider provider, string? instanceName,
        PluginRegistry plugins)
    {
        Host?.Dispose();
        Host = host;

        // THE SESSION'S MINTER, HANDED DOWN. Turn ids number this session's transcript, so they must
        // come from one counter — and the sink calls moved here while the agent kept minting from the
        // agent minted from its own. A transcript sink keys its rows by id, so a repeat overwrites
        // the row the first is still streaming into.
        //
        // NULL-TOLERANT because callers pass null to change the OTHER fields — a provider swap that
        // keeps the host, and tests that assert on Plugins alone. The signature does not say so,
        // which is worth knowing rather than crashing on.
        host?.UseTurnIds(NextTurnId);
        Provider = provider;
        InstanceName = instanceName;
        Plugins = plugins;
    }

    /// <summary>
    /// How this session's permission questions are judged — its folder and its edit mode.
    ///
    /// <para>KEPT SO THE SESSION CAN CHANGE ITS OWN MODE. Setting a mode means moving the agent's
    /// working mode AND the policy's edit mode together; splitting them across two owners is how one
    /// gets set and the other does not, which shows up as a session that says accept-edits and asks
    /// anyway.</para>
    /// </summary>
    public Permissions.PermissionPolicy? Policy { get; private set; }


    /// <summary>Public form of <see cref="RefusedWhileBusy"/>, for the manager's resume — which is a
    /// session operation performed from outside because the store belongs to the manager.</summary>
    public bool RefuseIfBusy() => RefusedWhileBusy();

    private bool RefusedWhileBusy()
    {
        if (!IsBusy) return false;

        // NOT CAUTION FOR ITS OWN SAKE. The tool list is fixed once a request begins — deliberately,
        // so a tool cannot appear or vanish between two turns of one request and leave the model
        // chasing something that is no longer there. Changing mode or model under a running turn is
        // exactly that, and re-wiring or restoring goes further: it replaces the agent the turn is
        // appending to, so its tool results land in a conversation nobody is reading.
        Say("[yellow]A turn is running — press Escape to stop it first.[/]");
        return true;
    }

    /// <summary>
    /// Puts this session into a working mode: the agent's delegation axis and the edit axis
    /// together.
    ///
    /// <para>ONE CALL FOR BOTH AXES, because they are one decision: setting <c>Host.Mode</c> and
    /// <c>policy.Edits</c> on adjacent lines is two places to forget.</para>
    ///
    /// <para>FALSE WITH NO HOST, so a mode set before the first wire is refused rather than silently
    /// half-applied to a policy whose agent does not exist yet.</para>
    /// </summary>
    /// <summary>
    /// Sets the mode from what the user typed at <c>/mode</c> — decided here, not by the caller.
    ///
    /// <para>EVERY INPUT THE DECISION NEEDS IS ALREADY HERE. The composition root assembled a
    /// <c>ModeQuery</c> from four captured locals — the runner's mode, folder trust, the working
    /// directory, whether a classifier exists — and all four are the session's own: <c>Host.Mode</c>,
    /// <c>Policy.FolderTrusted</c>, <c>WorkingDirectory</c>, and the flag <c>NoteCatalog</c> stores.
    /// Reaching across for them is what kept this command in the UI.</para>
    ///
    /// <para>THE REPLY IS SAID ONLY WHEN IT ADDS SOMETHING. A CHANGE is announced by
    /// <see cref="SetMode(WorkingMode)"/>, which knows what is actually in force once trust is taken
    /// into account; this says the other outcomes — a listing, an unknown axis, an
    /// already-in-that-mode — which the command knows and the session does not.</para>
    /// </summary>
    public CommandStatus SetMode(string argument)
    {
        if (Host is null)
        {
            Say("[yellow]No provider configured — there is no agent to set a mode on.[/]");
            return CommandStatus.Refused;
        }

        var decision = Commands.ModeCommand.Decide(new Commands.ModeQuery(
            argument, Host.Mode, Policy?.FolderTrusted ?? false, WorkingDirectory,
            _classifierConfigured));

        // A REFUSAL IS ITS OWN OUTCOME, and SetMode already said why — reporting it again here would
        // be one event told twice.
        if (decision.NewMode is { } next)
            return SetMode(next);

        if (decision.Reply is { Length: > 0 }) Say(decision.Reply);
        return CommandStatus.Reported;
    }

    /// <summary>
    /// How this session is set up to work right now — delegation and edits.
    ///
    /// <para>ASKED OF THE SESSION, NOT REACHED FOR THROUGH THE HOST. A front end repainting its
    /// status line was writing <c>session.Host is { } host</c> then reading <c>host.Mode</c> — which
    /// works, and makes the host part of the consumer's vocabulary for a fact the session owns. That
    /// is the difference between a library with a surface and a library with an interior somebody
    /// has to learn.</para>
    ///
    /// <para><see cref="WorkingMode.Default"/> WITH NO HOST, which is what a session that has not
    /// been wired is actually running as — not a null a caller has to branch on.</para>
    /// </summary>
    public WorkingMode Mode => Host?.Mode ?? WorkingMode.Default;

    public CommandStatus SetMode(WorkingMode mode)
    {
        if (Host is null || RefusedWhileBusy()) return CommandStatus.Refused;

        Host.Mode = mode;
        if (Policy is not null)
        {
            Policy.Edits = mode.Edits;

            // AND REMEMBERED, because the pair was the thing that got copied wrong. Both callers —
            // /mode and Shift+Tab — set the mode and then remembered it as two separate statements,
            // and the comment at the Shift+Tab site says exactly what that costs: "the pair of lines
            // this replaced is the pair that gets copied with the policy half missing". One call
            // cannot half-happen.
            Policy.RememberEdits(mode.Edits);
        }

        // SAID BY THE SESSION, not by whoever asked: /mode and Shift+Tab are one action reached two
        // ways, and what is ACTUALLY in force depends on folder trust — which the policy knows and a
        // caller would have to look up.
        Announce(SessionChangeKind.Mode);
        Say(ModeNotice.EditsChanged(mode.Edits, Policy?.FolderTrusted ?? false,
            Policy?.Root ?? WorkingDirectory));
        return CommandStatus.Changed;
    }

    /// <summary>Records the catalog this session was wired against, so it can answer
    /// <see cref="Values"/> without the caller supplying it. Called by SessionFactory.</summary>
    internal void NoteCatalog(ResolvedConfig resolution, ProviderRegistry? catalog, bool classifierConfigured)
    {
        Resolution = resolution;
        _catalog = catalog;
        _classifierConfigured = classifierConfigured;
    }

    /// <summary>
    /// Where this session says things — the same observer its turns stream through.
    ///
    /// <para>SO IT CAN REPORT ITS OWN CHANGES. A front end composing these sentences itself would
    /// reach into session state to do it, and the first one to miss a line has a session whose state
    /// changed silently. It says so itself now, once, through the channel already carrying
    /// everything else the user sees.</para>
    /// </summary>
    private ISessionObserver? _sink;

    /// <summary>
    /// What every session in this process shares — the stores a command reads, and where logs go.
    ///
    /// <para>HELD SO A COMMAND CAN ANSWER FOR ITSELF. Without it, <c>/sessions</c> and <c>/stats</c>
    /// had to be handed the store they read as a parameter, which made a consumer supply Core's own
    /// plumbing to call a method on a type Core had already wired.</para>
    ///
    /// <para>NULL BEFORE THE FIRST WIRE, and the commands that need a store say so rather than
    /// throwing — a session with no history store is an ordinary headless arrangement.</para>
    /// </summary>
    public SharedServices? Services { get; private set; }

    /// <summary>Records the services this session reads through. Called by SessionFactory.</summary>
    internal void NoteServices(SharedServices shared) => Services = shared;

    /// <summary>
    /// The manager that opened this session, or null for one built directly.
    ///
    /// <para>NEEDED FOR ONE THING: <c>/sessions resume N</c>, which restores through the manager
    /// because arming the resume, re-wiring, retiring the old row and saying so are four steps that
    /// only work together. A session that did them itself would be the second place that sequence
    /// lives.</para>
    /// </summary>
    public SessionManager? Manager { get; private set; }

    /// <summary>Records the manager that opened this session. Called by SessionManager.Open.</summary>
    internal void NoteManager(SessionManager manager) => Manager = manager;

    /// <summary>Records the observer this session speaks through. Called by SessionFactory, which is
    /// handed it in the ports.</summary>
    internal void NoteObserver(ISessionObserver? sink) => _sink = sink;

    /// <summary>Says something to whoever is watching this session. A no-op when nobody is.</summary>
    private void Say(string markup) => _sink?.Said(markup);

    /// <summary>
    /// Raised after this session changes something a front end would show.
    ///
    /// <para>THE OTHER HALF OF SAYING IT. The message is prose for a human; this is the signal for a
    /// surface that has to redraw — a status bar quoting the model, a panel counting a window. They
    /// are different consumers: nothing reads the sentence to decide what to paint, and nothing
    /// prints the signal.</para>
    ///
    /// <para>NO PAYLOAD BEYOND THE KIND. Everything a watcher needs is readable from this session,
    /// which it already holds; posting values would be a second copy of state that can disagree with
    /// the first. The kind exists only so a resume does not force a model-label repaint.</para>
    ///
    /// <para>RAISED AFTER the change is applied, so a handler that reads the session sees the new
    /// state rather than the state being replaced.</para>
    /// </summary>
    public event Action<SessionChangeKind>? Changed;

    private void Announce(SessionChangeKind kind) => Changed?.Invoke(kind);

    // ANNOUNCE BEFORE YOU SAY, everywhere. A watcher reacts to the signal by redrawing, and words
    // written first land on a surface the redraw is about to replace — which for /clear meant the
    // explanation vanished with the scrollback and left an empty screen. Harmless for a mode or
    // model change today, where the reaction only repaints a status bar; kept uniform so the next
    // change that redraws more does not have to rediscover it.

    /// <summary>Announces that an earlier conversation was restored into this session. Called by the
    /// manager, which owns the resume sequence — see SessionManager.Resume.</summary>
    internal void SayResumed(int messages)
    {
        Announce(SessionChangeKind.Resumed);
        Say($"[yellow]Resumed an earlier session: {messages} messages restored. "
          + "They are not shown above, but the agent remembers them.[/]");
    }

    /// <summary>Records the policy this session is judged by, so it can move both mode axes
    /// together. Called by SessionFactory, which is handed it in the ports.</summary>
    internal void NotePolicy(Permissions.PermissionPolicy? policy) => Policy = policy;

    /// <summary>
    /// The catalog this session was wired against, and whether a classifier is configured in it.
    ///
    /// <para>KEPT SO THE SESSION CAN ANSWER FOR ITSELF. <c>/model</c> offering the configured
    /// instances and <c>/mode edits</c> offering the valid modes are both questions about THIS
    /// session, and they used to be answered by the composition root reaching into a resolution and
    /// a policy it happened to have in scope.</para>
    /// </summary>
    /// <summary>The resolution this session is running on — its provider, window, agent types.
    /// Kept so a watcher reacting to <see cref="Changed"/> can read what it needs off the session
    /// instead of re-resolving from disk inside a UI handler, which is both wasteful and able to
    /// fail at exactly the moment the screen must be repainted.</summary>
    public ResolvedConfig? Resolution { get; private set; }

    private ProviderRegistry? _catalog;
    private bool _classifierConfigured;

    /// <summary>What this session can offer for a named set — see <see cref="CompletionSets"/>.
    ///
    /// <para>Empty for a set it does not own, so a caller can ask both a session and a manager
    /// without knowing which answers what.</para>
    /// </summary>
    public IReadOnlyList<CompletionValue> Values(string set) => set switch
    {
        CompletionSets.Providers => ProviderValues(),
        CompletionSets.EditModes => EditModeValues(),
        CompletionSets.AgentModes => AgentModeValues(),
        CompletionSets.AgentTypes => AgentTypeValues(),
        _ => [],
    };

    private static IReadOnlyList<CompletionValue> AgentModeValues() =>
    [
        new("single", "works alone; the spawn tool is withdrawn"),
        new("fan-out", "can spawn sub-agents"),
    ];

    // THE CATALOG THIS SESSION WAS WIRED WITH, which is the shipped types MERGED with config —
    // reading resolution.AgentTypes instead offered only the config-declared ones, so a session with
    // six spawnable types completed to two. A type added to config since launch is deliberately not
    // here either: it cannot be spawned by this session, and completing to it names something the
    // spawn tool rejects.
    private IReadOnlyList<CompletionValue> AgentTypeValues() =>
        _agentTypes is not { All.Count: > 0 } catalog
            ? []
            : [.. catalog.All.Select(t => new CompletionValue(
                t.Name, t.Description ?? "a sub-agent type"))];

    private AgentTypeCatalog? _agentTypes;

    /// <summary>Records the agent-type catalog this session was wired with, so it can answer for
    /// what it can actually spawn. Called by SessionFactory.</summary>
    internal void NoteAgentTypes(AgentTypeCatalog catalog) => _agentTypes = catalog;

    /// <summary>
    /// What spawns this session's children, so a model switch can move their default with it.
    ///
    /// <para>HERE RATHER THAN REACHED THROUGH THE HOST. The host holds one too — it must, because it
    /// builds the agent and spawning is a tool the agent invokes — but that is plumbing, not
    /// ownership. Switching model is the session's operation, and routing it through the host to
    /// reach the spawner was the session asking a component it owns to fetch something from a
    /// component it also owns.</para>
    /// </summary>
    private ISubAgentSpawner? _spawner;

    /// <summary>Records the spawner this session was wired with. Called by SessionFactory.</summary>
    internal void NoteSpawner(ISubAgentSpawner? spawner) => _spawner = spawner;

    private IReadOnlyList<CompletionValue> ProviderValues()
    {
        if (_catalog is null) return [];

        var models = _catalog.InstanceModels;
        var windows = _catalog.InstanceWindows;

        return [.. _catalog.InstanceNames.Select(name =>
        {
            var window = windows.TryGetValue(name, out var w) && w is { } size ? $" · {Compact(size)}" : "";

            // "in use" READ FROM THE SESSION, which is the whole reason this answer belongs here: the
            // instance in use is session state, and a catalog alone cannot say which one it is.
            var here = string.Equals(name, InstanceName, StringComparison.OrdinalIgnoreCase) ? " · in use" : "";
            return new CompletionValue(name, $"{models.GetValueOrDefault(name, "?")}{window}{here}");
        })];
    }

    // AUTO ONLY WHEN A CLASSIFIER EXISTS. Offering a mode that cannot work is worse than not offering
    // it — EditModes.ValidWith already encodes that rule for the error message, and this is the same
    // rule reaching the palette so the two cannot disagree.
    private IReadOnlyList<CompletionValue> EditModeValues()
    {
        List<CompletionValue> values =
        [
            new("always-ask", "ask before every file write and command"),
            new("accept-edits", "write files in this folder without asking; commands still ask"),
        ];

        if (_classifierConfigured)
            values.Add(new("auto", "a second model judges each request"));

        return values;
    }

    private static string Compact(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M" : $"{tokens / 1000}K";

}
