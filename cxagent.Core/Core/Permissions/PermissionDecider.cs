using CxAgent.Core.Commands;
using CxAgent.Core.Sessions;
using CxAgent.Core.Permissions;

namespace CxAgent.Core.Permissions;


/// <summary>One permission decision, as reported for telemetry.</summary>
/// <param name="Kind">Which permission kind it was.</param>
/// <param name="Decision">How it was resolved — allowed, denied, silent, auto-allowed, auto-refused, auto-denied.</param>
/// <param name="Requester">Who asked, or null for the session's own agent.</param>
/// <param name="Subject">What the decision was about — the command, the path, the origin.</param>
/// <param name="Flagged">
/// WHETHER TRIAGE SENT THIS ACTION TO STAGE TWO before the classifier reached its verdict. False for
/// every decision that never touched the classifier (silent, human-decided) and for a triage ALLOW
/// that never left stage one. True on any of the three auto-* decisions where stage one did NOT
/// clearly allow — including an auto-allowed that only got there because stage two reversed a triage
/// flag back to Allow, which is a flagged decision that still ended in "allowed". This is the field
/// the triage-flag-rate in <c>/stats</c> is computed from; see
/// <see cref="ClassifierDecision.Flagged"/> for why it rides the decision itself.
/// </param>
public sealed record PermissionDecisionReport(
    PermissionKind Kind, string Decision, string? Requester, string? Subject, bool Flagged = false);

/// <summary>
/// The real, interactive <see cref="IPermissionGate"/>: consults <see cref="PermissionPolicy"/>
/// for the silent classes (in-boundary-and-trusted file ops, stored "Always" rules), and for
/// everything else marshals onto the UI thread to show a <c>PermissionPromptControl</c> in
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
public sealed class PermissionDecider : IPermissionGate
{
    private readonly PermissionRulesStore _store;
    private readonly Action<Message>? _notice;

    /// <summary>
    /// The reviewer for <c>/mode edits auto</c>, or null when none is configured — in which case auto
    /// is not reachable at all and this is never consulted.
    ///
    /// <para>A PROPERTY, NOT A SIXTH CONSTRUCTOR PARAMETER. The constructor already takes five, and
    /// the classifier is optional session wiring rather than something the gate cannot exist without.
    /// Set once at startup beside the policy.</para>
    /// </summary>
    public ActionClassifier? Classifier { get; set; }

    /// <summary>
    /// Binds the classifier this resolution describes, or clears it when it describes none.
    ///
    /// <para>CALLED ON EVERY RE-WIRE, not once at startup. Bound once, changing `classifier` in config
    /// and pressing F5 leaves `auto` mode consulting the SUPERSEDED provider — silently, because the
    /// mode still works. Clearing matters as much: removing the entry and re-wiring has to turn the
    /// mode off, or a mode stays alive that config no longer describes.</para>
    ///
    /// <para>Here rather than in the composition root so the rule is one line at each call site and
    /// cannot be half-applied — a re-wire spread across the root moves some consumers of a resolution
    /// and not others.</para>
    /// </summary>
    public void BindClassifier(string? instanceName, Llm.ProviderRegistry? providers) =>
        Classifier =
            instanceName is { Length: > 0 } name
            && providers is not null
            && providers.TryGet(name, out var provider)
                ? new ActionClassifier(provider)
                : null;

    // REPORTED ONCE PER TURN, not per action. A provider that is down would otherwise put an
    // identical yellow line beside every gated write in a shell-heavy turn — the same noise that
    // removing the per-allow echo fixed. Reset by Session.RunTurnAsync at the top of every lap
    // (Session.Turn.cs) — a type test against PermissionDecider there, since this reset is state
    // specific to this one gate implementation, not a concept IPermissionGate itself carries.
    private bool _reportedClassifierFailure;

    /// <summary>Lets a new turn report a classifier failure again — the fact is stale once the turn
    /// that observed it is over, and a session that stays quiet forever after one blip would hide a
    /// provider that never came back.</summary>
    public void ResetTurnState()
    {
        _reportedClassifierFailure = false;
        // KEPT IN LOCKSTEP WITH THE FAILURE FLAG ABOVE, not reset by a second call site — a cached
        // verdict answers for one action, not a standing rule, and must not outlive the turn it was
        // computed for. Session.RunTurnAsync calls this exact method at the top of every lap, so the
        // classifier's cache is cleared on the same beat as the failure flag, with nothing new for
        // that call site to remember to wire up separately.
        Classifier?.ResetTurnState();
    }

