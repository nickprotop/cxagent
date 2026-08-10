using CxAgent.Core.Agent;
using CxAgent.Core.Permissions;
using SharpConsoleUI;

namespace CxAgent.UI;

/// <summary>
/// The real, interactive <see cref="IPermissionGate"/>: consults <see cref="PermissionPolicy"/>
/// for the silent classes (in-boundary-and-trusted file ops, stored "Always" rules), and for
/// everything else marshals onto the UI thread to show a <see cref="PermissionPromptControl"/> in
/// the composer cell, awaits the user's answer, persists it if asked, and echoes the outcome into
/// the transcript.
///
/// <para><see cref="RequestAsync"/> runs on a background scheduler thread (a job executor's
/// thread, one per running job up to <c>maxParallel</c> — AgentHost.cs:264 — currently 4). It
/// therefore never touches a control directly; it only ever reaches the UI through
/// <c>system.EnqueueOnUIThread</c>, via the <see cref="_promptHook"/> seam below.</para>
///
/// <para><b>Why concurrent requests are not a deadlock.</b> <see cref="_oneAtATime"/> serialises
/// prompts to one at a time — the composer cell can only show one control. With maxParallel = 4,
/// up to four jobs can each want permission at once; the second, third, and fourth PARK on this
/// semaphore, and each one's scheduler slot (and thread) sits idle for as long as the user takes
/// to answer the one in front of it. That is not a deadlock, because the answer the user is being
/// asked for never depends on any of the parked jobs finishing — nothing downstream of the parked
/// `WaitAsync` needs to run for the prompt in front to resolve. It is the exact same shape as a
/// copilot draft parking an entire goal today (AgentHost's approval gate): the whole point of
/// asking is that the app waits for a human, and waiting for a human is not progress-blocked on
/// the app's own work.</para>
/// </summary>
public sealed class InteractivePermissionGate : IPermissionGate
{
    private readonly PermissionPolicy _policy;
    private readonly PermissionRulesStore _store;
    private readonly string _workingDir;
    private readonly IChatSink? _sink;

    // The UI seam: shows a prompt for `request` (offerTrust decides whether the fourth "Trust
    // this folder" button appears) and completes with the user's choice. The real constructor
    // implements this by building ONE PermissionPromptControl, calling BuildContent() exactly
    // once, holding that IWindowControl reference, and passing the SAME reference to both
    // MainWindow.ShowPermissionPrompt and RestoreComposer — GridControl.ReplaceControl matches by
    // ReferenceEquals (GridControl.cs:389), so a second BuildContent() call would throw from
    // inside the render loop. Tests implement this with a scripted TCS instead of a live window —
    // the same trick IChatSink plays for AgentHost.
    //
    // Takes `ct` too (not just request/offerTrust): the real implementation registers cancellation
    // directly against the CONTROL's own Completion, not just a gate-local TCS — see the class doc
    // amendment on "why cancellation must resolve the control, not just RequestAsync's return
    // value" for the soft-lock this closes.
    private readonly Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> _promptHook;

