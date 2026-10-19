using CxAgent.Core.Commands;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Sessions;

/// <summary>A saved config change arriving at this session.</summary>
public sealed partial class Session
{
    private Action? _pendingRebind;
    private Action? _pendingFresh;

    /// <summary>
    /// The day this session began. Every agent wired for it puts this date in its system prompt, so a
    /// re-wire on a later day sends the same prompt and keeps the cache.
    /// </summary>
    public DateOnly StartedOn { get; internal set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// The conversation's size in tokens: the model's own count of the last request — the number the
    /// status bar shows — or, from a provider that reports none, the characters at four to a token. Null
    /// only with nothing in the conversation. AN ESTIMATE IS ENOUGH: the question it answers is whether a
    /// tab has a prompt cache worth asking about, and a silent server must not make that "no".
    /// </summary>
    public int? ContextUsed => Host is not { } host ? null
        : host.LastReportedInput
          ?? host.Context.ProjectedUsed
          ?? (host.Context.Count > 0 ? Math.Max(1, host.Context.TotalChars() / 4) : null);

    /// <summary>A saved change waits for this tab's next clear or compaction — the mode line says so.</summary>
    public bool ConfigPending => Volatile.Read(ref _pendingFresh) is not null;

    /// <summary>
    /// Holds <paramref name="apply"/> until the conversation is fresh — cleared or compacted — when the
    /// prompt cache is rebuilt anyway, so applying then costs nothing extra.
    /// </summary>
    internal void PendUntilFresh(Action apply)
    {
        Interlocked.Exchange(ref _pendingFresh, apply);
        Announce(SessionChangeKind.Model);
    }

    /// <summary>The conversation was cleared or compacted: a change waiting for that, now.</summary>
    internal void OnContextFresh()
    {
        if (Interlocked.Exchange(ref _pendingFresh, null) is not { } apply) return;
        Announce(SessionChangeKind.Model);
        apply();
    }

    /// <summary>
    /// A <c>/model</c> switch drops a waiting change: the user chose a model from the catalog the change
    /// already brought, and applying the wait afterwards would undo their choice.
    /// </summary>
    private void DropPendingFresh()
    {
        if (Interlocked.Exchange(ref _pendingFresh, null) is not null) Announce(SessionChangeKind.Model);
    }

    /// <summary>
    /// The same model on the same endpoint behind a new provider object — a rotated key, new headers — swapped
    /// the way <c>/model</c> swaps: no new host, no new agent, so nothing the model sees changes and the
    /// prompt cache is kept. The session's own thinking level is kept too.
    /// </summary>
    internal void SwapInPlace(ActiveModel next, ResolvedConfig fresh, string note)
    {
        if (Host is null) return;
        var keep = next.Provider is IThinkingProvider thinker && Provider is IThinkingProvider
            ? next with { Provider = thinker.WithThinking(ThinkingLevel) }
            : next;
        SwapModel(keep);
        Resolution = fresh.WithModel(keep);
        _catalog = fresh.Providers;
        Announce(SessionChangeKind.Model);
        Say(new Message($"{note} — applied in place; the prompt cache is kept."));
    }

    /// <summary>
    /// What reaches every session's wiring without touching what the model sees: who reviews auto mode,
    /// how many sub-agents may run at once, and where each agent type runs.
    /// </summary>
    internal void RefreshWide(ResolvedConfig fresh)
    {
        if (Policy is { } policy)
            policy.Classifier = Permissions.PermissionDecider.ClassifierFor(
                fresh.ClassifierInstance, fresh.Providers, fresh.ClassifierTimeoutSeconds);
        var types = new Agents.AgentTypeCatalog(fresh.AgentTypes, fresh.Providers);
        var said = DescribeTypes(_agentTypes);
        _spawner?.Refresh(fresh.MaxConcurrentAgents, types);
        NoteAgentTypes(types);

        // THE MODEL HEARS OF A NEW OR CHANGED TYPE IN THE NEXT MESSAGE, not in the spawn tool: that tool's
        // description is part of the cached prompt, and rewriting it re-sends the whole conversation.
        if (_spawner is not null && DescribeTypes(types) is var now && now != said)
            Volatile.Write(ref _typesNote, "[Agent types available now: " + now + "]");
    }

    private string? _typesNote;

    /// <summary>The catalog as the types note says it: each type, what it is for, and where it may run —
    /// with each provider's description, since the model chooses by them and the tool's own text is the
    /// session's first, not this refresh's.</summary>
    internal static string DescribeTypes(Agents.AgentTypeCatalog? catalog) =>
        catalog is null ? "" : string.Join("; ", catalog.All.Select(t =>
            (t.Description is { Length: > 0 } d ? $"{t.Name} — {d}" : t.Name)
            + (t.Disabled is { } why ? $" (disabled: {why})"
               // NOT A CONSTANT for "all": the note is sent only when this text changes, so it must name
               // the providers, or one added or re-described mid-session would never be announced.
               : t.AnyProvider ? $" (any provider, no default: {string.Join(", ", t.Offers.Select(o => o.Description is { Length: > 0 } od ? $"{o.Label} ({od})" : o.Label))})"
               : t.Offers.Count > 0 ? $" (runs on {string.Join(", ", t.Offers.Select(o => o.Description is { Length: > 0 } od ? $"{o.Label} ({od})" : o.Label))})"
               : "")));

    /// <summary>The note for the next message, once.</summary>
    internal string? TakeTypesNote() => Interlocked.Exchange(ref _typesNote, null);

    /// <summary>
    /// The new config's catalog, kept on this session without a re-wire — for a session the change does
    /// not touch. Its model stays; <c>/model</c> and a new tab opened from here then see every instance
    /// the file now has.
    /// </summary>
    internal void AdoptCatalog(ResolvedConfig fresh)
    {
        if (Resolution is { } current) Resolution = current with { Catalog = fresh.Catalog, Entries = fresh.Entries };
        _catalog = fresh.Providers;
        _classifierConfigured = fresh.ClassifierInstance is { Length: > 0 };
    }

    /// <summary>Forgets a conversation carried for a wire that did not happen.</summary>
    internal void DropCarried()
    {
        TakePendingResume();
        TakeCarriedLedger();
    }

    /// <summary>Said when a change touched this session's provider but the session could not move.</summary>
    internal void SayNotRebound(string why) =>
        Say(new Message($"The saved change could not be applied to this tab: {why}. It stays on its current provider.",
            Severity.Warning));

    /// <summary>
    /// Runs <paramref name="rebind"/> now when idle, or when the running turn ends.
    ///
    /// <para>NEVER INSIDE A TURN: a re-wire replaces the host the turn is writing into. The last one
    /// pending wins — two saves during one turn mean the file's latest state, not both in order.</para>
    /// </summary>
    internal void RebindWhenIdle(Action rebind)
    {
        if (!IsBusy) { rebind(); return; }
        Interlocked.Exchange(ref _pendingRebind, rebind);

        // BUSY MAY HAVE FALLEN between the check and the store: the turn's finally then already ran its
        // catch-up, and nothing else would take this one.
        if (!IsBusy) CatchUpOnConfig();
    }

    /// <summary>
    /// The turn's finally: a rebind that waited for it, scheduled. A queued line may start the next lap
    /// at once; the scheduled rebind asks again whether the session is idle when it runs.
    /// </summary>
    internal void CatchUpOnConfig()
    {
        if (Interlocked.Exchange(ref _pendingRebind, null) is not { } rebind) return;
        try { rebind(); }
        catch (Exception ex)
        {
            // IN THE TURN'S UNWIND, where nothing catches: said instead, and the tab stays on its old
            // provider — still working, and still saying why it did not move.
            Say(new Message($"The config change could not be applied here: {ex.Message}", Severity.Warning));
        }
    }

    /// <summary>Said after a rebind lands.</summary>
    internal void SayRebound(string note) =>
        Say(new Message($"{note} — this tab now uses it; the prompt cache starts over."));
}