    // The UI seam: shows a prompt for `request` (offerTrust decides whether the fourth "Trust
    // this folder" button appears) and completes with the user's choice. The real constructor
    // implements this by building ONE PermissionPromptControl, calling BuildContent() exactly
    // once, holding that IWindowControl reference, and passing the SAME reference to both
    // MainWindow.ShowPermissionPrompt and RestoreComposer — GridControl.ReplaceControl matches by
    // ReferenceEquals (GridControl.cs:389), so a second BuildContent() call would throw from
    // inside the render loop. Tests implement this with a scripted TCS instead of a live window —
    // the same trick ISessionObserver plays for AgentHost.
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


    /// <summary>
    /// A gate over this rules store, asking through <paramref name="promptHook"/>.
    ///
    /// <para>THE HOOK IS THE ONLY THING A FRONT END SUPPLIES. Everything a decision needs — the
    /// silent path, stored rules, the auto classifier and its fail-closed behaviour, the trust
    /// floor, persistence — is here. A terminal passes a function that shows a control; a web front
    /// end passes one that opens a dialog; a test passes a script. None of them reimplement the
    /// pipeline, which is the point of it living in Core.</para>
    /// </summary>
    /// <param name="notice">Where a save failure or an unavailable classifier is reported, or null
    /// to say nothing. Deliberately not an interface: this is one line of text going somewhere, and
    /// a Core type should not know what a transcript is.
    ///
    /// <para>A <see cref="Message"/> rather than a string, so the gate states its own tone and the
    /// front end picks the colour. A gate that pre-styled its text would be the one place Core still
    /// decided how something looked, and every consumer would have had to undo it.</para></param>
    /// <param name="store">Where an "always" answer is remembered.</param>
    /// <param name="promptHook">How a question reaches a human, and how their answer comes back.</param>
    public static PermissionDecider WithPrompt(PermissionRulesStore store, Action<Message>? notice,
        Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook) =>
        new(store, notice, promptHook);

    private PermissionDecider(PermissionRulesStore store,
        Action<Message>? notice, Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook)
    {
        _store = store;
        _notice = notice;
        _promptHook = promptHook;
    }

    /// <summary>Test seam: drives the gate with a scripted prompt function instead of a live
    /// window (the fake-prompt-hook trick the brief specifies). Public rather than internal —
    /// the InternalsVisibleTo grant covers Session's assemble members only — but is not
    /// part of the gate's runtime API surface: production code always uses the UI-wired
    /// constructor above.</summary>
    /// <param name="policy">
    /// Stamped onto every request this gate receives, standing in for
    /// <see cref="PermissionGatedPlugin"/>, which does it in production. The gate itself holds no
    /// policy — that is the point of the split — so a test that builds one and expects the gate to
    /// remember it is testing a shape this type does not have.
    /// </param>
    /// <param name="store">Where an "always" answer is remembered.</param>
    /// <param name="notice">Where a one-line explanation goes, or null to say nothing.</param>
    /// <param name="promptHook">How a question reaches a human, and how their answer comes back.</param>
    public static PermissionDecider ForTesting(PermissionPolicy policy, PermissionRulesStore store,
        Action<Message>? notice,
        Func<PermissionRequest, bool, CancellationToken, Task<PermissionChoice>> promptHook) =>
        new(store, notice, promptHook) { StampForTesting = policy };

    /// <summary>Test-only: the policy to attach to requests that arrive without one. Null in
    /// production, where PermissionGatedPlugin has already stamped them.</summary>
    public PermissionPolicy? StampForTesting { get; init; }

    /// <summary>
    /// Called with every decision this gate makes. Set by the composition root to record history;
    /// null when nothing is listening, which is the case in every test.
    ///
    /// <para>A HOOK RATHER THAN A STORE, for the same reason the loop raises reports instead of
    /// writing rows: a permission gate that knows about a database is a permission gate that can fail
    /// for a reason unrelated to permission.</para>
    /// </summary>
    public Action<PermissionDecisionReport>? OnDecision { get; set; }

