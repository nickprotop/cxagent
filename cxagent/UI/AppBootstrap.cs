using CxAgent.Core.Llm;
using CxAgent.Core.Models;
using CxAgent.Core.Orchestrator;
using CxAgent.Core.Permissions;
using CxAgent.Core.Plugins;
using CxAgent.Core.Storage;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;

namespace CxAgent.UI;

/// <summary>
/// App entry: resolves the provider, builds the system + main window, wires goal submission to
/// GoalRunner (only when a provider is configured), runs the loop, and disposes resources.
/// </summary>
public static class AppBootstrap
{
    public static int Run(string[] args)
    {
        bool useMock = args.Contains("--mock");
        var paths = new AppPaths();
        paths.EnsureCreated();

        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k && k.StartsWith("CXAGENT_"))
            .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""));

        var resolution = ProviderResolver.Resolve(paths, env, useMock);

        var driver = new NetConsoleDriver();
        var system = new ConsoleWindowSystem(driver,
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true, ShowTopPanel: false, ShowBottomPanel: false));

        var logs = new LogFileManager(paths);

        var mainWindow = new MainWindow(system, resolution, logs);
        var window = mainWindow.Build();

        // Task 4: the real interactive gate. workingDir is captured ONCE here — not re-read per
        // F5/F7/F8 re-wire below — because a rule granted in this project must stay scoped to
        // this project for the life of the process (PermissionRulesStore scopes every rule and
        // trust entry by this exact string). rulesStore/policy/gate are likewise built once and
        // reused across every WireRunner call: a fresh store per re-wire would forget every rule
        // and trust decision the user made earlier in the same session — ONE gate instance across
        // re-wires, matching PluginRegistry.CreateWithBuiltins being rebuilt around it below.
        var workingDir = Path.GetFullPath(Environment.CurrentDirectory);
        var permissionRules = new PermissionRulesStore(paths);
        var permissionPolicy = new PermissionPolicy(workingDir, permissionRules);
        // A forwarding sink rather than passing IChatSink directly: the gate is built here, before
        // WireRunner has created the real ChatTranscriptSink (which itself needs `system` and
        // `mainWindow`, already available, but is only constructed inside WireRunner, once per
        // re-wire). LatestChatSink.Current is set at the bottom of WireRunner and read live by the
        // gate on every RequestAsync call, so the echo always lands in whichever transcript sink
        // is CURRENT — never a stale one captured before the first WireRunner ran.
        var permissionSink = new LatestChatSink();
        var permissionGate = new InteractivePermissionGate(system, mainWindow, workingDir,
            permissionPolicy, permissionRules, permissionSink);
        // Guards the LoadError echo below so it is reported once, on the FIRST WireRunner call
        // only — F5/F7/F8 re-wires reuse this same permissionRules instance, and its LoadError
        // describes what happened at construction, not live state, so repeating it on every
        // re-wire would just be noise about an event that already happened and was already told.
        var permissionLoadErrorReported = false;

        var conversation = new List<ChatMessage>();
        using var cts = new CancellationTokenSource();

        // Mutable so first-run setup (and F5 settings) can install a runner that didn't exist at
        // startup. The PreviewKeyPressed handler below closes over THIS FIELD, not over a runner
        // local, so a later assignment takes effect without re-registering the handler. activeProvider
        // tracks alongside it — F6's diagnose closure must call the CURRENT provider, not whichever
        // one was resolved at startup, or a provider change via F5 mid-session would silently keep
        // diagnosing against the old (possibly now-invalid) one.
        GoalRunner? runner = null;
        ILlmProvider? activeProvider = resolution.Provider;

        // The currently-open consolidated Settings dialog, or null when none is open. Captured by the
        // Escape global shortcut (routes Escape to Cancel while a dialog is open) and by
        // OpenSettingsAsync (reentrancy: a second F5/F7/F8 press selects a page in this instance rather
        // than opening a second dialog). Cleared in OpenSettingsAsync's `finally` — see its comment.
        SettingsDialog? openDialog = null;

        // A tool-using worker makes SEVERAL paid provider calls per job, all of them inside the
        // plugin, which reports to no ledger of its own. Without this they are invisible to both the
        // status-bar readout and the goal token budget — the same hole I3 found in JobDiagnoser, one
        // layer down and now multiplied by the turn count.
        //
        // Declared AFTER `runner` and `mainWindow` because it reads them: a local function may be
        // CALLED before its captures are declared but may not REFERENCE them from above. It closes
        // over the variables, not their current (null) values, exactly as onJobFailed and the
        // diagnoser's own onUsage do — hence `runner?.`, which is null only for the window before the
        // first WireRunner call.
        void RecordWorkerUsage(LlmUsage usage)
        {
            runner?.Ledger.Record(usage);
            if (runner is not null)
                system.EnqueueOnUIThread(() => mainWindow.SetTokenTotal(runner.Ledger.TotalTokens));
        }

        // Rebuilt on every WireRunner call rather than fixed at startup: llm_agent closes over the
        // RoleResolver, so a rebinding made in the F7 role editor (or a catalog change via F5/F8) must
        // produce a registry carrying the NEW resolver. A single startup registry would keep
        // dispatching through the bindings that existed at launch. Seeded here so the diagnosis path
        // below has a registry before the first wire — nothing reads it between here and then.
        //
        // ?? Unbounded, not ?? 0: Orchestrator is null on the --mock and no-provider paths, and
        // "Unbounded" names only the TOKEN fields — MaxWorkerTurns still carries its real default
        // there. A 0 cap would make every worker return empty before its first provider call.
        //
        // --fan-out (or orchestrator.fanOut) is what registers llm_agent at all. Off by default: in
        // single-agent mode the orchestrator plans file/shell/http jobs directly and there is no
        // worker type for it to reach for.
        var fanOut = args.Contains("--fan-out")
                     || (resolution.Orchestrator ?? OrchestratorSettings.Unbounded).FanOut;
        var plugins = PluginRegistry.CreateWithBuiltins(resolution.Providers, permissionGate,
            (resolution.Orchestrator ?? OrchestratorSettings.Unbounded).MaxWorkerTurns,
            RecordWorkerUsage, fanOut);

        void WireRunner(ProviderResolution res)
        {
            if (!res.HasProvider) return;
            activeProvider = res.Provider;
            // Rebuilt from THIS resolution's roles so an F7 rebinding takes effect in this session.
            // Both the new GoalRunner below and the diagnosis path read this field, not a startup copy.
            // The worker turn cap and usage sink are re-threaded here too — an F5 settings change that
            // edits orchestrator.maxWorkerTurns must take effect without a restart, and a registry
            // rebuilt without RecordWorkerUsage would silently stop counting worker tokens mid-session.
            plugins = PluginRegistry.CreateWithBuiltins(res.Providers, permissionGate,
                (res.Orchestrator ?? OrchestratorSettings.Unbounded).MaxWorkerTurns, RecordWorkerUsage,
                args.Contains("--fan-out")
                    || (res.Orchestrator ?? OrchestratorSettings.Unbounded).FanOut);
            // F5 rewiring mid-session replaces `runner` with a fresh GoalRunner — dispose the outgoing
            // one (I1 #1), which releases every scheduler IT ever created, rather than leaking them
            // for the rest of the process's lifetime.
            runner?.Dispose();
            var sink = new ChatTranscriptSink(system, mainWindow.Chat);
            // The permission gate is built once, above, before this sink exists — point it at the
            // CURRENT transcript sink so a permission echo always lands in the visible transcript,
            // even after an F5/F7/F8 re-wire replaces it.
            permissionSink.Current = sink;
            // I3: permissionRules.Load ran at construction, before any sink existed to tell the
            // user. Echo a load failure here, once — a bad hand-edit to permissions.json silently
            // dropped every rule and all folder trust, and the user needs to know before they grant
            // anything else (the next grant backs the unreadable file up to permissions.json.bad).
            if (!permissionLoadErrorReported && permissionRules.LoadError is { } loadError)
            {
                permissionSink.ShowSystemMessage($"[yellow]{loadError}[/]");
                permissionLoadErrorReported = true;
            }
            // Jobs render INLINE in the transcript, not in a side panel — one column, jobs
            // interleaved with the turns that caused them. JobPanelSink (and JobPanelControl) still
            // exist and still work; they are simply not wired. Both speak IJobPanel, so this line is
            // the entire switch: GoalRunner never touches a control.
            // The failed-job buttons. Delegates rather than a GoalRunner reference because `runner`
            // is assigned BELOW this line and REPLACED on every re-wire — capturing the instance
            // would pin whichever runner existed when this sink was built. Reading through the
            // closure is the same pattern every other handler here uses.
            //
            // No inline failure actions. Retry/Skip/Diagnose let the user drive the scheduler by
            // hand while the orchestrator was mid-drive -- "a drive operation is already in
            // progress" on screen -- and a hand-skipped job desynchronised the plan from what the
            // orchestrator believed had run. The failure and its reason reach the model on the next
            // consult, which already has a repair round.
            var jobPanelSink = new InlineJobSink(system, mainWindow.Chat);
            // res.Orchestrator carries config.json's token budgets. Passing it is what makes the cap
            // real: GoalRunner takes OrchestratorSettings? and defaults to unbounded, so omitting it
            // here silently disabled cost control in production while every unit test still passed.
            runner = new GoalRunner(res.Provider!, sink, jobPanelSink, plugins, logs,
                orchestrator: res.Orchestrator,
                // onJobFailed is deliberately NOT wired. It used to run the full
                // diagnose→RecoveryFlow-modal→apply flow automatically on every failure, so a failed
                // job interrupted the user with a blocking dialog they never asked for — and now that
                // failed jobs carry inline Retry/Skip/Diagnose buttons, it was a second, louder route
                // to the same three actions. It also spent a model call per failure whether or not the
                // user wanted a diagnosis.
                //
                // Diagnosis is now PULL, not push: press Diagnose on the job (or F6 with it focused)
                // and the same DiagnoseJobAsync flow runs, modal and all. GoalRunner.DrainAutoDiagnosis
                // early-returns on a null delegate (GoalRunner.cs:713), so omitting it is a supported
                // configuration, not a hole.
                // P11 Task 2: the real window (when config told us one), so auto-compression derives
                // its threshold from actual headroom instead of always falling back to the fixed
                // constant. Null on --mock/no-provider and whenever contextWindow isn't configured.
                contextWindow: res.ContextWindow,
                // Single-agent unless fan-out was asked for: one agent with tools, no dag.
                singleAgent: !(args.Contains("--fan-out")
                               || (res.Orchestrator ?? OrchestratorSettings.Unbounded).FanOut));
            runner.TokensUpdated += (_, total) => system.EnqueueOnUIThread(() => mainWindow.SetTokenTotal(total));
            runner.DraftPending += (_, pending) => system.EnqueueOnUIThread(() => mainWindow.SetDraftPending(pending));
            mainWindow.SetSubmissionEnabled(true);
        }

        // Broken role bindings, surfaced where the user will actually see them. A role bound to an
        // instance that was renamed or deleted resolves to the DEFAULT provider silently by design
        // (RoleResolver never throws, so a stale binding cannot take down a goal) — which means without
        // this the user learns nothing until they notice their reviewer jobs ran on the local model and
        // billed the wrong account.
        //
        // Called from WireRunner, not once at startup, so it also fires after an F5/F7/F8 re-wire —
        // deleting an instance in F8 is exactly how a binding becomes broken, and the report belongs at
        // the moment it breaks, not at next launch.
        //
        // On a HEALTHY config BindingWarnings() is empty and this posts NOTHING. Unbound roles are the
        // normal state of every fresh install and are deliberately not reported: a warning that fires
        // for every new user is one they learn to ignore, and then the ones that matter are invisible

        WireRunner(resolution);   // startup path, unchanged in effect

        // F6 — MANUAL diagnosis. Deliberately UNGATED: unlike the automatic post-failure trigger
        // above (RetryCount < MaxRetries), the user is explicitly asking, so this must work on any
        // Failed job regardless of retry headroom — gating it here would make F6 silently do nothing
        // on exactly the exhausted jobs a user most wants explained.
        // Diagnose ONE job by id. Extracted from the F6 handler so the inline Diagnose button on a
        // failed job's message can reach the same flow: the button knows exactly WHICH job it belongs
        // to, and routing it through "whatever is focused" would diagnose the wrong one whenever focus
        // sat elsewhere — which, for a button living in the transcript, is most of the time.
        async Task DiagnoseByIdAsync(string jobId)
        {
            if (runner is null) return;
            // TryGetSession (review round 2, N2), not CurrentDag alone: DiagnoseJobAsync below spans
            // an unbounded await on the recovery dialog. If a SECOND goal starts while that dialog is
            // open, CurrentDag + a later RetryJobAsync call would re-read runner state at THAT point
            // and silently retry through goal 2's scheduler instead of the dag actually being edited —
            // this is C1's exact symptom, recreated one layer up. Capturing the matched (dag, scheduler)
            // pair NOW and driving retry/skip through the CAPTURED scheduler closes that hole; GoalRunner
            // no longer eagerly disposes a scheduler on swap either (see GoalRunner's _allSchedulers doc
            // comment), so this captured reference also stays valid, not just correctly-targeted.
            if (!runner.TryGetSession(out var dag, out var scheduler)) return;
            var job = dag!.TryGet(jobId);
            if (job is null || job.State != JobState.Failed) return;
            if (activeProvider is null) return;

            await DiagnoseJobAsync(job, dag, scheduler!, activeProvider, isManual: true, cts.Token);
        }

        // F6 keeps its meaning — diagnose whatever job has focus — by resolving focus to an id and
        // handing it to the shared flow above.
        mainWindow.DiagnoseFocusedJob = () =>
            mainWindow.FocusedJobId() is { } focused ? DiagnoseByIdAsync(focused) : Task.CompletedTask;

        // The same buttons already exist on every Failed job block (JobBlockControl) and were wired
        // to re-raise as JobPanelControl.DiagnoseRequested/RetryRequested/SkipRequested — but nothing
        // subscribed to them (Task 11 review I4), so pressing the visible on-block button did
        // nothing while F6 on the same job worked. Diagnose is manual/ungated, same as F6 (a block's
        // own Diagnose button is exactly as explicit a user request as the F6 key). Retry/Skip go
        // straight to the scheduler — force: true on Retry for the same reason as F6: a user pressing
        // a visible "Retry" button on an exhausted job must not silently no-op. Retry/Skip from the
        // button are immediate (no intervening dialog), so re-reading via runner.RetryJobAsync/
        // SkipJobAsync at call time is safe here — unlike the dialog-spanning DiagnoseJobAsync flow,
        // there is no unbounded await between "user clicked" and "act", so the N2 hazard doesn't apply.
        mainWindow.JobPanel.DiagnoseRequested += (_, jobId) =>
        {
            if (runner is null || activeProvider is null) return;
            if (!runner.TryGetSession(out var dag, out var scheduler)) return;
            var job = dag!.TryGet(jobId);
            if (job is null || job.State != JobState.Failed) return;
            _ = DiagnoseJobAsync(job, dag, scheduler!, activeProvider, isManual: true, cts.Token);
        };
        mainWindow.JobPanel.RetryRequested += (_, jobId) => _ = RetryLatestSessionWithReportAsync(jobId, force: true);
        mainWindow.JobPanel.SkipRequested += (_, jobId) => _ = SkipLatestSessionWithReportAsync(jobId);

        // Shared diagnose → confirm → apply flow for the automatic post-failure trigger, F6, and the
        // block-level Diagnose button. Best-effort throughout: DiagnoseJobAsync never throws and
        // returns null on any failure, in which case this says so plainly in chat and leaves the job
        // Failed — it must never hang and never take down the goal (diagnosis is an enhancement, not
        // an execution step). Takes the CAPTURED scheduler (review round 2, N2) rather than re-reading
        // runner.CurrentDag/RetryJobAsync after the dialog await below — see the F6 handler's comment.
        async Task DiagnoseJobAsync(Job job, JobDag dag, DagScheduler scheduler, ILlmProvider provider, bool isManual, CancellationToken diagCt)
        {
            // I3: diagnosis rounds spend real provider tokens that were previously recorded nowhere,
            // invisible to both the status-bar readout and the goal token budget. Route them into the
            // SAME ledger a goal's own planning call uses, and refresh the status bar immediately —
            // this bypasses GoalRunner's own TokensUpdated raise point (diagnosis isn't part of a
            // RunCoreAsync call), so the UI push has to happen here instead.
            var diagnoser = new JobDiagnoser(provider, plugins, logs, onUsage: usage =>
            {
                runner?.Ledger.Record(usage);
                if (runner is not null)
                    system.EnqueueOnUIThread(() => mainWindow.SetTokenTotal(runner.Ledger.TotalTokens));
            });
            var diagnosis = await diagnoser.DiagnoseJobAsync(job, dag, diagCt);
            if (diagnosis is null)
            {
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        $"[yellow]Diagnosis unavailable for '{job.DisplayName}' — leaving it Failed.[/]"));
                return;
            }

            // The unbounded await the whole N2 hazard is about: the user can take arbitrarily long to
            // respond, or submit a brand new goal in the meantime. Everything AFTER this line must act
            // on the `scheduler`/`dag` parameters captured before this await, never re-read runner state.
            var chosen = await RecoveryFlow.RunAsync(system, mainWindow.Window, diagnosis, diagCt);
            if (chosen is null) return;   // user declined — do not touch the dag

            if (chosen == RecoveryAction.Skip)
            {
                await SkipWithReportAsync(job.Id, scheduler);
                return;
            }
            if (chosen == RecoveryAction.AskUser)
                return;   // the model explicitly declined to decide; nothing to apply

            var mod = diagnosis.Modification ?? DagModification.Empty;
            if (!DagModifier.TryApply(dag, mod, insertBeforeJobId: job.Id, out var error))
            {
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System, $"[red]Could not apply recovery: {error}[/]"));
                return;
            }

            // I2: TryApply may have added new jobs (InsertBefore) that JobPanelControl has never seen
            // — SetJobs only ran once, at plan compile. Re-syncing here means the inserted job actually
            // paints and updates as it runs, instead of executing invisibly while the failed block
            // just sits there.
            if (mod.JobsToAdd.Count > 0)
                system.EnqueueOnUIThread(() => mainWindow.JobPanel.SetJobs(dag.AllJobs));

            // C1: manual diagnosis (isManual — F6 or the block's own Diagnose button) must retry an
            // exhausted job for real, not silently no-op at DagScheduler's RetryCount>=MaxRetries
            // guard AFTER TryApply has already mutated the live dag above. Automatic diagnosis is
            // never manual, so it stays subject to the normal cap.
            await RetryWithReportAsync(job.Id, scheduler, force: isManual);
        }

        // C1 + "never silent": both wrap the scheduler call and report a false ("nothing happened")
        // result as a chat message instead of letting it disappear — a false return after TryApply
        // has already mutated the dag would otherwise leave it edited but unexecuted with no sign
        // anything went wrong. Operates on the given scheduler directly (review round 2, N2) — the
        // caller (DiagnoseJobAsync) has already captured it before its own dialog await, so this must
        // NOT re-read runner._currentScheduler, which could by now belong to a different goal.
        async Task RetryWithReportAsync(string jobId, DagScheduler scheduler, bool force)
        {
            bool queued = await scheduler.RetryAsync(jobId, force);
            if (!queued)
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        $"[yellow]Could not retry job '{jobId}' — it may no longer be Failed, or it may have used up its retries.[/]"));
        }

        // No-captured-session variant for call sites with no intervening dialog (the block's own
        // Retry button — re-reading runner.RetryJobAsync at call time is safe there; see the
        // DiagnoseRequested handler's comment above). C# local functions can't be overloaded by
        // signature, hence the distinct name rather than a second RetryWithReportAsync.
        async Task RetryLatestSessionWithReportAsync(string jobId, bool force)
        {
            if (runner is null) return;
            bool queued = await runner.RetryJobAsync(jobId, force);
            if (!queued)
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        $"[yellow]Could not retry job '{jobId}' — it may no longer be Failed, or it may have used up its retries.[/]"));
        }

        async Task SkipWithReportAsync(string jobId, DagScheduler scheduler)
        {
            bool skipped = await scheduler.SkipAsync(jobId);
            if (!skipped)
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        $"[yellow]Could not skip job '{jobId}' — it may already be Succeeded or Running.[/]"));
        }

        // No-captured-session variant — see RetryLatestSessionWithReportAsync's comment above.
        async Task SkipLatestSessionWithReportAsync(string jobId)
        {
            if (runner is null) return;
            bool skipped = await runner.SkipJobAsync(jobId);
            if (!skipped)
                system.EnqueueOnUIThread(() =>
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        $"[yellow]Could not skip job '{jobId}' — it may already be Succeeded or Running.[/]"));
        }

        // Submit model: plain Enter SUBMITS, Shift+Enter inserts a newline (the chat-UI convention,
        // portable across terminals). Ctrl+Enter can't be used — most Unix terminals send bare '\r'
        // for both Enter and Ctrl+Enter with no Control modifier, so it's indistinguishable. We
        // intercept Enter in PreviewKeyPressed (fires BEFORE the focused control) and set e.Handled
        // so the MultilineEditControl never inserts its own newline; we insert the newline ourselves
        // on Shift+Enter. Gated to when the composer has focus, so Enter in the job panel (expand
        // block) still works. Registered UNCONDITIONALLY (not just when a provider is configured at
        // startup) because it closes over the `runner` field above, so a provider wired in later via
        // first-run setup or F5 settings becomes usable without re-registering anything.
        window.PreviewKeyPressed += (_, e) =>
        {
            if (e.KeyInfo.Key != ConsoleKey.Enter) return;
            if (!mainWindow.Input.HasFocus) return;   // let the job panel etc. handle Enter when focused there

            if (e.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift))
            {
                // Shift+Enter → newline (the MLE bubbles modified Enter instead of inserting, so we do it).
                mainWindow.Input.Content = (mainWindow.Input.Content ?? "") + "\n";
                e.Handled = true;
                return;
            }

            // Plain Enter → submit.
            e.Handled = true;   // consume it so the MLE doesn't also insert a newline
            if (runner is null || !mainWindow.SubmissionEnabled) return;
            var goalText = mainWindow.Input.Content;
            if (string.IsNullOrWhiteSpace(goalText)) return;
            mainWindow.Input.Content = "";   // clear the composer for the next goal

            // /compress means COMPRESS — it summarises through the model, exactly as auto-compression
            // does, rather than deleting the oldest half. Truncation survives only as the fallback
            // when that call fails. Handled before TryHandle because it is the one command that needs
            // a provider call, and this handler is synchronous.
            if (SessionCommands.IsCompress(goalText))
            {
                var beforeCount = conversation.Count;
                _ = SessionCompressor.CompressAsync(conversation, activeProvider!, cts.Token, usage =>
                    {
                        runner?.Ledger.Record(usage);
                        if (runner is not null)
                            system.EnqueueOnUIThread(() => mainWindow.SetTokenTotal(runner.Ledger.TotalTokens));
                    })
                    .ContinueWith(t => system.EnqueueOnUIThread(() =>
                    {
                        var r = t.IsCompletedSuccessfully ? t.Result : default;
                        mainWindow.Chat.AddMessage(ChatRole.System,
                            conversation.Count < beforeCount
                                ? $"{(r.Summarised ? "Summarised" : "Truncated (summary failed)")}: "
                                  + $"{beforeCount} messages → {conversation.Count}."
                                : "Conversation is already short — nothing to compress.");
                    }), TaskScheduler.Default);
                return;
            }

            // The remaining session commands (/clear) intercept BEFORE RunAsync: they cost nothing —
            // no goal, no provider call, no tokens — and act on `conversation` directly.
            if (SessionCommands.TryHandle(goalText, conversation, out var commandReply))
            {
                mainWindow.Chat.AddMessage(ChatRole.System, commandReply);
                return;
            }

            // Fire-and-forget on the UI-initiated flow; sync-context resumes continuations on the UI thread.
            _ = runner.RunAsync(goalText, conversation, cts.Token);
        };

        // Global shortcuts. FUNCTION KEYS for the pane/goal actions, deliberately — a terminal sends
        // Ctrl+<letter> as a single ASCII control byte, so several Ctrl combos are physically
        // indistinguishable from other keys and can never be bound:
        //   Ctrl+J = 0x0A = LF  → identical to Enter (verified: the handler never fired)
        //   Ctrl+H = 0x08 = BS  → identical to Backspace
        //   Ctrl+M = 0x0D = CR  → identical to Enter
        //   Ctrl+I = 0x09 = TAB → identical to Tab
        // (This is the same class of limitation as the Ctrl+Enter problem noted in e272fac.)
        // Ctrl+N (0x0E) doesn't collide with a key, but the driver's raw reader didn't deliver it
        // either, so it's avoided too. F-keys arrive as escape sequences — unambiguous — and Ctrl+Q
        // (0x11) is proven working, so it keeps the quit binding.
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F2, mainWindow.NewGoal);
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F4, mainWindow.FocusChat);
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F1, mainWindow.ShowHelp);
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F5, () => { _ = mainWindow.ShowSettings?.Invoke(); });
        // F6 — diagnose the focused failed job. Fire-and-forget, same as every other action handler:
        // InstallSynchronizationContext:true makes blocking here a self-deadlock (see cc8b004).
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F6, () => { _ = mainWindow.DiagnoseFocusedJob?.Invoke(); });
        // F7/F8 are GONE. They existed because roles and providers were separate dialogs, and the
        // old comment justified them as "each does one thing and the status bar can name both" —
        // neither is true now. Both opened the SAME consolidated dialog, differing only in which
        // page it landed on, so the status bar advertised three keys for one surface and a user
        // pressing F7 had no way to know F5 and F8 went to the same place.
        //
        // The page they deep-linked to is still one keystroke away INSIDE the dialog (the nav pane
        // is the first focus stop), so nothing became less reachable — the choice just moved from
        // "remember which F-key" to "read the four page names in front of you", which is the point
        // of having a nav pane at all.
        //
        // Deep-linking itself is kept, not deleted: SettingsDialog.RunAsync(SettingsPage, ct) and
        // SelectPage remain, and a future `/settings roles` command or a restore-last-page can use
        // them without re-plumbing anything.
        system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => { cts.Cancel(); system.Shutdown(); });
        // F9 Approve / Esc Discard — copilot mode's (P9) approve-or-discard gate. `runner` is read
        // through the closure (same pattern as every other handler here), so these track whichever
        // GoalRunner WireRunner last installed. Both ApproveDraft/DiscardDraft are synchronous and
        // self-guard to a no-op when nothing is currently drafting (GoalRunner.cs:162/174) — no
        // `runner.HasPendingApproval` pre-check needed here, and none of the other handlers in this
        // block pre-check their own preconditions either (F6 DiagnoseFocusedJob is the same shape).
        // Esc, not another F-key: this codebase has no OTHER Esc binding anywhere (grepped before
        // choosing it), so it's free, and Esc-to-cancel/dismiss is the universal convention — a
        // second F-key would be one more thing to memorize for no reason.
        // Escape must reach `openDialog.Cancel()` BEFORE `runner?.DiscardDraft()` — this global fires
        // at InputCoordinator.cs:131, well before active-window routing at :150, so a dialog window can
        // never see Escape itself; routing it here is the only way it reaches Cancel at all. `openDialog`
        // is cleared in OpenSettingsAsync's `finally`, so once the dialog closes Escape falls straight
        // back through to DiscardDraft — permanently hijacking it here would be a one-way trapdoor.
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.Escape,
            () =>
            {
                // Routed through EscapeRouting.For so the DECISION is unit-testable; the actions
                // themselves (which need a live dialog and a live runner) stay here.
                if (EscapeRouting.For(openDialog is not null) == EscapeTarget.CancelDialog)
                    openDialog!.Cancel();
                else
                    runner?.DiscardDraft();
            });

        // MainWindow stays independent of SettingsDialog/SetupWizard; AppBootstrap supplies the flow
        // via these seams. F5/F7/F8 all route through the ONE consolidated handler below, differing
        // only in which page they land on and (F5 only) whether an absent/invalid config runs the
        // setup wizard instead of opening the dialog — see SettingsEntry.Classify.
        mainWindow.ShowSettings = () => OpenSettingsAsync(SettingsPage.Providers);

        // Holds the currently-open SettingsDialog instance, if any — read by the Escape handler above
        // and by OpenSettingsAsync's reentrancy check just below. Null whenever no dialog is open;
        // OpenSettingsAsync's `finally` is what guarantees that, so Escape is never left pointed at a
        // closed dialog.
        async Task OpenSettingsAsync(SettingsPage page)
        {
            if (openDialog is { } existing)
            {
                // Reentrant F5/F7/F8: select the requested page in the dialog that's already open
                // rather than stacking a second one.
                existing.SelectPage(page);
                return;
            }

            var load = SettingsEntry.LoadSettings(paths, env);
            var route = SettingsEntry.Classify(load);

            if (route == SettingsRoute.RunWizard)
            {
                await RunSetupFlowAsync(system, mainWindow, paths, env, WireRunner, cts.Token);
                return;
            }

            // OpenDialog: build a fresh working copy from what's on disk (Absent → an empty catalog
            // with built-ins seeded, same baseline RoleEditor.FromSettings always gave) and show it.
            // ForLoad, not the plain constructor: it REFUSES an invalid load rather than silently
            // starting from EmptyCatalog(). Classify above already routes invalid -> repair wizard, so
            // this is defence in depth — but the two guards protect different things. Classify decides
            // WHERE to go; ForLoad makes it impossible for a session to exist over a config it would
            // destroy, no matter which entry point built it.
            var session = SettingsSession.ForLoad(load);
            var dialog = new SettingsDialog(system, mainWindow.Window, paths, session, permissionRules, workingDir);
            openDialog = dialog;
            try
            {
                var result = await dialog.RunAsync(page, cts.Token);
                if (result is null) return;   // cancelled, or TryCompose found nothing dirty

                // Re-resolve and re-wire so an edit made here takes effect in THIS session rather than
                // at next launch — exactly one re-wire, gated on a non-null result, matching the
                // "exactly one re-wire on Save, none on Cancel" rule the retired F7/F8 handlers followed.
                WireRunner(ProviderResolver.Resolve(paths, env, useMock: false));
                mainWindow.Chat.AddMessage(ChatRole.System, "Configuration saved.");
            }
            finally
            {
                // MUST run even on an exception or a cancelled RunAsync — otherwise Escape stays
                // pointed at a closed dialog forever (the global handler above checks `openDialog`,
                // not window state).
                openDialog = null;
            }
        }

        // (Task 2.5) The startup trust question: an unclassified folder must be asked about,
        // immediately and blocking, before any goal can run in it. Deferred onto the UI thread for
        // the same reason as the wizard/composer-hint below — ShowPermissionPrompt swaps a control
        // into the composer's grid cell, which needs the render loop system.Run() starts. Runs
        // AFTER the setup wizard completes on the no-provider path (not concurrently — two flows
        // contending for the user at once is worse than sequencing), and immediately otherwise.
        // Fire-and-forget: InstallSynchronizationContext resumes the continuation on the UI thread,
        // so nothing here blocks it; the blocking the user actually experiences is structural (the
        // composer cell is occupied), not a thread block.
        async Task AskTrustIfUnknownAsync()
        {
            if (permissionRules.GetTrust(workingDir) != TrustState.Unknown) return;

            var prompt = new TrustQuestionControl(workingDir);
            var content = prompt.BuildContent();   // built ONCE — see PermissionPromptControl's contract
            mainWindow.ShowPermissionPrompt(content);
            try
            {
                var trusted = await prompt.Completion;
                var state = trusted ? TrustState.Trusted : TrustState.Untrusted;
                try
                {
                    permissionRules.SetTrust(workingDir, state);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    permissionSink.ShowSystemMessage($"[yellow]could not save folder trust: {ex.Message}[/]");
                }
                permissionSink.ShowSystemMessage(trusted
                    ? "[green]trusted this folder[/]"
                    : "[yellow]not trusted — file operations in this folder will ask every time[/]");
            }
            finally
            {
                mainWindow.RestoreComposer(content);
            }
        }

        if (!resolution.HasProvider)
        {
            // Deferred onto the UI thread: the wizard's SwapContentHost needs the message loop that
            // system.Run() starts, so it cannot be awaited inline here. EnqueueOnUIThread runs it on the
            // first pump after Run() begins. Fire-and-forget is correct — InstallSynchronizationContext
            // resumes the continuation on the UI thread; blocking here would self-deadlock.
            //
            // `_ = ...` on the Task, not an async-void lambda: EnqueueOnUIThread takes a plain Action,
            // and an async-void target would let an exception from either await escape as unobservable
            // (the one thing the render loop must never see) instead of surfacing through the Task.
            system.EnqueueOnUIThread(() => _ = RunWizardThenAskTrustAsync());
        }
        else
        {
            system.EnqueueOnUIThread(() => _ = AskTrustIfUnknownAsync());
        }

        async Task RunWizardThenAskTrustAsync()
        {
            await mainWindow.ShowSettings!.Invoke();
            await AskTrustIfUnknownAsync();   // sequenced AFTER the wizard, not concurrently
        }

        // D10: same deferral, same reason. Adding a status-bar item calls Invalidate(Relayout),
        // which is a max-join at the render tick — inside BuildWindow there is no tick to join and
        // it blocks forever. On the first pump after Run() begins, there is.
        system.EnqueueOnUIThread(mainWindow.ShowComposerHint);

        int code = system.Run();
        // I1 #1: GoalRunner.Dispose releases EVERY scheduler this session's runner ever created (each
        // one's CancellationTokenSource + two SemaphoreSlims) — without this they leak for the rest of
        // the process's lifetime, since (as of review round 2's N2 fix) GoalRunner no longer disposes
        // schedulers one-at-a-time as goals swap; this is now the only release point.
        runner?.Dispose();
        return code;
    }

    /// <summary>
    /// Runs the setup wizard (first-run launch or F5 settings), persists the result, and rewires the
    /// live GoalRunner so the same session becomes usable immediately — no restart. On cancel (null
    /// result) or a wizard fault, this is a no-op beyond whatever the wizard itself already reported.
    /// </summary>
    private static async Task RunSetupFlowAsync(
        ConsoleWindowSystem system,
        MainWindow mainWindow,
        AppPaths paths,
        Dictionary<string, string> env,
        Action<ProviderResolution> wireRunner,
        CancellationToken ct)
    {
        // Load what is already configured so the wizard APPENDS. Without this, F5 replaced the whole
        // catalog (single-entry dictionary, empty roles) — destroying every other provider instance
        // and every role binding the user had.
        //
        // An INVALID config is deliberately still passed as null (the wizard starts from empty) rather
        // than refused as F7/F8 do: F5 is the documented way OUT of a broken config — the message both
        // of those print says "press F5" — so blocking it too would leave a user with an unloadable
        // file and no in-app route to repair it. The cost is real (a wizard run over an invalid file
        // rebuilds rather than merges), so it is stated here rather than left to be rediscovered.
        var load = SettingsEntry.LoadSettings(paths, env);
        var existing = load.Settings;
        if (load.IsInvalid)
            mainWindow.Chat.AddMessage(ChatRole.System,
                "[yellow]config.json did not load (" + load.ErrorText
                + "), so setup starts fresh — existing providers and roles in that file will be replaced.[/]");

        var settings = await SetupWizard.RunAsync(system, mainWindow.Window, ct, existing);
        if (settings is null) return;   // user cancelled

        ProviderConfigWriter.Write(paths, settings);
        // useMock: false — a --mock session already has a provider and never reaches first-run setup;
        // F5 re-running setup mid-session should re-resolve against the real config either way.
        var reResolved = ProviderResolver.Resolve(paths, env, useMock: false);
        wireRunner(reResolved);
        mainWindow.Chat.AddMessage(ChatRole.System, reResolved.HasProvider
            ? $"Configuration saved. Provider: {reResolved.DisplayName}. Type a goal and press Enter."
            : "Configuration saved, but it did not load cleanly: " + string.Join("; ", reResolved.Errors));
    }
}
