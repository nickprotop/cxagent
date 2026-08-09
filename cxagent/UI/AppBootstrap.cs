using CxAgent.Core.Llm;
using CxAgent.Core.Models;
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
/// AgentHost (only when a provider is configured), runs the loop, and disposes resources.
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

        // THE RESUME BUFFER. Built before the runner so it can be handed in at construction, and
        // pruned once here rather than on a timer: startup is the only moment nothing is mid-turn,
        // and finished sessions are the only rows old enough to be worth dropping.
        var sessions = new SqliteSessionStore(paths);
        sessions.Prune(SqliteSessionStore.DefaultRetention);

        var mainWindow = new MainWindow(system, resolution, logs)
        {
            ConfiguredMaxWorkerTurns = ReadConfiguredMaxWorkerTurns(paths),
        };
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
        AgentHost? runner = null;
        ILlmProvider? activeProvider = resolution.Provider;

        // The currently-open consolidated Settings dialog, or null when none is open. Captured by the
        // Escape global shortcut (routes Escape to Cancel while a dialog is open) and by
        // OpenSettingsAsync (reentrancy: a second F5/F7/F8 press selects a page in this instance rather
        // than opening a second dialog). Cleared in OpenSettingsAsync's `finally` — see its comment.
        SettingsDialog? openDialog = null;

        // Rebuilt on every WireRunner call rather than fixed at startup: an F7 role rebinding (or a
        // catalog change via F5/F8) must produce a registry carrying the NEW resolution. A single
        // startup registry would keep dispatching through the bindings that existed at launch. Seeded
        // here so there is a registry before the first wire — nothing reads it between here and then.
        var plugins = PluginRegistry.CreateWithBuiltins(resolution.Providers, permissionGate);

        // A crashed session waiting to be picked up, until the user answers. Consumed ONCE by the
        // next WireRunner and cleared, so an F5 provider swap later in the session does not silently
        // re-restore a context the user has already moved past.
        SessionSnapshot? pendingResume = null;

        void WireRunner(ProviderResolution res)
        {
            if (!res.HasProvider) return;
            activeProvider = res.Provider;
            // Rebuilt from THIS resolution's roles so an F7 rebinding takes effect in this session.
            // The new AgentHost below reads this field, not a startup copy.
            plugins = PluginRegistry.CreateWithBuiltins(res.Providers, permissionGate);
            // F5 rewiring mid-session replaces `runner` with a fresh AgentHost — dispose the
            // outgoing one rather than leaking it for the rest of the process's lifetime.
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
            // the entire switch: AgentHost never touches a control.
            // The failed-job buttons. Delegates rather than a AgentHost reference because `runner`
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
            // real: AgentHost takes OrchestratorSettings? and defaults to unbounded, so omitting it
            // here silently disabled cost control in production while every unit test still passed.
            runner = new AgentHost(res.Provider!, sink, jobPanelSink, plugins, logs,
                orchestrator: res.Orchestrator,
                // P11 Task 2: the real window (when config told us one), so auto-compression derives
                // its threshold from actual headroom instead of always falling back to the fixed
                // constant. Null on --mock/no-provider and whenever contextWindow isn't configured.
                contextWindow: res.ContextWindow,
                // Every completed turn lands here, so a crash leaves something to resume from.
                store: sessions,
                // OUR config folder, so a user-level AGENTS.md applies wherever they work.
                globalInstructionsDir: paths.ConfigDir,
                resume: System.Threading.Interlocked.Exchange(ref pendingResume, null))
            {
                // The user's OWN value, or null. res.Orchestrator is null exactly when the config
                // said nothing — the Unbounded placeholder substituted elsewhere would report 200
                // and make "unconfigured" indistinguishable from "configured to 200".
                // Read the RAW JSON, not the settings record. ProviderConfig.Orchestrator is a
                // non-nullable property that shadows its own nullable parameter and defaults to
                // Unbounded (ProviderConfig.cs:140) — so it is NEVER null, every `?? ` against it is
                // dead code, and "the user configured 200" is indistinguishable from "the config
                // said nothing" at every layer above the file itself.
                ConfiguredMaxWorkerTurns = ReadConfiguredMaxWorkerTurns(paths),
            };
            runner.TokensUpdated += (_, total) => system.EnqueueOnUIThread(() => mainWindow.SetTokenTotal(total));
            runner.ContextUsedUpdated += (_, used) => system.EnqueueOnUIThread(() => mainWindow.SetContextUsed(used));
            runner.ContextCompressed += (_, d) => system.EnqueueOnUIThread(() => mainWindow.MarkContextStale(d.Before, d.After));
            runner.ContextEstimatedUpdated += (_, used) => system.EnqueueOnUIThread(() => mainWindow.SetContextUsed(used, estimated: true));
            // ONCE, AT WIRE-UP. The agent's id is fixed for its life, so there is nothing to wait for
            // and nothing to re-raise — this used to be a GoalStarted subscription that fired on every
            // prompt because every prompt minted a new id.
            mainWindow.SessionId = runner.SessionId;
            mainWindow.RefreshSessionPanel();
            runner.TurnCompleted += (_, calls) => system.EnqueueOnUIThread(() =>
            {
                mainWindow.SessionPanel.RecordTurn(calls);
                mainWindow.SetTokenSplit(runner.Ledger.InputTokens, runner.Ledger.OutputTokens);
                mainWindow.RefreshSessionPanel();
            });
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

        // Submit model: plain Enter SUBMITS, and a line ending in a BACKSLASH continues onto the
        // next one — the shell's own convention, and Claude Code's.
        //
        // Shift+Enter was the first answer and it does not work here. The reasoning was that it is
        // "the chat-UI convention, portable across terminals", but that is a GUI assumption: most
        // Unix terminals send a bare '\r' for Enter with no modifier bits at all, so Shift+Enter and
        // Enter are the same byte and the app cannot tell them apart. It was documented in three
        // places and reachable in none. (Ctrl+Enter fails for exactly the same reason, which the
        // original comment already noted without following the observation through.)
        //
        // A trailing '\' needs no modifier to survive, which is the whole point: it is IN THE TEXT.
        // Every terminal delivers it, and any user who has continued a shell command already knows
        // it. We intercept Enter in PreviewKeyPressed (fires BEFORE the focused control) and set
        // e.Handled so the MultilineEditControl never inserts its own newline; we insert the newline
        // ourselves when the text ends in a backslash. Gated to when the composer has focus, so Enter in the job panel (expand
        // block) still works. Registered UNCONDITIONALLY (not just when a provider is configured at
        // startup) because it closes over the `runner` field above, so a provider wired in later via
        // first-run setup or F5 settings becomes usable without re-registering anything.
        // THE SLASH MENU. Its keys are handled by the portal's own content, NOT here: an open
        // desktop portal captures keyboard input before PreviewKeyPressed is reached, so a hook in
        // this handler would never fire while the menu is up. See CommandMenuContent.
        var commandMenu = new CommandMenu(system, window, mainWindow.Input) { Composer = mainWindow.Input };
        commandMenu.Chosen += (_, cmd) =>
        {
            // Choosing fills the composer rather than dispatching. The command may take arguments,
            // and a menu that ran on selection would make "/compress" unreachable-with-an-argument
            // and give the user no chance to change their mind. One more Enter runs it.
            mainWindow.Input.Input = cmd.Name;
        };
        mainWindow.Input.InputChanged += (_, text) => commandMenu.Sync(text);

        window.PreviewKeyPressed += (_, e) =>
        {

            if (e.KeyInfo.Key != ConsoleKey.Enter) return;
            if (!mainWindow.Input.HasFocus) return;   // let the job panel etc. handle Enter when focused there

            // A LINE ENDING IN '\' CONTINUES. The backslash is consumed — it is punctuation for the
            // editor, not part of the goal — and replaced by the newline it asked for. Trailing
            // whitespace is ignored when looking for it, because a stray space after the backslash is
            // invisible and would otherwise silently submit the goal instead of continuing it.
            if (ComposerContinuation(mainWindow.Input.Input) is { } continued)
            {
                // The caret follows on its own: PromptControl's Input setter puts it at the end of
                // the new value. MultilineEditControl left it at 0,0, so everything typed after a
                // continuation was inserted at the START — "first line \" + Enter + "second line"
                // produced "second linefirst line", and it needed an explicit cursor move here.
                mainWindow.Input.Input = continued;

                e.Handled = true;
                return;
            }

            // Plain Enter → submit.
            e.Handled = true;   // consume it so the MLE doesn't also insert a newline
            if (runner is null || !mainWindow.SubmissionEnabled) return;
            var goalText = mainWindow.Input.Input;
            if (string.IsNullOrWhiteSpace(goalText)) return;
            // RECORD IT FOR ↑/↓ OURSELVES. PromptControl records history inside its own Submit(),
            // which this handler pre-empts — we consume Enter before the control sees it, so nothing
            // would ever reach the history and the feature would be silently dead.
            mainWindow.Input.RecordHistory(goalText);

            mainWindow.Input.Input = "";   // clear the composer for the next goal
            mainWindow.RetireComposerPlaceholder();

            // ONE DISPATCH, DRIVEN BY THE OUTCOME. This was three ordered checks — IsCompress, then a
            // Match whose Quit case was an outcome and whose /help case was a NAME comparison, then
            // TryHandle — and the order between them was load-bearing without saying so. Adding a
            // command meant finding the right rung. Now the command's own outcome says who services
            // it, which is also what lets a menu dispatch a chosen row through this same path.
            if (SessionCommands.Match(goalText) is { } command)
            {
                switch (command.Outcome)
                {
                    case CommandOutcome.Quit:
                        cts.Cancel();
                        system.Shutdown();
                        return;

                    case CommandOutcome.NeedsWindow:
                        mainWindow.ShowHelp();
                        return;

                    case CommandOutcome.NeedsProvider:
                        // /compress means COMPRESS — it summarises through the model, exactly as
                        // auto-compression does, rather than deleting the oldest half. Truncation
                        // survives only as the fallback when that call fails. It is out here rather
                        // than in SessionCommands because that type is synchronous and provider-free
                        // by design — which is what keeps it testable without a window.
                        // THROUGH THE RUNNER, which owns the provider, the ledger and the job panel.
                        // This used to call the compressor directly and, having no job panel, could
                        // only print one line of prose once the work was over — so a /compress looked
                        // like nothing happening for several seconds, then a sentence. It now draws
                        // the same spinner row, with the same expandable summary, as the two automatic
                        // routes.
                        if (runner is not null)
                            _ = runner.CompressNowAsync(cts.Token);
                        return;
                }
            }

            // Everything else that begins with a slash — /clear, and any unrecognised command, which
            // gets the "available commands" reply rather than being sent to the model as a task.
            // Costs nothing: no goal, no provider call, no tokens.
            if (SessionCommands.TryHandle(goalText, conversation, out var commandReply))
            {
                // /clear MUST CLEAR BOTH LISTS. SessionCommands only sees the session conversation —
                // the transcript's record — while what the MODEL carries is the agent's context, and
                // that now persists across goals. Clearing one and not the other would empty the
                // screen while the agent silently remembered everything, which is the opposite of
                // what the command promises.
                if (SessionCommands.Match(goalText)?.Name == "/clear")
                {
                    runner?.Context.Clear();
                    mainWindow.SetContextUsed(0);
                }

                mainWindow.Chat.AddMessage(ChatRole.System, commandReply);
                return;
            }

            // Fire-and-forget on the UI-initiated flow; sync-context resumes continuations on the UI thread.
            // Retire the hint HERE, at submission — not when tokens first arrive. Tied to the token
            // readout it stayed on screen for the whole of a running request, telling the user to type
            // a prompt while the agent was several tool calls into one.
            mainWindow.RetireComposerHint();

            _ = runner.SendAsync(goalText, conversation, cts.Token);
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
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F3, mainWindow.ToggleSessionPanel);
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F1, mainWindow.ShowHelp);
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F5, () => { _ = mainWindow.ShowSettings?.Invoke(); });
        // F2, F4 and F6 are GONE, and F6 was DEAD CODE the whole time.
        //
        // F6 diagnosed "whatever job has focus", resolved through FocusedJobId() — which walks the
        // focus path for a JobBlockControl. Those are created only by JobPanelControl, and the job
        // panel is never placed in the grid: jobs render INLINE in the transcript. So the lookup
        // always returned null and the key was a no-op in every mode, while Help advertised it. The
        // same flow is still reachable from the Diagnose button on a failed job's own block, which
        // addresses its job by id rather than by focus.
        //
        // F2 cleared the composer and refocused it; F4 focused the composer. Both were routes back
        // from a focus that can no longer happen — the job panel they existed to return from is gone,
        // and the composer now keeps focus across submits (UnfocusOnEnter = false). Clearing is
        // Ctrl+U, and history on the up-arrow made "empty the box" the rarer intent anyway.
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
        // AgentHost WireRunner last installed. Both ApproveDraft/DiscardDraft are synchronous and
        // self-guard to a no-op when nothing is currently drafting (AgentHost.cs:162/174) — no
        // `runner.HasPendingApproval` pre-check needed here, and none of the other handlers in this
        // block pre-check their own preconditions either (F6 DiagnoseFocusedJob is the same shape).
        // Esc, not another F-key: this codebase has no OTHER Esc binding anywhere (grepped before
        // choosing it), so it's free, and Esc-to-cancel/dismiss is the universal convention — a
        // second F-key would be one more thing to memorize for no reason.
        // Escape reaches `openDialog.Cancel()` only from here: this global fires at
        // InputCoordinator.cs:131, well before active-window routing at :150, so a dialog window can
        // never see Escape itself. `openDialog` is cleared in OpenSettingsAsync's `finally`, so once
        // the dialog closes Escape does nothing again — it used to fall through to DiscardDraft,
        // which was a no-op from the moment the copilot draft gate was deleted.
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.Escape,
            () =>
            {
                // Routed through EscapeRouting.For so the DECISION is unit-testable; the action
                // itself (which needs a live dialog) stays here.
                if (EscapeRouting.For(openDialog is not null) == EscapeTarget.CancelDialog)
                    openDialog!.Cancel();
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
        // Seed the panel on the first pump, for the same reason as the hint above: it adds controls,
        // and doing that during BuildWindow joins a render tick that does not exist yet.
        system.EnqueueOnUIThread(() =>
        {
            // The panel is responsive in BOTH senses — whether it shows at all, and how wide it is —
            // so a resize has to re-run the same decision that startup did.
            system.WindowResized += (_, _) => system.EnqueueOnUIThread(mainWindow.RefreshSessionPanel);

            mainWindow.SetPermissionRuleCount(
                permissionRules.RulesFor(Directory.GetCurrentDirectory()).Rules.Count);
            mainWindow.RefreshSessionPanel();

            // NEVER RESUME SILENTLY. A context the user did not ask for is one they cannot account
            // for, and it is paid for on the very first turn — so this asks, on the first pump, for
            // the same reason everything else here is deferred: a dialog needs a render tick to join.
            _ = OfferResumeAsync();
        });

        async Task OfferResumeAsync()
        {
            var snapshot = sessions.LoadLatestUnfinished();
            if (snapshot is null) return;

            // ENOUGH TO RECOGNISE IT BY. A ULID identifies a session but describes nothing; the size
            // and the age are what tell someone whether this is the work they were in the middle of.
            var age = DateTimeOffset.UtcNow - snapshot.UpdatedAt;
            var when = age.TotalMinutes < 1 ? "just now"
                     : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                     : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                     : $"{(int)age.TotalDays}d ago";

            const string resume = "Resume it";
            const string fresh = "Start fresh";

            var choice = await FlowDialogs.ChooseAsync(system, mainWindow.Window,
                $"An earlier session ended without closing ({snapshot.Context.Count} messages, "
                + $"last active {when}). Resume it?",
                [resume, fresh], cts.Token);

            if (choice == resume)
            {
                pendingResume = snapshot;
                WireRunner(resolution);   // rebuilds the runner over the restored context

                // RETIRE THE ROW IT CAME FROM. The resumed session is a NEW agent with a new id
                // writing its own rows, so leaving the old one unfinished would offer the same
                // crashed context again at every launch — and accepting it twice would fork the
                // conversation into two sessions claiming the same history.
                sessions.MarkFinished(snapshot.AgentId);

                // SAY SO IN THE TRANSCRIPT. The restored turns are not rendered — they are the
                // model's memory, not this session's scrollback — so without a line here the user
                // faces an empty screen and an agent that mysteriously already knows things.
                permissionSink.ShowSystemMessage(
                    $"[yellow]Resumed an earlier session: {snapshot.Context.Count} messages restored. "
                    + "They are not shown above, but the agent remembers them.[/]");
            }
            else if (choice == fresh)
            {
                // Retired explicitly, so it stops being offered on every launch from here on.
                sessions.MarkFinished(snapshot.AgentId);
            }
            // Dismissed: left alone, and offered again next launch. Declining to answer is not the
            // same as declining the session.
        }

        int code = system.Run();

        // ENDED PROPERLY, so do not offer it back. Reaching this line is the only evidence available
        // that the process was not killed mid-session — which is precisely what makes an unfinished
        // row mean something.
        runner?.MarkSessionFinished();
        // I1 #1: AgentHost.Dispose releases EVERY scheduler this session's runner ever created (each
        // one's CancellationTokenSource + two SemaphoreSlims) — without this they leak for the rest of
        // the process's lifetime, since (as of review round 2's N2 fix) AgentHost no longer disposes
        // schedulers one-at-a-time as goals swap; this is now the only release point.
        mainWindow.Dispose();   // stops the panel clock
        runner?.Dispose();
        return code;
    }

    /// <summary>
    /// Runs the setup wizard (first-run launch or F5 settings), persists the result, and rewires the
    /// live AgentHost so the same session becomes usable immediately — no restart. On cancel (null
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

    /// <summary>
    /// <c>orchestrator.maxWorkerTurns</c> exactly as the user wrote it, or null when absent.
    ///
    /// <para>Goes to the FILE because the settings record cannot answer the question: its
    /// Orchestrator property is non-nullable and falls back to Unbounded, which carries
    /// MaxWorkerTurns = 200 from the record's own default. Single-agent needs "nobody said" and
    /// "somebody said 200" to be different, and only the raw JSON still knows.</para>
    /// </summary>
    private static int? ReadConfiguredMaxWorkerTurns(AppPaths paths)
    {
        try
        {
            var path = Path.Combine(paths.ConfigDir, "config.json");
            if (!File.Exists(path)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("orchestrator", out var o)
                && o.TryGetProperty("maxWorkerTurns", out var v)
                && v.TryGetInt32(out var turns)
                    ? turns
                    : null;
        }
        catch (Exception)
        {
            return null;   // unreadable config is the loader's problem to report, not this one's
        }
    }

    /// <summary>
    /// The composer's line-continuation rule: the text to put back, or null to SUBMIT.
    ///
    /// <para>Extracted from the key handler because the rule is pure string logic and the handler
    /// needs a live window, a focused control and a key event to reach it — a lot of scaffolding for
    /// "does this end in a backslash". Public rather than internal because this assembly grants no
    /// InternalsVisibleTo, and the ForTest suffix follows the seam convention used elsewhere here.</para>
    /// </summary>
    public static string? ComposerContinuationForTest(string? typed) => ComposerContinuation(typed);

    private static string? ComposerContinuation(string? typed)
    {
        var text = (typed ?? "").TrimEnd();

        // TRAILING WHITESPACE IS IGNORED when looking for the backslash. A stray space after it
        // is invisible, and without this the goal would submit instead of continuing — a
        // difference the user cannot see and cannot undo.
        if (!text.EndsWith('\\')) return null;

        // AN ESCAPED BACKSLASH IS NOT A CONTINUATION. "C:\\path\\\\" ends in a literal
        // backslash the user typed on purpose; only an ODD number of them is the line-continuation
        // marker, exactly as a shell reads it.
        var run = 0;
        for (var i = text.Length - 1; i >= 0 && text[i] == '\\'; i--) run++;
        if (run % 2 == 0) return null;

        // The marker is punctuation for the editor, not part of the goal, so it is consumed.
        return text[..^1] + "\n";
    }
}