    /// <summary>
    /// Called when a request starts WAITING for the user, and again when it stops — true then false,
    /// carrying the requester so a caller can find whose row to mark.
    ///
    /// <para>SEPARATE FROM <see cref="OnDecision"/>, which fires once the answer is known. That is
    /// too late for a row: the interesting interval is precisely the one before a decision exists.
    /// With one agent nobody needed it — blocked and slow look alike and it does not matter. With
    /// three children up, one parked on approval is indistinguishable from one grinding, and the
    /// user cannot tell which row their answer unblocks.</para>
    ///
    /// <para>NOT raised for the silent path. A rule that already allows something never waits, and
    /// flashing a row into "waiting" and out again within the same microsecond would be noise
    /// describing an interval that did not happen.</para>
    /// </summary>
    public Action<string?, bool>? OnWaiting { get; set; }

    /// <summary>
    /// Wraps the decision so EVERY path is recorded — silent allows, stored rules, denials, and the
    /// cancelled-while-queued case. There are six returns inside; recording at each would leave the
    /// next one added silently unrecorded.
    /// </summary>
    public async Task<PermissionOutcome> RequestAsync(PermissionRequest request, CancellationToken ct)
    {
        // THE ASKING SESSION'S POLICY, not this gate's. One gate serves the process — stored rules
        // and the prompt queue have to be shared — but the policy behind it is per session: it holds
        // a working directory and an edit mode, and those belong to one conversation. Captured here,
        // they became shared by accident, so a second session would be judged against the first's
        // root and the first's mode. See PermissionRequest.Policy.
        //
        // NULL FALLS BACK to the gate's own, which is the single-session path and every existing
        // caller.
        // NO FALLBACK. The gate is one per process and holds no session, so a request that arrives
        // without a policy is one nobody can judge: there is no root to check a path against and no
        // edit mode to read. Refusing is the only honest answer — inventing one would decide a
        // question using another session's rules, which is the failure this whole split exists to
        // end. Every production path stamps it (PermissionGatedPlugin); this is the guard for a
        // caller that forgets.
        // The test seam stands in for PermissionGatedPlugin, which stamps this in production.
        if (StampForTesting is not null && request.Policy is null)
            request = request with { Policy = StampForTesting };

        if (request.Policy is not { } policy)
        {
            OnDecision?.Invoke(new(request.Kind, "denied", request.Requester, request.What));
            _notice?.Invoke(new("refused: this request carried no session policy, so there "
                             + "was nothing to judge it against.", Severity.Warning));
            // NOBODY DECIDED THIS — there was no session to ask and no classifier consulted. Still
            // reported as a user denial (DeniedBy left at its "user" default) because that is this
            // gate's existing fail-closed vocabulary for "nothing else fits"; the alternative would
            // invent a third DeniedBy value for a caller-programming-error path no production caller
            // can actually reach.
            return PermissionOutcome.ByUser;
        }

        // The silent path is reported separately: "allowed without asking" and "the user said yes"
        // are different facts, and collapsing them would make a session of stored rules look like a
        // session of decisions.
        if (policy.IsSilentlyAllowed(request))
        {
            OnDecision?.Invoke(new(request.Kind, "silent", request.Requester, request.What));
            return PermissionOutcome.Allow;
        }

        // AUTO MODE'S SECOND OPINION, consulted only for what would otherwise prompt — never to
        // OVERRIDE a decision already made. A stored rule and the boundary pass are settled above; a
        // denial is settled below by the user. This sits in the one gap where nothing has decided yet.
        // TRUST FLOORS AUTO TOO, and this guard is what makes that true. IsSilentlyAllowed holds the
        // floor for the other modes, and reaching this line means it said no — which on an untrusted
        // folder it says for EVERY write. Without a trust check here — it was a direct
        // AllowsSilentWrites call before EffectFor absorbed it — a classifier's ALLOW would return
        // true below and hand auto the one power no mode is allowed to have: widening past a trust
        // decision the user made. Modes narrow, trust bounds — a classifier is still a mode.
        //
        // AND THE GUARD IS NOW AN EFFECT, NOT A BOOL. AllowsSilentWrites answered "may a write here
        // be silent", which happened to be the right question only while writes were the sole
        // reviewed kind. EffectFor answers "what may a verdict CHANGE here", which is the question
        // that stays right as kinds are added: an http_request can be reviewed without an ALLOW on it
        // ever meaning silence. A bool in this position cannot express that — the line below returns
        // true on an ALLOW, so any population a bool admits is a population a bool silences. Trust is
        // still the floor and EffectFor still checks it; see ReviewEffect.
        var effect = policy.EffectFor(request);
        if (effect != ReviewEffect.None && Classifier is not null)
        {
            // TOLD ONLY HERE, inside the branch that actually calls the classifier. Neither the
            // silent path above nor the trust-floor guard above that ever reach this line, so a
            // stored rule or an in-boundary pass never flashes "reviewing…" — the row must show it
            // only for the one path that genuinely goes out to a model. Wrapped so a thrown/awaited
            // failure below still clears it; leaving it stuck true would read as a hang forever.
            request.OnReviewing?.Invoke(true);
            ClassifierDecision decision;
            try
            {
                decision = await Classifier.JudgeAsync(request, ct);
            }
            finally
            {
                request.OnReviewing?.Invoke(false);
            }

            // AN ALLOW ONLY SILENCES WHERE THE EFFECT SAYS IT MAY. This is the whole point of the
            // enum: MayAnnotate reaches here with a verdict and must still fall through to the
            // prompt.
            if (decision.Verdict == ClassifierVerdict.Allow && effect == ReviewEffect.MayApprove)
            {
                // "auto-allowed", NOT "silent". Both let the action through without asking, and
                // recording them the same made the classifier invisible: in one drive's 317 silent
                // decisions there was no way to tell which had cost a model call and which had
                // passed on the boundary alone. It is a real request to a real endpoint on every
                // trusted write, and a reader deciding whether auto is worth its latency needs the
                // count.
                OnDecision?.Invoke(new(request.Kind, "auto-allowed", request.Requester, request.What, decision.Flagged));
                // AutoAllow, not Allow — same effect (the action runs) but the outcome now says WHO
                // decided, which is what lets the tool row badge "auto-approved" (Task 8) rather than
                // looking identical to a silent rule/boundary allow.
                return PermissionOutcome.AutoAllow;
            }

            // AND A DENY REFUSES OUTRIGHT, under the same effect and for the symmetric reason. If a
            // verdict is trusted enough to skip the prompt on an ALLOW it is trusted enough to end
            // the action on a DENY; treating a DENY as merely "ask" would make auto strictly more
            // permissive than the mode it claims to be, since the user is then offered a yes on the
            // one action a reviewer objected to.
            //
            // NOT UNDER MayAnnotate. There the verdict shapes the prompt and decides nothing, in
            // BOTH directions — an annotate-only kind is one where we do not think a model's opinion
            // should be the last word, and that is not a claim about which way it leans.
            if (decision.Verdict == ClassifierVerdict.Deny && effect == ReviewEffect.MayApprove)
            {
                OnDecision?.Invoke(new(request.Kind, "auto-denied", request.Requester, request.What, decision.Flagged));
                request = request with { DeniedByClassifier = true, ClassifierReason = decision.Reason };
                // THE REASON RIDES THE OUTCOME NOW, not only the request field above. That field
                // still exists for the prompt-heading work (Task 8+); this is the return the MODEL
                // actually reads, and it is the one place DenialMessage can tell an auto-denial from
                // a human one.
                return PermissionOutcome.ByClassifier(decision.Reason);
            }

            // EVERYTHING ELSE PROMPTS, and that is every failure too: a timeout, a transport error,
            // an unparseable body all arrive here as Ask (see ClassifierVerdict). Never allow, never
            // deny — a gate that cannot get an answer asks for one.
            //
            // AND THE REFUSAL, which is the one a user actually sees. Reaching here on a trusted
            // folder means the classifier did not clear the action and a prompt is about to appear —
            // the single most useful thing to be able to count, because it is the answer to "why did
            // auto ask me that?" and nothing recorded it.
            OnDecision?.Invoke(new(request.Kind, "auto-refused", request.Requester, request.What, decision.Flagged));

            // AND THE PROMPT IS TOLD WHY, so its heading names the classifier rather than blaming a
            // folder the user very likely trusts — and carries the model's own reason when it gave
            // one, which is the part the user can actually act on.
            request = request with { RefusedByClassifier = true, ClassifierReason = decision.Reason };


            // FAILS CLOSED, OUT LOUD. A gate that quietly falls back looks like a classifier that
            // disagreed, and the user learns nothing — worse, they conclude auto is simply strict.
            // Plain text is this file's vocabulary for "did not work, nothing was denied", the same
            // shape as a rule that could not be saved — a caller receiving the string decides how
            // that tone renders; the gate itself picks no colour for it.
            //
            // ONCE PER TURN, not per action: a shell-heavy turn would otherwise bury the transcript
            // in identical warnings — the same flooding the suppressed per-allow echo below avoids.
            if (Classifier.LastFailure is { } failure && !_reportedClassifierFailure)
            {
                _reportedClassifierFailure = true;
                _notice?.Invoke(new($"auto review unavailable ({failure}) — asking instead", Severity.Warning));
            }
        }

        // WAITING STARTS HERE — before the queue, not after it. A child third in line behind two
        // other prompts is waiting from this moment, and a row that only turned "waiting" once its
        // prompt was actually SHOWN would sit reading "running" through the whole queue.
        OnWaiting?.Invoke(request.Requester, true);
        try
        {
            var granted = await DecideAsync(request, ct);
            OnDecision?.Invoke(new(request.Kind, granted ? "allowed" : "denied", request.Requester, request.What));
            // A HUMAN ANSWERED HERE, always — DecideAsync is only reached once the silent path,
            // auto-allow and auto-deny have all fallen through, so every bool it returns is either
            // the user's own click (Once/Always/TrustFolder/Deny) or a cancellation the gate treats
            // the same as one. ByUser/Allow are correct labels for both, unlike the fallback above.
            return granted ? PermissionOutcome.Allow : PermissionOutcome.ByUser;
        }
        finally
        {
            // IN A FINALLY, because a cancelled-while-queued request never reaches the line above —
            // and a row left permanently "waiting" for an answer nobody will ever be asked for is
            // worse than one that never said it was waiting at all.
            OnWaiting?.Invoke(request.Requester, false);
        }
    }

