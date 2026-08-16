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

    /// <summary>Records the catalog this session was wired against, so it can answer
    /// <see cref="Values"/> without the caller supplying it. Called by SessionFactory.</summary>
    public void NoteCatalog(ProviderRegistry? catalog, bool classifierConfigured)
    {
        _catalog = catalog;
        _classifierConfigured = classifierConfigured;
    }

    /// <summary>
    /// The catalog this session was wired against, and whether a classifier is configured in it.
    ///
    /// <para>KEPT SO THE SESSION CAN ANSWER FOR ITSELF. <c>/model</c> offering the configured
    /// instances and <c>/mode edits</c> offering the valid modes are both questions about THIS
    /// session, and they used to be answered by the composition root reaching into a resolution and
    /// a policy it happened to have in scope.</para>
    /// </summary>
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
        _ => [],
    };

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
    public bool SwitchModel(ProviderResolution next)
    {
        if (Host is null || next.Provider is null) return false;

        Host.SwapProvider(next.Provider, next.InstanceName, next.ContextWindow);

        // THE SESSION'S OWN COPIES FOLLOW. /model's completions and the panel read InstanceName from
        // here, so leaving these behind would offer the user the model they just left.
        Provider = next.Provider;
        InstanceName = next.InstanceName;

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
