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

    /// <summary>The label every spend readout uses: <c>instance:model</c>, or the bare model when no
    /// instance is named. Two entries can serve the same model with different endpoints and windows,
    /// so the model alone cannot say where the tokens went.</summary>
    public string? SpendLabel => Provider is null
        ? null
        : InstanceName is { Length: > 0 } instance
            ? $"{instance}:{Provider.ModelId}"
            : Provider.ModelId;
}