    private async Task<bool> DecideAsync(PermissionRequest request, CancellationToken ct)
    {

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
            // THE ASKING SESSION'S BOUNDARY, for the same reason the decision path uses it: whether
            // a path is "inside the working directory" depends on WHOSE working directory, and
            // offering to trust a folder on the strength of another session's root would offer the
            // wrong promise.
            // NOT WHEN THE FOLDER IS ALREADY TRUSTED, which is the case that made the button a lie.
            // Reported from a live run: the user had trusted the folder at startup, auto mode's
            // classifier then said ask on a write inside it, and the prompt still offered "Trust
            // this folder" — pressing it would have re-stored trust they already had and changed
            // nothing, because trust was never what refused. The heading already says
            // "auto review said ask?"; the button contradicted it.
            var offerTrust = (request.Kind is PermissionKind.FileRead or PermissionKind.FileWrite)
                && !request.Policy!.FolderTrusted
                && request.Policy!.IsInBoundary(request.Display);

            // `ct` is handed straight to the prompt hook rather than raced against it with a
            // second, gate-local TaskCompletionSource. Racing a LOCAL tcs resolves it to Deny on
            // cancellation and returns, while the real UI prompt — awaiting its OWN Completion,
            // unrelated to that local tcs — sits there forever waiting for a click that will never
            // come: the composer never gets restored and every later permission prompt silently
            // no-ops (MainWindow's idempotence guard). The prompt hook registers cancellation
            // directly on the control's Completion (see AwaitAndRestore), so awaiting it here is
            // both correct AND resolves promptly on cancellation, with no second path to race.
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
                // narrating them back to themselves. Printing a line per allow buries a shell-heavy
                // goal under confirmations of things the user had personally just authorised.
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
                    // THE ASKING SESSION'S FOLDER. A grant belongs to the project it was made in,
                    // and this gate serves every session in the process — filing it under the
                    // gate's own root would grant a permission in a project the user was not
                    // looking at.
                    _store.Add(request.Policy!.Root, request.Kind, request.AlwaysRule);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _notice?.Invoke(new($"could not save this rule for next time: {ex.Message}", Severity.Warning));
                }
                // Silent on success. The rule IS visible — Settings → Permissions lists every stored
                // rule for this folder — so this is discoverable rather than invisible, without a line
                // per grant in the conversation.
                return true;

            case PermissionChoice.TrustFolder:
                try
                {
                    _store.SetTrust(request.Policy!.Root, TrustState.Trusted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _notice?.Invoke(new($"could not save folder trust for next time: {ex.Message}", Severity.Warning));
                }
                return true;   // silent; the trust state is shown on Settings → Permissions

            case PermissionChoice.Deny:
            default:
                _notice?.Invoke(new($"denied: {request.Display}", Severity.Warning));
                return false;
        }
    }
}
