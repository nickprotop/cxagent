using CxAgent.Core.Permissions;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The terminal's way of asking, handed to the gate as a function.
///
/// <para>WHAT IS LEFT AFTER THE MOVE. The decision pipeline — silent policy, stored rules, the auto
/// classifier and its fail-closed behaviour, the trust floor, persistence — is
/// <see cref="PermissionDecider"/> in Core now. This is the part that genuinely needs a window: show
/// a control, wait for a click, put the composer back. Everything else a second front end would have
/// had to reimplement, and reimplementing a fail-closed classifier is how one quietly stops failing
/// closed.</para>
/// </summary>
public static class WindowPermissionPrompt
{
    /// <summary>
    /// One prompt on screen at a time, because one composer cell.
    ///
    /// <para>THIS QUEUE USED TO BE IN CORE, as a semaphore inside the gate held across the user's
    /// answer. It was always this window's constraint — <c>MainWindow._activePrompt</c> is what can
    /// only hold one control — and keeping it in the gate meant every consumer inherited a terminal's
    /// layout, and one unanswered prompt could freeze permission decisions for every session in the
    /// process.</para>
    ///
    /// <para>PER CONVERSATION, MATCHING WHAT IT ACTUALLY GUARDS: a composer cell, and there is one
    /// per TAB. It was static — "one composer cell per window and, today, one window" — which was
    /// true until sessions got their own tabs, and then it serialised prompts that no longer shared
    /// a cell. Two sessions each asking about their own work queued behind each other for no reason
    /// a user could see: the second session's turn simply sat there, with nothing on screen saying
    /// why, which is the failure mode this whole design is built to avoid.</para>
    ///
    /// <para>KEYED ON THE SESSION ID rather than held by the tab, because the gate's side of this
    /// has a <c>PermissionRequest</c> and not a tab — the policy is the only thing that knows which
    /// conversation is asking. A request with no policy shares one bucket, which is right: it cannot
    /// say where it belongs, so it must not be allowed to open an unbounded number of them.</para>
    ///
    /// <para>NEVER EVICTED, deliberately. One `SemaphoreSlim` per session for the life of the
    /// process is a handful of objects even for a heavy user, and evicting one while a prompt is
    /// waiting on it is a race with nothing to gain.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        OnScreen = new();

    /// <summary>The cell-guard for one conversation.</summary>
    private static SemaphoreSlim CellFor(string? sessionId) =>
        OnScreen.GetOrAdd(sessionId ?? "", _ => new SemaphoreSlim(1, 1));

    /// <summary>A gate that asks through this window.</summary>
    public static PermissionDecider Gate(ConsoleWindowSystem system, MainWindow mw,
        PermissionRulesStore store, ITranscriptWriter? transcript) =>
        // THE Message OVERLOAD, so the gate's own notices arrive with their severity intact rather
        // than as a pre-coloured sentence Core had to compose.
        PermissionDecider.WithPrompt(store,

            // INTO THE ASKING SESSION'S TRANSCRIPT. One gate serves every session, so a notice that
            // went to "the transcript" went to whichever the writer resolved — the active tab, and
            // before that a control captured at startup. A denial belongs in the history of the
            // conversation that was denied: filed anywhere else it accuses the wrong session and
            // leaves the right one with no record of what it was refused.
            //
            // NULL SESSION FALLS BACK TO THE ACTIVE TAB, which is the no-policy refusal — there is
            // no session to file it against, and in front of the user is the only place left.
            transcript is null ? null : (sessionId, message) =>
            {
                if (sessionId is not null && mw.TabForSessionId(sessionId) is { } tab)
                    ChatTranscriptSink.Post(tab.Chat, ChatTranscriptSink.Row(message));
                else
                    transcript.Write(message);
            },
            (request, offerTrust, ct) => ShowOneAtATime(system, mw, request, offerTrust, ct));

    /// <summary>
    /// Waits for the composer cell, then asks.
    ///
    /// <para>A REQUEST CANCELLED WHILE STILL QUEUED IS A DENY. It never reached the screen, so there
    /// is no control to resolve and nothing to restore — but it must not throw either, because a
    /// cancelled goal has to unwind cleanly, the same way one cancelled with its prompt on screen
    /// does. This is the behaviour the gate's semaphore used to provide, moved with it.</para>
    /// </summary>
    private static async Task<PermissionChoice> ShowOneAtATime(ConsoleWindowSystem system,
        MainWindow mw, PermissionRequest request, bool offerTrust, CancellationToken ct)
    {
        // THIS CONVERSATION'S CELL, not the process's. Two sessions asking at once each own a
        // composer, so neither has any reason to wait for the other.
        var cell = CellFor(request.Policy?.SessionId);

        try
        {
            await cell.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return PermissionChoice.Deny;
        }

        try
        {
            // NAME THE ASKING TAB BEFORE RAISING. The window raises a prompt into the ACTIVE tab's
            // composer, and with several sessions the active tab is not reliably the one that asked
            // — a prompt in the wrong composer is invisible and cannot be answered at all. The
            // request's policy is the only thing here that knows which session it belongs to.
            mw.NotePromptTabBySessionId(request.Policy?.SessionId);

            var prompt = new PermissionPromptControl(request, offerTrust);
            var content = prompt.BuildContent();   // built ONCE — see PermissionDecider's note
            // THE DENY ACTION GOES WITH IT. The window is handed the built content, not this
            // control, so it cannot answer the prompt on its own — this is the one operation
            // Escape needs, and TryCancel is already the safe idempotent Deny the cancellation
            // registration below uses.
            system.EnqueueOnUIThread(() => mw.ShowPermissionPrompt(content, prompt.TryCancel));
            return await AwaitAndRestore(prompt, ct,
                () => system.EnqueueOnUIThread(() => mw.RestoreComposer(content)));
        }
        finally
        {
            cell.Release();
        }
    }

    // Cancellation is registered on the CONTROL's own Completion (via TryCancel), not a gate-local
    // TCS: a cancelled goal must make prompt.Completion itself resolve, or this method's `finally`
    // never runs, RestoreComposer is never called, the composer stays swapped out forever, and
    // MainWindow._activePrompt stays set — silently no-opping every later ShowPermissionPrompt call
    // (MainWindow's idempotence guard). That is a permanent soft-lock: no goal can ever be submitted
    // again. The registration is disposed once Completion resolves either way (real click or
    // cancellation), so it never fires late against a control nobody is looking at. TryCancel is a
    // safe no-op if a real click already resolved it, or wins the race and a later click finds the
    // TCS already completed (PermissionPromptControl.Completion's own doc: a second resolution is
    // silently ignored, same as a double-click).
    private static async Task<PermissionChoice> AwaitAndRestore(
        PermissionPromptControl prompt, CancellationToken ct, Action restore)
    {
        using var reg = ct.Register(() => prompt.TryCancel());
        try
        {
            return await prompt.Completion;
        }
        finally
        {
            restore();
        }
    }
}
