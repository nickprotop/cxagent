using CxAgent.Core.Commands;
using CxAgent.Core.Llm;

namespace CxAgent.Core.Sessions;

/// <summary>A saved config change arriving at this session.</summary>
public sealed partial class Session
{
    private Action? _pendingRebind;

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