    // Serialises prompts to one at a time: the composer cell can only show one control, and
    // showing a second while the first is still up is exactly the caller bug MainWindow's own
    // idempotence guard defends against (ShowPermissionPrompt no-ops rather than crash, but we
    // must not rely on that — see the class doc for why parking here is safe, not a deadlock).
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>The real, UI-wired gate. `system`/`mw` are used only inside the prompt hook, kept
    /// thin: build the control, show it, await Completion, restore the composer in `finally`.
    /// `workingDir` is the same root string AppBootstrap built `policy` from (captured once,
    /// Path.GetFullPath(Environment.CurrentDirectory)) — PermissionPolicy doesn't expose its
    /// captured root, so the caller passes it again here rather than this class re-deriving it.</summary>
    public InteractivePermissionGate(ConsoleWindowSystem system, MainWindow mw,
        string workingDir, PermissionPolicy policy, PermissionRulesStore store, IChatSink? sink)
        : this(policy, store, workingDir, sink,
            (request, offerTrust, ct) =>
            {
                var prompt = new PermissionPromptControl(request, offerTrust);
                var content = prompt.BuildContent();   // built ONCE — see the field's doc comment
                system.EnqueueOnUIThread(() => mw.ShowPermissionPrompt(content));
                return AwaitAndRestore(prompt, ct, () => system.EnqueueOnUIThread(() => mw.RestoreComposer(content)));
            })
    {
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

    private InteractivePermissionGate(PermissionPolicy policy, PermissionRulesStore store, string workingDir,
        IChatSink? sink, Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook)
    {
        _policy = policy;
        _store = store;
        _workingDir = workingDir;
        _sink = sink;
        _promptHook = promptHook;
    }

    /// <summary>Test seam: drives the gate with a scripted prompt function instead of a live
    /// window (the fake-prompt-hook trick the brief specifies). Public rather than internal —
    /// this codebase has no InternalsVisibleTo grant (see OrchestratorLoop.cs:495) — but is not
    /// part of the gate's runtime API surface: production code always uses the UI-wired
    /// constructor above.</summary>
    public static InteractivePermissionGate ForTesting(PermissionPolicy policy, PermissionRulesStore store,
        string workingDir, IChatSink? sink,
        Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook) =>
        new(policy, store, workingDir, sink, promptHook);

    public async Task<bool> RequestAsync(PermissionRequest request, CancellationToken ct)
    {
        if (_policy.IsSilentlyAllowed(request))
            return true;   // in-boundary-and-trusted, or a matching stored rule — no UI at all

        try
        {
            await _oneAtATime.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while still queued behind another prompt — a deny, not a hang, and not a
            // thrown exception either: a cancelled goal must resolve cleanly, same as a cancelled
            // goal that answers Deny after the prompt was actually shown (below).
            return false;
        }
        try
        {
            var offerTrust = (request.Kind is PermissionKind.FileRead or PermissionKind.FileWrite)
                && _policy.IsInBoundary(request.Display);

            // `ct` is handed straight to the prompt hook rather than raced against it with a
            // second, gate-local TaskCompletionSource: an earlier version resolved a LOCAL tcs to
            // Deny on cancellation and returned, while the real UI prompt (awaiting its OWN
            // Completion, unrelated to that local tcs) kept sitting there forever waiting for a
            // click that would never come — the composer never got restored and every later
            // permission prompt silently no-opped (MainWindow's idempotence guard). The real
            // prompt hook now registers cancellation directly on the control's Completion (see
            // AwaitAndRestore), so awaiting it here is both correct AND still resolves promptly on
            // cancellation — there is no longer a separate path to race.
            PermissionChoice choice;
            try
            {
                choice = await _promptHook(request, offerTrust, ct);
            }
            catch
            {
                // A throwing prompt hook must not hang the caller, and must not propagate an
                // exception the render loop never expected to see from a permission decision —
                // Deny is always a safe fallback here.
                choice = PermissionChoice.Deny;
            }
            return Apply(request, choice);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private bool Apply(PermissionRequest request, PermissionChoice choice)
    {
        switch (choice)
        {
            case PermissionChoice.Once:
                // NO echo. The user just pressed "Allow once" on a prompt showing this exact
                // command — telling them what they chose, one line later, is the transcript
                // narrating them back to themselves. Every allow used to print a line, which on a
                // shell-heavy goal buried the conversation under confirmations of things the user
                // had personally just authorised.
                //
                // DENIALS and SAVE FAILURES still speak (below), because those report something the
                // user did NOT already know: work that did not happen, or a grant that will not
                // survive a restart.
                return true;

            case PermissionChoice.Always:
                // AlwaysRule is null exactly when the request can't be truthfully generalised
                // into a rule (e.g. a shell command with a custom env — PermissionPolicy's
                // ShellRequest). The "Always" button never appears in that case
                // (PermissionPromptControl.BuildContent), so this should be unreachable — but if
                // it somehow is reached, fall back to Once rather than crash or store a null rule.
                if (request.AlwaysRule is null)
                    return true;   // silent, same reasoning as Once above
                // A failed save must not revoke the grant just given — the user already said yes,
                // and this run should honour that even if persistence for NEXT time didn't stick.
                try
                {
                    _store.Add(_workingDir, request.Kind, request.AlwaysRule);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _sink?.ShowSystemMessage($"[yellow]could not save this rule for next time: {ex.Message}[/]");
                }
                // Silent on success. The rule IS visible — Settings → Permissions lists every stored
                // rule for this folder — so this is discoverable rather than invisible, without a line
                // per grant in the conversation.
                return true;

            case PermissionChoice.TrustFolder:
                try
                {
                    _store.SetTrust(_workingDir, TrustState.Trusted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _sink?.ShowSystemMessage($"[yellow]could not save folder trust for next time: {ex.Message}[/]");
                }
                return true;   // silent; the trust state is shown on Settings → Permissions

            case PermissionChoice.Deny:
            default:
                _sink?.ShowSystemMessage($"[red]denied: {request.Display}[/]");
                return false;
        }
    }
}
