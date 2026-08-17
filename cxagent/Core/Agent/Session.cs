using CxAgent.Core.Llm;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;

namespace CxAgent.Core.Agent;

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
public sealed class Session
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
    public AgentHost? Host { get; private set; }

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
    public void CarryLedger(TokenLedger ledger) =>
        Interlocked.Exchange(ref _carriedLedger, ledger);

    /// <summary>Takes the carried ledger and clears it — see <see cref="_carriedLedger"/> for why it
    /// must be consumed exactly once.</summary>
    public TokenLedger? TakeCarriedLedger() =>
        Interlocked.Exchange(ref _carriedLedger, null);

    /// <summary>Arms a resume for the next wire to pick up.</summary>
    public void PendResume(SessionSnapshot snapshot) => _pendingResume = snapshot;

    /// <summary>Takes the pending resume and clears it — consumed once, see <see cref="_pendingResume"/>.</summary>
    public SessionSnapshot? TakePendingResume()
    {
        var pending = _pendingResume;
        _pendingResume = null;
        return pending;
    }

    /// <summary>
    /// What the user typed while a turn was running, not yet given to the model.
    ///
    /// <para>ONE MESSAGE, NOT A LIST. Several lines typed in a burst are one thought completed — a
    /// correction and then its qualifier — so a second line APPENDS rather than starting a second
    /// entry. It was already effectively one message: the previous list was only ever consumed by
    /// joining it with newlines, so nothing downstream could tell the difference. Making the data
    /// model say so removes the case where half of it is delivered and half is still pending, which
    /// is the whole reason the UI needed to know WHICH lines went in.</para>
    ///
    /// <para>ON THE SESSION, not the composition root. It was a local in a 1,600-line UI method,
    /// which is the shape <see cref="Session"/> exists to end: two sessions in one process shared
    /// one list, so a line typed into one would have been delivered to whichever turn finished
    /// first.</para>
    ///
    /// <para>LOCKED, because the two sides are on different threads: the UI appends from the render
    /// loop while the turn takes it from the agent's own flow.</para>
    /// </summary>
    private string? _pending;
    private readonly object _pendingGate = new();

    /// <summary>Adds to what is waiting, starting it if nothing was. Newline-separated: the lines
    /// were separate thoughts when they were typed, and the break is structure a model reads.</summary>
    public void Steer(string text)
    {
        lock (_pendingGate)
            _pending = string.IsNullOrEmpty(_pending) ? text : _pending + "\n" + text;
    }

    /// <summary>What is waiting, or null. For the UI to render — takes nothing.</summary>
    public string? PendingSteer
    {
        get { lock (_pendingGate) return _pending; }
    }

    /// <summary>
    /// Takes what is waiting and clears it, so it is delivered exactly once.
    ///
    /// <para>WHOLE OR NOT AT ALL. There is nothing to take partially, which is what makes the
    /// promoted/pending split a single boolean everywhere else: the transcript block is removed, not
    /// shrunk, and Escape returns everything still here because everything here is un-delivered.</para>
    /// </summary>
    public string? TakePendingSteer()
    {
        lock (_pendingGate)
        {
            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    /// <summary>
    /// Swaps in a newly wired host, disposing the one it replaces.
    ///
    /// <para>DISPOSING IS THE POINT. A re-wire that merely reassigned would leak the outgoing host —
    /// and its subscriptions — for the rest of the process's life. That is a step a caller has to
    /// remember when the host is a bare local, and cannot forget when it goes through here.</para>
    /// </summary>
    public void ReplaceHost(AgentHost host, ILlmProvider provider, string? instanceName,
        PluginRegistry plugins)
    {
        Host?.Dispose();
        Host = host;
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

    /// <summary>
    /// True while a turn is running — the session's own answer, not a flag a front end keeps.
    ///
    /// <para>Whether an action can happen NOW is a fact about this session, so the session is what
    /// knows it. A caller that wants to grey out a menu can read this; a caller that wants to try
    /// anyway gets refused with a reason, which is what the mutating methods below do.</para>
    /// </summary>
    public bool IsBusy => Host?.IsBusy ?? false;

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
    /// <para>ONE CALL FOR BOTH AXES, because they are one decision. The composition root used to set
    /// <c>Host.Mode</c> and <c>policy.Edits</c> on adjacent lines, which is two places to forget.
    /// The store write stays with the caller — remembering a preference is not a property of the
    /// running session, and a folder whose config cannot be written must still switch mode now.</para>
    ///
    /// <para>FALSE WITH NO HOST, so a mode set before the first wire is refused rather than silently
    /// half-applied to a policy whose agent does not exist yet.</para>
    /// </summary>
    public bool SetMode(WorkingMode mode)
    {
        if (Host is null || RefusedWhileBusy()) return false;

        Host.Mode = mode;
        if (Policy is not null) Policy.Edits = mode.Edits;

        // SAID BY THE SESSION, not by whoever asked. Both callers — /mode and Shift+Tab — used to
        // compose this themselves, which is two wordings for one action and two chances to report a
        // change the session had just refused. What is ACTUALLY in force depends on folder trust,
        // which the policy knows and a caller would have to look up.
        Announce(SessionChangeKind.Mode);
        Say(ModeNotice.EditsChanged(mode.Edits, Policy?.FolderTrusted ?? false,
            Policy?.Root ?? WorkingDirectory));
        return true;
    }

    /// <summary>Records the catalog this session was wired against, so it can answer
    /// <see cref="Values"/> without the caller supplying it. Called by SessionFactory.</summary>
    public void NoteCatalog(ResolvedConfig resolution, ProviderRegistry? catalog, bool classifierConfigured)
    {
        Resolution = resolution;
        _catalog = catalog;
        _classifierConfigured = classifierConfigured;
    }

    /// <summary>
    /// Where this session says things — the same observer its turns stream through.
    ///
    /// <para>SO IT CAN REPORT ITS OWN CHANGES. Switching model or mode used to be announced by the
    /// composition root, which composed the sentence by reaching into session state; every front end
    /// would have had to reimplement that, and the first to miss a line has a session whose state
    /// changed silently. It says so itself now, once, through the channel already carrying
    /// everything else the user sees.</para>
    /// </summary>
    private ISessionObserver? _sink;

    /// <summary>Records the observer this session speaks through. Called by SessionFactory, which is
    /// handed it in the ports.</summary>
    public void NoteObserver(ISessionObserver? sink) => _sink = sink;

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
    public void SayResumed(int messages)
    {
        Announce(SessionChangeKind.Resumed);
        Say($"[yellow]Resumed an earlier session: {messages} messages restored. "
          + "They are not shown above, but the agent remembers them.[/]");
    }

    /// <summary>Records the policy this session is judged by, so it can move both mode axes
    /// together. Called by SessionFactory, which is handed it in the ports.</summary>
    public void NotePolicy(Permissions.PermissionPolicy? policy) => Policy = policy;

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
    public void NoteAgentTypes(AgentTypeCatalog catalog) => _agentTypes = catalog;

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
    public void NoteSpawner(ISubAgentSpawner? spawner) => _spawner = spawner;

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

    /// <summary>
    /// Says what skills this session can reach, and which SKILL.md files were skipped.
    ///
    /// <para>DISCOVERED NOW, from the working directory — the same read the agent does each turn, so
    /// what this reports is what the model is seeing rather than a copy that could disagree.</para>
    /// </summary>
    public bool ListSkills(string globalInstructionsDir)
    {
        Say(new Commands.SkillsCommand(
            () => Skills.SkillCatalog.Find(WorkingDirectory, globalInstructionsDir)).Render());

        return true;
    }

    /// <summary>
    /// Says what this process has spent, over the window <paramref name="arguments"/> asks for.
    ///
    /// <para>THE STORE IS THE MANAGER'S, so it is passed rather than held: usage outlives any one
    /// session and a session that owned it would be claiming a total it did not earn alone.</para>
    ///
    /// <para>REPORTED, NEVER CLEARED HERE. Clearing needs a confirmation, and a confirmation needs
    /// somebody to ask — see the registry, where a front end overrides this command with one that
    /// can.</para>
    /// </summary>
    public bool SayUsage(Storage.UsageHistoryStore history, string arguments)
    {
        try
        {
            Say(Commands.StatsCommand.Render(history, arguments));
        }
        catch (Exception ex)
        {
            // REPORTED, like every other read of this store: an empty dashboard would say "you have
            // spent nothing", which is a lie a user cannot detect.
            Say($"[{Commands.Markup.Danger}]Could not read usage history: {ex.Message}[/]");
        }

        return true;
    }

    /// <summary>
    /// The briefing for a turn that explores this folder and writes down what it found.
    ///
    /// <para>SESSION WORK, and the only command here that spends tokens. It reads which instruction
    /// file to write, says so when the answer is not the obvious one — an existing CLAUDE.md is read
    /// but never written — and sends a briefing the user did not type.</para>
    ///
    /// <para>ECHOED AS "/init", NOT AS THE BRIEFING. The user typed three words; the model receives
    /// several paragraphs about what to explore. Putting those on the transcript as the user's own
    /// message attributes words to them they never wrote, on every later read of the log.</para>
    ///
    /// <para>REFUSED MID-TURN like every other operation that needs the agent — a second SendAsync
    /// on one agent appends to a live conversation from two loops.</para>
    /// </summary>
    /// <returns>
    /// The briefing to send, or null when there is no host to send it to.
    ///
    /// <para>RETURNED RATHER THAN SENT, and this is the one command that cannot own its own turn.
    /// A turn needs the caller's cancellation scope — the one Escape holds — and its queue, so a
    /// session that called SendAsync itself would start a turn nothing could stop or steer. The
    /// session decides WHAT to send and says what it noticed; the caller runs it the same way it
    /// runs a typed goal.</para>
    /// </returns>
    public string? InitialisePrompt()
    {
        if (Host is null || RefusedWhileBusy()) return null;

        var target = Commands.InitCommand.Resolve(WorkingDirectory);
        if (target.Note is { } note) Say($"[{Commands.Markup.Muted}]{note}[/]");

        return Commands.InitCommand.Prompt(target);
    }

    /// <summary>
    /// Says what sub-agent types this session can spawn, or one type's full briefing.
    ///
    /// <para>FROM THE CATALOG THIS SESSION WAS WIRED WITH — the shipped types merged with config,
    /// which is what it can actually spawn. Building one from the resolution instead, as the caller
    /// did, listed only the config-declared names.</para>
    /// </summary>
    public bool ListAgentTypes(string arguments)
    {
        if (_agentTypes is not { } catalog) return false;

        Say(new Commands.AgentsCommand(catalog).Render(arguments));
        return true;
    }

    /// <summary>Says what changed in this session's working folder, per <c>git diff</c>.</summary>
    public bool ShowDiff(string arguments)
    {
        Say(Commands.DiffCommand.Render(arguments, WorkingDirectory));
        return true;
    }

    /// <summary>
    /// Empties this session's conversation.
    ///
    /// <para>THE SESSION'S HALF ONLY. It drops the messages and says so; what a front end does about
    /// its own scrollback is the front end's decision — clearing it is one reasonable answer and
    /// drawing a divider is another, and a session with an opinion about that is a session that
    /// cannot be driven by a log writer or a web page. The <see cref="Changed"/> signal is how a
    /// surface learns there is something to redraw.</para>
    ///
    /// <para>REFUSED MID-TURN like every other mutation: emptying the list a running turn is
    /// appending to leaves that turn writing into a conversation nobody will read, and its next
    /// request carries tool results whose calls are gone.</para>
    /// </summary>
    public bool ClearContext()
    {
        if (Host is null || RefusedWhileBusy()) return false;

        Host.Context.Clear();

        // ANNOUNCED BEFORE IT IS SAID, and this is the general rule rather than a quirk of clearing:
        // a watcher reacts to the signal by redrawing, and a message written first lands on a
        // surface the redraw is about to replace. Said last, it survives whatever the reaction did.
        //
        // Found here because clearing is the reaction that destroys most — this front end wipes its
        // scrollback, so the sentence written before the announcement vanished and the user was left
        // with an empty screen and no word about why.
        Announce(SessionChangeKind.ContextCleared);
        Say("Conversation cleared.");
        return true;
    }

    /// <summary>
    /// Compacts this session's context now, if a turn is not already running.
    ///
    /// <para>REFUSED MID-TURN RATHER THAN QUEUED, and the difference from an ordinary prompt is
    /// real: a prompt is still valid when the turn ends, but this is a measurement-and-rewrite of a
    /// context that is actively changing — running it later is a DIFFERENT operation from the one
    /// that was asked for. Nothing is lost by refusing: the turn loop already compacts on measured
    /// pressure, so this costs a keystroke rather than a compaction.</para>
    ///
    /// <para>FIRE AND FORGET, returning the task for a caller that wants to await it. The refusal
    /// path returns null, having already said why.</para>
    /// </summary>
    public Task<SessionCompressor.CompressResult>? CompressNow(CancellationToken ct)
    {
        if (Host is null || RefusedWhileBusy()) return null;

        return Host.CompressNowAsync(ct);
    }

    /// <summary>
    /// Points this session at a different model, keeping the conversation.
    ///
    /// <para>WHAT THIS REPLACES. /model used to resolve an instance, arm a handoff with
    /// <see cref="CarryToNextWire"/>, re-wire the whole session and dispose the outgoing host —
    /// rebuilding an agent, its plugin registry, its sub-agent factory and its MCP binding in order
    /// to change which endpoint gets called. Everything but the provider was rebuilt identically,
    /// because /model reads the same config file it always did.</para>
    ///
    /// <para>SESSION WORK, and it belongs here rather than in the composition root: it reads this
    /// session's state and mutates this session's state. What the UI keeps is reporting the change,
    /// which is the only part that needs a window.</para>
    ///
    /// <para>FALSE WHEN THERE IS NO HOST — a switch before the first wire has nothing to point
    /// anywhere, and the caller says so rather than this throwing.</para>
    /// </summary>
    public bool SwitchModel(ActiveModel? next, string? requestedName = null)
    {
        if (RefusedWhileBusy()) return false;

        // THE FAILURE IS SAID HERE TOO, not by whoever resolved. A caller that reported it printed a
        // second, vaguer sentence on top of whatever this had already said — visible on screen as
        // "Could not switch to openrouter." directly under "A turn is running". One speaker per
        // outcome, and this is the speaker.
        if (next is null)
        {
            Say($"[red]Could not start {requestedName ?? "that model"} — it did not resolve to a "
              + "usable provider. Check its entry in config.json.[/]");
            return false;
        }

        if (Host is null) return false;

        // READ BEFORE THE SWAP, because the announcement below compares what the context WAS on the
        // model being left against the window it is moving to. The composition root used to read
        // these itself, in this order, before calling — an ordering a second front end would have to
        // know about and could get wrong. It never needed to know.
        var previousWindow = Host.Context.Window;
        var used = Host.Context.Used;

        Host.SwapProvider(next);

        // AND THE CHILDREN'S DEFAULT. A child with no provider of its own inherits it from the
        // spawner, which held the model captured at wire time — so every sub-agent kept talking to
        // the model the session started on. Confirmed in the usage archive: every explore run after
        // a switch still recorded the old instance, while the switch notice promised the opposite.
        //
        // FUTURE CHILDREN ONLY. One already running keeps its provider: it holds its own dialogue
        // with that model, and moving it mid-flight would split that dialogue across two endpoints.
        _spawner?.SwapDefaultProvider(next.Provider, next.ContextWindow, next.InstanceName);

        // THE SESSION'S OWN COPIES FOLLOW. /model's completions and the panel read InstanceName from
        // here, so leaving these behind would offer the user the model they just left.
        Provider = next.Provider;
        InstanceName = next.InstanceName;

        // THE CATALOG IS UNTOUCHED, and now unreachable from here: this method takes an ActiveModel,
        // so there is no configuration in scope to replace by accident. That is the whole reason for
        // the split — SwapProvider once moved the agent and the host and not the spawner, and nothing
        // named the set that had to move together.
        Resolution = Resolution?.WithModel(next) ?? new ResolvedConfig(next, Llm.ProviderCatalog.Empty, []);

        Announce(SessionChangeKind.Model);
        Say(ModelSwitchNotice.For(next.InstanceName ?? next.Provider.ProviderId, next.Provider.ModelId,
            next.ContextWindow, previousWindow, used));

        return true;
    }

    /// <summary>
    /// Arms the next wire to continue THIS conversation on another instance — the <c>/model</c>
    /// handoff.
    ///
    /// <para>TWO THINGS CARRY, AND THEY CARRY TOGETHER. The conversation, so a switch is not a
    /// restart; and the LEDGER, which a re-wire otherwise starts fresh. Fresh is right when the
    /// provider is being reconfigured and wrong here: a user switching model mid-conversation
    /// expects <c>/stats</c> to show the whole session, and the ledger already tallies by
    /// instance:model — which is exactly the question a switch creates.</para>
    ///
    /// <para>ONE CALL RATHER THAN TWO, because arming one without the other is silently wrong in
    /// both directions: the conversation without the ledger loses the session's spend, and the
    /// ledger without the conversation attributes the old spend to a fresh chat.</para>
    /// </summary>
    /// <returns>False when there is no host to carry from — nothing is armed.</returns>
    public bool CarryToNextWire()
    {
        if (Host is null) return false;

        PendResume(new SessionSnapshot(
            Host.SessionId, Host.Context.Snapshot(),
            Host.Ledger.InputTokens, Host.Ledger.OutputTokens, DateTimeOffset.UtcNow));
        CarryLedger(Host.Ledger);
        return true;
    }

    /// <summary>The label every spend readout uses: <c>instance:model</c>, or the bare model when no
    /// instance is named. Two entries can serve the same model with different endpoints and windows,
    /// so the model alone cannot say where the tokens went.</summary>
    public string? SpendLabel => Provider is null
        ? null
        : InstanceName is { Length: > 0 } instance
            ? $"{instance}:{Provider.ModelId}"
            : Provider.ModelId;
}
