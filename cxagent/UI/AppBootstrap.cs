using System.Reflection;
using CxAgent.Core.Agent;
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
    /// <summary>
    /// <c>instance:model</c> — how a model is named everywhere the UI shows one.
    ///
    /// <para>Mirrors <c>MainWindow.ModelLabel</c>. Two spellings of the same fact would drift, and
    /// this one is reached from a static path that has no window.</para>
    /// </summary>
    private static string ModelLabelOf(ProviderResolution resolution)
    {
        var model = resolution.Provider?.ModelId;
        if (model is null) return resolution.DisplayName ?? "no provider";

        return resolution.InstanceName is { Length: > 0 } instance ? $"{instance}:{model}" : model;
    }

    /// <summary>
    /// What this build calls itself.
    ///
    /// <para>FROM THE ASSEMBLY, not a constant: the release workflow passes <c>-p:Version</c> at
    /// publish, so a hardcoded string here would be right only until the next tag and wrong
    /// silently thereafter. A local build reports whatever the SDK defaulted to, which is honest —
    /// it is not a release.</para>
    /// </summary>
    private static string Version() =>
        System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            // A '+' suffix is the source-revision the SDK appends; the version is what precedes it.
            ?.Split('+')[0]
        ?? "unknown";

    /// <summary>
    /// The session <c>--resume</c> asked for, or why there is not one.
    ///
    /// <para>STATIC AND OUTSIDE <c>Run</c> so it can be tested: everything else on this path needs a
    /// console driver and a terminal. The two failure modes are kept apart because they call for
    /// different actions — nothing to resume means "just start", while an ambiguous id means "type
    /// more characters", and a single "could not resume" would hide which one happened.</para>
    ///
    /// <para>Public rather than internal: this codebase has no InternalsVisibleTo grant.</para>
    /// </summary>
    public static (SessionSnapshot? Snapshot, string Problem) FindResumeTarget(
        SqliteSessionStore store, string workingDir, string? uid)
    {
        // BARE --resume MEANS THE MOST RECENT UNFINISHED ONE HERE, which is the session the startup
        // offer would have proposed. Scoped to the folder for the same reason it is: restoring
        // another project's conversation fills this one with its files and decisions.
        if (uid is null)
        {
            var latest = store.LoadLatestUnfinished(workingDir);
            return latest is null
                ? (null, "No unfinished session to resume in this folder.")
                : (latest, "");
        }

        var found = store.LoadByUid(uid);

        if (found.IsAmbiguous)
            return (null, $"'{uid}' matches {found.Ambiguous.Count} sessions "
                        + $"({string.Join(", ", found.Ambiguous.Take(4).Select(SessionsCommand.Short))}"
                        + $"{(found.Ambiguous.Count > 4 ? ", …" : "")}) — use more characters.");

        // FOUND BY ID REGARDLESS OF FOLDER, unlike bare --resume. Naming a specific session is an
        // explicit act: someone who copied an id out of `--sessions all` and pasted it here meant
        // that session, and refusing it because the shell is elsewhere would be a rule with no
        // purpose — the folder scope exists to stop an unasked-for session appearing, not to stop
        // a named one being opened.
        return found.Session is null
            ? (null, $"No session matches '{uid}'.")
            : (found.Session, "");
    }

    public static int Run(string[] args)
    {
        var options = CommandLine.Parse(args);

        // A BAD ARGUMENT STOPS THE APP. Ignoring it would start a session that is not the one the
        // user asked for — and `--mode fanout` silently becoming single mode is how someone concludes
        // sub-agents do not work. Written to stderr and returned as a non-zero exit, since at this
        // point there is no window to show anything in.
        if (options.Error is { } error)
        {
            Console.Error.WriteLine($"cxagent: {error}");
            return 2;
        }

        // --version PRINTS AND EXITS, before anything reads config or builds a window. It is the
        // one question you ask a binary you are not sure about, and it must never depend on the app
        // being able to start.
        if (options.ShowVersion)
        {
            Console.WriteLine($"cxagent {Version()}");
            return 0;
        }

        bool useMock = options.UseMock;
        var startupMode = options.Mode;
        var paths = new AppPaths();
        paths.EnsureCreated();

        // --sessions PRINTS AND EXITS, before anything builds a window or resolves a provider.
        // Answering "which conversations do I have here" should not cost a TUI launch, and the ids
        // it prints are meant to be copied into the next command — which is why it goes to stdout as
        // plain tab-separated text rather than through the transcript.
        if (options.ListSessions)
        {
            var listing = new SqliteSessionStore(paths);
            var all = options.ListAllSessions;
            Console.WriteLine(SessionsCommand.RenderPlain(
                listing.List(all ? null : Path.GetFullPath(Environment.CurrentDirectory), all), all));
            return 0;
        }

        var env = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string k && k.StartsWith("CXAGENT_"))
            .ToDictionary(e => (string)e.Key, e => (string)(e.Value ?? ""));

        var resolution = ProviderResolver.Resolve(paths, env, useMock);

        // --model OVERRIDES defaultProvider FOR THIS RUN ONLY, without touching config. Same rule
        // /model follows: naming an instance that is not configured stops rather than falling back,
        // because silently starting on the model the user was trying to avoid is the worst outcome.
        if (options.Instance is { } wanted && !useMock)
        {
            if (ProviderResolver.ResolveInstance(paths, env, wanted) is not { } chosen)
            {
                Console.Error.WriteLine($"cxagent: no provider called '{wanted}' in config.");
                return 2;
            }

            resolution = chosen;
        }

        var driver = new NetConsoleDriver();
        var system = new ConsoleWindowSystem(driver,
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true, ShowTopPanel: false, ShowBottomPanel: false));

        var logs = new LogFileManager(paths);

        // THE RESUME BUFFER. Built before the runner so it can be handed in at construction, and
        // pruned once here rather than on a timer: startup is the only moment nothing is mid-turn,
        // and finished sessions are the only rows old enough to be worth dropping.
        var sessions = new SqliteSessionStore(paths);
        sessions.Prune(SqliteSessionStore.DefaultRetention);

        // USAGE HISTORY — a different file, and NOT pruned. The resume database above is a buffer
        // whose rows are worthless once a session ends cleanly; this is the archive, and pruning an
        // archive on startup would delete the answer to "where did last month go" every time the app
        // opened.
        var history = new UsageHistoryStore(paths);

        var mainWindow = new MainWindow(system, resolution, logs)
        {
            // BEFORE Build(), because the banner it writes is a chat message and cannot be revised.
            // The SetMode call further down still fixes the composer line on every /mode; this is the
            // one readout that has to be right the first time.
            StartupMode = AgentModes.Name(startupMode),
        };
        var window = mainWindow.Build();

        // Task 4: the real interactive gate. workingDir is captured ONCE here — not re-read per
        // F5/F7/F8 re-wire below — because a rule granted in this project must stay scoped to
        // this project for the life of the process (PermissionRulesStore scopes every rule and
        // trust entry by this exact string). rulesStore/policy/gate are likewise built once and
        // reused across every WireRunner call: a fresh store per re-wire would forget every rule
        // and trust decision the user made earlier in the same session — ONE gate instance across
        // re-wires, matching PluginRegistry.CreateWithBuiltins being rebuilt around it below.
        // THE SESSION, as an object rather than as six locals scattered through this method.
        //
        // Every field it holds was already here — host, provider, instance name, plugins, the
        // carried ledger, the pending resume — captured by WireRunner's closure. A local is ONE
        // slot, so a second session would need a second copy of this method; naming the state is
        // what makes a second one possible. The comments below already reasoned in these terms
        // ("owned by the SESSION, not by any one AgentHost") long before there was a type to say it.
        //
        // FIRST, BEFORE THE GATE, so its folder is the ONE source every layer below reads. The gate
        // needs this root string, so a session that also demanded a plugin registry could not be
        // built before the gate that needs the root the session holds — which is why the session
        // takes only its folder and everything else arrives with the first wire.
        //
        // The directory is GIVEN, not read: that is the whole point of the type. It still comes from
        // the process here, because one process runs one session — but every consumer now takes it
        // FROM the session, so a second root is a change to this line rather than to eighteen sites.
        var session = new Core.Agent.Session(Path.GetFullPath(Environment.CurrentDirectory));
        var permissionRules = new PermissionRulesStore(paths);
        var permissionPolicy = new PermissionPolicy(session.WorkingDirectory, permissionRules);
        // The UI's own transcript writer. The control it wraps is created with mainWindow above and
        // never replaced, so — unlike the forwarder this used to be — there is no later lifetime to
        // chase: every caller below can hold this one instance for good.
        var transcript = new TranscriptWriter(system, mainWindow.Chat);
        var permissionGate = new InteractivePermissionGate(system, mainWindow, session.WorkingDirectory,
            permissionPolicy, permissionRules, transcript);
        // Guards the LoadError echo below so it is reported once, on the FIRST WireRunner call
        // only — F5/F7/F8 re-wires reuse this same permissionRules instance, and its LoadError
        // describes what happened at construction, not live state, so repeating it on every
        // re-wire would just be noise about an event that already happened and was already told.
        var permissionLoadErrorReported = false;

        using var cts = new CancellationTokenSource();

        // Mutable so first-run setup (and F5 settings) can install a runner that didn't exist at
        // startup. The PreviewKeyPressed handler below closes over THIS FIELD, not over a runner
        // local, so a later assignment takes effect without re-registering the handler. session.Provider
        // tracks alongside it — F6's diagnose closure must call the CURRENT provider, not whichever
        // one was resolved at startup, or a provider change via F5 mid-session would silently keep
        // diagnosing against the old (possibly now-invalid) one.
        AgentHost? runner = null;

        // The currently-open consolidated Settings dialog, or null when none is open. Captured by the
        // Escape global shortcut (routes Escape to Cancel while a dialog is open) and by
        // OpenSettingsAsync (reentrancy: a second F5/F7/F8 press selects a page in this instance rather
        // than opening a second dialog). Cleared in OpenSettingsAsync's `finally` — see its comment.
        SettingsDialog? openDialog = null;





        // Owned by the SESSION, not by any one AgentHost: a re-wire swaps the model and must not
        // kill the servers. Assigned below, before the first WireRunner, and read by every host it
        // builds thereafter.
        // The running turn's cancellation scope, or null when nothing is running. Replaced on every
        // submission and read by the Escape handler — a field rather than a local because the handler
        // is registered once and must see the CURRENT turn, not whichever existed at registration.
        CancellationTokenSource? turnCts = null;

        // IS A TURN RUNNING — ONE DEFINITION, THREE CONSUMERS: the submission guard, /compress, and
        // Escape. They were about to grow three subtly different answers to the same question.
        //
        // `turnCts` alone is NOT that predicate, and this is the trap the spec called out. It is
        // never nulled when a turn ENDS, only replaced when the next one starts — so
        // `turnCts is { IsCancellationRequested: false }` LATCHES TRUE after the first completed
        // turn and would block every submission for the rest of the session. Escape got away with
        // reading it because cancelling an already-finished turn is harmless. A guard cannot.
        var turnRunning = false;
        bool IsTurnRunning() => turnRunning && turnCts is { IsCancellationRequested: false };

        // Messages typed while a turn was running, in the order they were typed. Joined into ONE
        // prompt when the turn ends (D18) — appended, never replaced: two messages are usually one
        // thought completed, and dropping either half is silent data loss the user cannot see.
        var queuedPrompts = new List<string>();

        // Tokens live beside config at 0600, never IN it. One HttpClient for the auth traffic, shared
        // rather than per-login: a new one per attempt leaks sockets in TIME_WAIT.
        var mcpTokens = new Core.Mcp.Auth.TokenStore(paths);
        using var httpForAuth = new HttpClient();

        // The token is read per REQUEST through this delegate, so a login mid-session reaches a
        // client that was built before it.
        var mcp = new Core.Mcp.McpManager(permissionGate,
            accessToken: name => mcpTokens.Get(name)?.AccessToken);

        // /mcp lives in its own type: this file decides WHAT EXISTS, not what a command does.
        var mcpCommand = new McpCommand(mcp, mcpTokens, httpForAuth, paths, env, mainWindow);


        void WireRunner(ProviderResolution res)
        {
            if (!res.HasProvider) return;

            // Rebuilt from THIS resolution's roles so an F7 rebinding takes effect in this session.
            // The new AgentHost below reads this field, not a startup copy.
            var plugins = PluginRegistry.CreateWithBuiltins(res.Providers, permissionGate);
            // The outgoing host is disposed by Session.ReplaceHost below, not here: a re-wire that
            // merely reassigned would leak it, and that is a step a caller can forget while the host
            // is a bare local.
            var sink = new ChatTranscriptSink(system, mainWindow.Chat);
            // The row and the agent must agree from the first frame — a status line that is right
            // only after the user touches something is a status line nobody trusts.
            mainWindow.SetMode(AgentModes.Name(startupMode));
            // I3: permissionRules.Load ran at construction, before any sink existed to tell the
            // user. Echo a load failure here, once — a bad hand-edit to permissions.json silently
            // dropped every rule and all folder trust, and the user needs to know before they grant
            // anything else (the next grant backs the unreadable file up to permissions.json.bad).
            if (!permissionLoadErrorReported && permissionRules.LoadError is { } loadError)
            {
                transcript.Write($"[yellow]{loadError}[/]");
                permissionLoadErrorReported = true;
            }
            // Jobs render INLINE in the transcript, not in a side panel — one column, jobs
            // interleaved with the turns that caused them. JobPanelSink (and JobPanelControl) still
            // exist and still work; they are simply not wired. Both speak IToolObserver, so this line is
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

            // CONSUMED ONCE, READ TWICE. Taking the session's pending resume clears it, so a later
            // F5 re-wire cannot resurrect a session the user already resumed. But BOTH the ledger's
            // seed and the host's context come from it, and taking it inline in the argument list
            // (as this used to) while also reading it for the ledger would hand the second reader a
            // null — seeding the ledger and silently discarding the entire restored conversation,
            // with every test still green. One local, both uses.
            var resumeSnapshot = session.TakePendingResume();

            // THE LEDGER IS THE COMPOSITION ROOT'S NOW (D7), not AgentHost's. Constructed here so
            // "which ledger does this agent get?" has an answer — the question per-model attribution
            // and sub-agent factories both have to ask.
            //
            // IN WireRunner, NOT AT THE TOP OF AppBootstrap, and the distinction is behavioural.
            // This method re-runs on every F5 provider change and that RESETS the spend to zero.
            // Hoisting it to startup would make the ledger survive the re-wire and report one
            // session's spend across two providers as though it were one model's.
            // CONSUMED ONCE, like the pending resume: a later re-wire must start fresh rather than
            // inherit a ledger from a switch two provider changes ago.
            var carried = session.TakeCarriedLedger();

            var ledger = carried
                ?? (resumeSnapshot is null
                    ? new TokenLedger()
                    : new TokenLedger(resumeSnapshot.InputTokens, resumeSnapshot.OutputTokens));

            // THE SUB-AGENT SEAM, assembled here because this is the only place that holds all of
            // it: the provider, the plugin registry, the ledger just built above, the context window
            // and the orchestrator settings. That is exactly what the ledger hoist was for — those
            // last two are private on AgentHost and were unreachable from any factory before it.
            var orchestrator = res.Orchestrator ?? OrchestratorSettings.Unbounded;
            // THE TYPE CATALOG. Built per re-wire, like everything else here: an F5 provider change
            // must re-resolve every type's instance against the NEW registry, or a type would keep a
            // provider the session no longer uses.
            var agentTypes = new AgentTypeCatalog(res.AgentTypes, res.Providers);

            var subAgents = new SubAgentSpawner(new SubAgentFactory(new SubAgentFactory.SubAgentRuntime
            {
                Provider = res.Provider!,
                InstanceName = res.InstanceName,
                Plugins = plugins,

                // THE PARENT'S LEDGER (D7): a child's spend is the session's spend.
                Ledger = ledger,
                Logs = logs,

                // THE SAME CEILING THE PARENT GETS, resolved once. Two expressions for one number is
                // how a configured 0 came to mean "unbounded" for the session and "the default" for
                // its children.
                MaxTurns = AgentHost.CeilingFor(orchestrator.MaxTurns),

                // THE CONSTANT, never the literal — two copies of this number desynchronise the
                // moment either moves, and a child that never compresses dies on an overflow.
                CompressAbove = orchestrator.EffectiveCompressThreshold(res.ContextWindow)
                    ?? OrchestratorSettings.DefaultCompressThreshold,
                ContextWindow = res.ContextWindow,

                GlobalInstructionsDir = paths.ConfigDir,
                Mcp = mcp.Toolset,

                // THE SESSION'S OWN RULE, injected rather than copied. A type on a different instance
                // has a different window, so the threshold must be re-derived from it — and a second
                // copy of "80% of the window" in the factory would desynchronise the moment either
                // moved.
                ThresholdFor = w => orchestrator.EffectiveCompressThreshold(w)
                    ?? OrchestratorSettings.DefaultCompressThreshold,

                // UNCAPPED UNLESS THE USER SAID OTHERWISE. Null is the common case and means every
                // spawn the model emits runs — the barrier still holds them all inside the turn.
                MaxConcurrentAgents = res.MaxConcurrentAgents,
                WorkingDir = session.WorkingDirectory,
            }),
                agentTypes);

            var host = new AgentHost(
                new AgentHost.AgentRuntime
                {
                    Provider = res.Provider!,
                    InstanceName = res.InstanceName,
                    Plugins = plugins,

                    // THE SAME workingDir THE PERMISSION GATE USES, captured once at startup.
                    // Sessions and permission rules are both scoped to the project they belong to,
                    // and they must agree on what "this project" means.
                    WorkingDir = session.WorkingDirectory,

                    // OUR config folder, so a user-level CXAGENT.md applies wherever they work.
                    GlobalInstructionsDir = paths.ConfigDir,

                    // The real window (when config told us one), so auto-compression derives its
                    // threshold from actual headroom instead of the fixed constant. Null on
                    // --mock/no-provider and whenever contextWindow is not configured.
                    ContextWindow = res.ContextWindow,

                    // Passing this is what makes the cap real: the host defaults to unbounded, so
                    // omitting it silently disabled the turn cap in production while every unit
                    // test still passed.
                    Orchestrator = res.Orchestrator,

                    // The toolset, but NOT the servers: ownership stays with the session. Handing
                    // those over would let an F5 re-wire dispose them, killing every server on a
                    // provider change and leaving the new host with a toolset over dead pipes.
                    Mcp = mcp.Toolset,

                    Spawner = subAgents,
                    Mode = startupMode,

                    // HOW THE MODEL ASKS. The window owns the composer swap, so this is the one
                    // place that can put a question where the permission gate already asks. A
                    // sub-agent never gets it — Agent refuses regardless of what is passed here.
                    AskUser = mainWindow.AskQuestionAsync,
                },
                sink,
                jobPanelSink,
                new AgentHost.SessionStores
                {
                    // Every completed turn lands here, so a crash leaves something to resume from.
                    Resume = sessions,

                    // And here, for /stats — a separate archive that outlives the session.
                    History = history,
                    Logs = logs,
                },
                resume: resumeSnapshot,

                // Built above from the same snapshot `resume` came from, so a resumed session gets
                // its spend back exactly as it did when AgentHost made this itself.
                ledger: ledger);

            // THROUGH THE SESSION, which disposes the host it replaces and records the provider and
            // instance alongside it — three facts that must move together, and used to be three
            // assignments a re-wire had to remember.
            session.ReplaceHost(host, res.Provider!, res.InstanceName, plugins);
            runner = host;

            // Non-fatal config complaints — a server entry we could not read. Said once, here,
            // because a skipped server the user never hears about is indistinguishable from one
            // that is merely slow to connect.
            foreach (var warning in res.Warnings)
                transcript.Write($"[yellow]{warning}[/]");

            // PERMISSION DECISIONS INTO HISTORY. Set here rather than at the gate's construction
            // because the session id does not exist until the host does — and reassigned on every
            // re-wire (F5 changes provider), so the hook reads `runner` lazily rather than closing
            // over the id of a host that has since been replaced.
            permissionGate.OnDecision = (kind, decision, requester) =>
                history.SavePermission(new PermissionRecord(
                    runner?.SessionId ?? "unknown", DateTimeOffset.UtcNow,
                    kind.ToString(), decision, requester, session.WorkingDirectory));

            runner.TokensUpdated += (_, total) => system.EnqueueOnUIThread(() =>
            {
                // THE PARENT'S OWN SPEND, not `total`. The event carries Ledger.TotalTokens, which is
                // the whole session — children share the ledger — and the status bar is this agent's
                // readout: it sits beside an occupancy percentage that is the parent's, so a
                // session-wide figure there read as the parent's and was four times too large.
                var (ownIn, ownOut) = runner!.OwnSpend;
                mainWindow.SetTokenTotal(ownIn + ownOut);
                mainWindow.SetTokenSplit(ownIn, ownOut);

                // THE SAME EVENT, so the breakdown and the number it breaks down can never disagree.
                // Pushed rather than pulled: the panel refreshes on a clock too, and a stale tally
                // beside a live total is the kind of small inconsistency nobody can explain later.
                //
                // The PANEL keeps the session-wide figures — that is the division: bar is this agent,
                // panel is everything, and "Tokens by agent" is where the two are reconciled.
                mainWindow.SetSpendByModel(runner.Ledger.ByModel, runner.Ledger.SubAgentTokens,
                    runner.Ledger.SplitByModel, runner.Ledger.CacheHitRate,
                    runner.Ledger.CacheHitRateByAgent);
            });
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
                // THE PARENT'S SPLIT, matching the total beside it. The ledger's InputTokens and
                // OutputTokens include every child, and a bar showing a session-wide ↑/↓ under a
                // parent-only total would be two figures that cannot be added together.
                var (turnIn, turnOut) = runner.OwnSpend;
                mainWindow.SetTokenSplit(turnIn, turnOut);

                // SKILLS, RE-READ EVERY TURN like the agent's own discovery — a skill added or
                // edited mid-session shows up here on the same turn its description reaches the
                // prompt, rather than after a restart.
                //
                // The LOADED list is derived from the parent's window, so it empties itself when
                // compaction removes a body. That silent stop is the thing worth showing.
                mainWindow.SkillCount = Core.Skills.SkillCatalog
                    .Find(session.WorkingDirectory, paths.ConfigDir).Skills.Count;
                mainWindow.LoadedSkills = runner.LoadedSkills;

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

        // MCP SERVERS BEFORE THE FIRST WIRE-UP.
        //
        // Started here rather than inside WireRunner because they belong to the SESSION, not to the
        // provider: an F5/F7/F8 re-wire swaps the model, and killing and re-spawning every server
        // over a provider change would cost seconds and lose whatever state they hold. WireRunner
        // reads these; they outlive each host it builds, and AppBootstrap ends them at exit.
        //
        // Before the first prompt, deliberately. The tools array is part of the prompt-cache prefix,
        // so a server that connects LATER invalidates it — starting them up front keeps that to at
        // most one invalidation instead of one per late arrival.
        //
        // Blocking is acceptable and bounded: every failure path inside returns rather than throws,
        // and each server carries its own timeout. This runs before the UI loop begins, so there is
        // no frame to drop — only a slower start for someone who configured a slow server.
        if (resolution.McpServers.Count > 0)
        {
            mcp.ReloadAsync(resolution.McpServers, CancellationToken.None).GetAwaiter().GetResult();

            // Each failure named once, plus any tool dropped for colliding — both are things the
            // user configured and would otherwise watch silently not happen.
            foreach (var message in mcp.Messages.Concat(mcp.Toolset.Warnings))
                transcript.Write($"[yellow]{message}[/]");
        }

        // The panel shows what is live, including servers that failed.
        mainWindow.SetMcpServers(mcp.Statuses());

        // --resume IS SEEDED BEFORE THE FIRST WIRE, not restored after it. The session's pending resume is what
        // WireRunner reads to build a host over an existing conversation, so setting it here means
        // the session starts restored — no second wire, and no window that is briefly empty before
        // a context appears in it. The startup OFFER cannot do this (it needs a rendered window to
        // put a dialog in) and re-wires for that reason; asking on the command line does not.
        string? resumeNotice = null;
        if (options.Resume.Wanted)
        {
            var (snapshot, problem) = FindResumeTarget(sessions, session.WorkingDirectory, options.Resume.Uid);
            if (snapshot is not null)
            {
                session.PendResume(snapshot);

                // RETIRE THE ROW IT CAME FROM: the resumed session is a new agent writing its own
                // rows, and leaving the old one open would offer the same context again at the next
                // launch. SUPERSEDED, not finished — it is a live conversation someone continued,
                // and pruning it would delete the history behind work that is still going.
                sessions.MarkSuperseded(snapshot.AgentId);
                resumeNotice = $"[yellow]Resumed an earlier session: {snapshot.Context.Count} "
                             + "messages restored. They are not shown above, but the agent "
                             + "remembers them.[/]";
            }
            else
            {
                // NOT FATAL. The user asked to continue something and gets a new session instead —
                // which is fine as long as it SAYS SO, since an unnoticed fresh start is how someone
                // spends a turn wondering why the agent forgot everything.
                resumeNotice = $"[yellow]{problem} Starting a new session.[/]";
            }
        }

        WireRunner(resolution);   // startup path, unchanged in effect

        // AFTER THE WIRE, because the sink it writes to is created inside it.
        if (resumeNotice is not null)
            transcript.Write(resumeNotice);

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
        commandMenu.Chosen += (_, completion) =>
        {
            // Choosing fills the composer rather than dispatching. The command may take arguments,
            // and a menu that ran on selection would make "/compress" unreachable-with-an-argument
            // and give the user no chance to change their mind. One more Enter runs it.
            //
            // The text is now whatever the ROW completes to — "/mcp" from the command list, or
            // "/mcp reload" when the user descended into its arguments.
            mainWindow.Input.Input = completion;
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

            // Plain Enter → submit. What a submitted line MEANS is SubmitComposer's.
            e.Handled = true;   // consume it so the MLE does not also insert a newline
            SubmitComposer();
        };

        // THE SUBMISSION PATH, lifted out of the key handler.
        //
        // What stayed there is genuinely about KEYS — is this Enter, does the composer have focus,
        // does the line continue. Everything here is about what a submitted line MEANS, and the two
        // were interleaved in one 115-line lambda, and only the first ten lines of it had anything
        // to do with a keystroke.
        void SubmitComposer()
        {
            if (runner is null || !mainWindow.SubmissionEnabled) return;
            var goalText = mainWindow.Input.Input;
            if (string.IsNullOrWhiteSpace(goalText)) return;
            // RECORD IT FOR ↑/↓ OURSELVES. PromptControl records history inside its own Submit(),
            // which this handler pre-empts — we consume Enter before the control sees it, so nothing
            // would ever reach the history and the feature would be silently dead.
            mainWindow.Input.RecordHistory(goalText);

            // WHAT TO SHOW, when it differs from what is sent — see the NeedsTurn case below.
            string? turnEcho = null;

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
                        // Two commands share this outcome now, so dispatch on the name. Both need
                        // something SessionCommands deliberately does not hold — the window for
                        // /help, the live servers for /mcp.
                        if (command.Name == "/mcp")
                        {
                            _ = mcpCommand.HandleAsync(SessionCommands.Arguments(goalText));
                            return;
                        }

                        if (command.Name == "/skills")
                        {
                            // DISCOVERED HERE AND NOW, from the working directory — the same read the
                            // agent does each turn, so what this prints is what the model is seeing
                            // rather than a copy that could disagree with it.
                            new SkillsCommand(
                                () => Core.Skills.SkillCatalog.Find(
                                    session.WorkingDirectory, paths.ConfigDir),
                                transcript).Handle();
                            return;
                        }

                        if (command.Name == "/diff")
                        {
                            // THE FOLDER THIS SESSION RUNS IN, not wherever the process happens to
                            // be: it is the same directory permissions are scoped to, and the one
                            // whose files the agent has been editing.
                            mainWindow.Chat.AddMessage(ChatRole.System,
                                DiffCommand.Render(SessionCommands.Arguments(goalText), session.WorkingDirectory));
                            return;
                        }

                        if (command.Name == "/model")
                        {
                            SwitchModel(SessionCommands.Arguments(goalText));
                            return;
                        }

                        if (command.Name == "/sessions")
                        {
                            HandleSessions(SessionCommands.Arguments(goalText));
                            return;
                        }

                        if (command.Name == "/stats")
                        {
                            var statsArg = SessionCommands.Arguments(goalText);

                            // READ FAILURES ARE REPORTED, unlike the writes. An empty dashboard from
                            // a locked database would say "you have done nothing", which is a lie a
                            // user cannot detect; an error tells them the number is missing rather
                            // than zero.
                            try
                            {
                                if (StatsCommand.IsClear(statsArg))
                                    ConfirmClearHistory(mainWindow, history);
                                else
                                    mainWindow.Chat.AddMessage(ChatRole.System,
                                        StatsCommand.Render(history, statsArg));
                            }
                            catch (Exception ex)
                            {
                                mainWindow.Chat.AddMessage(ChatRole.System,
                                    $"[{ColorScheme.DangerMarkup}]Could not read usage history: {ex.Message}[/]");
                            }
                            return;
                        }

                        if (command.Name == "/mode")
                        {
                            if (runner is null)
                            {
                                mainWindow.Chat.AddMessage(ChatRole.System,
                                    "[yellow]No provider configured — there is no agent to set a mode on.[/]");
                                return;
                            }

                            var decision = ModeCommand.Decide(
                                SessionCommands.Arguments(goalText), runner.Mode.Agent, IsTurnRunning());

                            // LIVE, NO RESTART. Both things a mode changes are rebuilt on the next
                            // prompt anyway — the tool list and the system message — so this is one
                            // assignment rather than a re-wire, and the conversation is untouched.
                            if (decision.NewMode is { } next)
                            {
                                runner.Mode = next;
                                mainWindow.SetMode(AgentModes.Name(next));
                            }

                            mainWindow.Chat.AddMessage(ChatRole.System, decision.Reply);
                            return;
                        }
                        mainWindow.ShowHelp();
                        return;

                    case CommandOutcome.NeedsTurn:
                        // REWRITTEN INTO A PROMPT AND FALLING THROUGH to the ordinary goal path
                        // below — not handled here. Everything that path already does is exactly
                        // what this needs: the running-turn queue, the cancellation scope, the
                        // spinner, the token accounting. Starting a turn here instead would be a
                        // second submission route that has to relearn all of it.
                        if (command.Name == "/init")
                        {
                            // DECLINED WHILE A TURN RUNS, like /compress and unlike an ordinary
                            // prompt. Queued prompts are JOINED into one message, so an /init waiting
                            // behind two other instructions would reach the model as a paragraph of
                            // its briefing glued to unrelated work — which is not the operation
                            // anybody asked for, and would be attributed to the user besides.
                            if (IsTurnRunning())
                            {
                                mainWindow.Chat.AddMessage(ChatRole.System,
                                    "[yellow]A turn is running — press Escape to stop it first.[/]");
                                return;
                            }

                            var target = InitCommand.Resolve(session.WorkingDirectory);
                            if (target.Note is { } note)
                                mainWindow.Chat.AddMessage(ChatRole.System,
                                    $"[{ColorScheme.MutedMarkup}]{note}[/]");

                            goalText = InitCommand.Prompt(target);

                            // WHAT THE USER TYPED, on the transcript. The model gets the briefing
                            // above; echoing that would attribute a message to the user that they
                            // never wrote, and leave them scrolling past it forever after.
                            turnEcho = "/init";
                        }
                        break;

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
                        //
                        // DECLINED WHILE A TURN RUNS (0d), and declined rather than queued.
                        // CompressNowAsync REPLACES Context.Messages wholesale while the agent's
                        // tool loop is appending results to that same list: best case the results
                        // are lost, likely case a torn List<T> and an InvalidOperationException
                        // mid-request. Live today — a parent doing three read_file calls is exposed,
                        // no sub-agent needed — and 0a's queue makes it MORE reachable, since
                        // /compress becomes one of the few things a user CAN press during a long
                        // run.
                        //
                        // NOT QUEUED, and the difference from an ordinary prompt is real: a prompt
                        // is still valid when the turn ends, but /compress is a measurement-and-
                        // rewrite of a context that is actively changing — running it later is a
                        // DIFFERENT operation from the one that was asked for. Nothing is lost by
                        // refusing: the automatic route already compresses on measured pressure, so
                        // this costs a keystroke, not a compaction.
                        if (IsTurnRunning())
                        {
                            mainWindow.Chat.AddMessage(ChatRole.System,
                                "[yellow]A turn is running — press Escape to stop it first.[/]");
                            return;
                        }

                        if (runner is not null)
                            _ = runner.CompressNowAsync(cts.Token);
                        return;
                }
            }

            // Everything else that begins with a slash — /clear, and any unrecognised command, which
            // gets the "available commands" reply rather than being sent to the model as a task.
            // Costs nothing: no goal, no provider call, no tokens.
            if (SessionCommands.TryHandle(goalText, out var commandReply))
            {
                // /clear CLEARS THE AGENT'S CONTEXT, which is the whole operation: that list is what
                // the model is sent on every turn. It used to clear a second list as well, on the
                // reasoning that one held the transcript and the other what the model carries — but
                // nothing ever read the second one, so the extra clear did nothing and the comment
                // implied a hazard that did not exist.
                if (SessionCommands.Match(goalText)?.Name == "/clear")
                {
                    runner?.Context.Clear();
                    mainWindow.SetContextUsed(0);
                }

                mainWindow.Chat.AddMessage(ChatRole.System, commandReply);
                return;
            }

            // A TURN IS ALREADY RUNNING: QUEUE, do not start a second one.
            //
            // Two SendAsync calls on the same Agent append to ONE live Context.Messages from two
            // loops — and worse, the Exchange below would dispose the RUNNING turn's token, so the
            // first loop throws ObjectDisposedException at its next cancellation check instead of
            // cancelling. Invisible today only because turns last seconds; a sub-agent turn lasts
            // minutes.
            //
            // GUARDING HERE, NOT AT SubmissionEnabled: that flag is tested on SubmitComposer's FIRST
            // line, before command dispatch, so using it would also disable /exit, /clear, /mcp,
            // /help and /compress — the user could not quit while a turn ran, and the composer would
            // claim "no provider", which is a lie. Commands reach this point already handled; only
            // the model dispatch is blocked.
            if (IsTurnRunning())
            {
                queuedPrompts.Add(goalText);
                mainWindow.Chat.AddMessage(ChatRole.System,
                    $"[dim]queued[/] {ChatTranscriptSink.Escape(goalText)}");
                return;
            }

            // Fire-and-forget on the UI-initiated flow; sync-context resumes continuations on the UI thread.
            // Retire the hint HERE, at submission — not when tokens first arrive. Tied to the token
            // readout it stayed on screen for the whole of a running request, telling the user to type
            // a prompt while the agent was several tool calls into one.
            mainWindow.RetireComposerHint();

            // A CANCELLATION SCOPE PER TURN, linked to the session's. Escape cancels THIS request;
            // the session token still ends everything on Ctrl+Q or /exit. Before this the only token
            // in existence was the session's, so there was no way to stop a turn without taking the
            // whole app down with it.
            var previousTurn = System.Threading.Interlocked.Exchange(ref turnCts,
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token));
            // SAFE ONLY BECAUSE OF THE GUARD ABOVE. This disposes the PREVIOUS turn's token, which
            // was a live one whenever a second submission landed mid-turn. Now a second submission
            // queues and never reaches here, so whatever this disposes is always a finished turn.
            previousTurn?.Dispose();
            var turnToken = turnCts!.Token;

            turnRunning = true;
            RunTurnAsync(goalText, turnToken, turnEcho);
        }

        // Runs one turn and, when it ends, drains anything typed while it was running.
        //
        // FIRE-AND-FORGET WITH A CONTINUATION, not `_ = runner.SendAsync(...)`. Nothing previously
        // knew when a turn ENDED, which is why the running flag could only ever latch. The await
        // here is what gives it a falling edge.
        async void RunTurnAsync(string prompt, CancellationToken token, string? echo = null)
        {
            try
            {
                await runner!.SendAsync(prompt, token, echo);
            }
            catch (OperationCanceledException)
            {
                // Escape. Already reported by the Escape handler; nothing to add.
            }
            catch (Exception ex)
            {
                // A turn that dies must still release the flag, or the session accepts no further
                // prompts and looks hung — the failure mode this whole guard exists to avoid.
                transcript.WriteError(ex.Message);
            }
            finally
            {
                turnRunning = false;
            }

            // THE QUEUE GOES IN AS ONE PROMPT (D18). Several messages are APPENDED
            // newline-separated, never replaced: two messages are usually one thought completed — a
            // correction and its qualifier — and replacing silently discards half of what someone
            // said with no way to tell which half survived. The newline (rather than a space) is
            // structure a model reads: they were separate thoughts.
            //
            // NOT drained on cancellation: Escape moves the queue back to the composer instead (see
            // the Escape handler), because the user stopping the run is the user changing their
            // mind, not confirming what they typed.
            if (queuedPrompts.Count == 0 || token.IsCancellationRequested) return;

            var joined = PromptQueue.Join(queuedPrompts);
            queuedPrompts.Clear();
            mainWindow.Input.Input = joined;
            SubmitComposer();
        }

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
        // BACK, WHILE THE MODEL IS ASKING. Only meaningful during a multi-question run, and a no-op
        // otherwise. Alt-modified because the field below is a text box: a bare Left or Backspace
        // shortcut would swallow the keys that edit a typed answer.
        system.RegisterGlobalShortcut(ConsoleModifiers.Alt, ConsoleKey.LeftArrow,
            () => mainWindow.TryQuestionBack());

        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.Escape,
            () =>
            {
                // A QUESTION FIRST, BEFORE ANYTHING ELSE. Escape while the model is asking means
                // "I am not answering that" — not "throw away the run". Making the only exit from a
                // dialog be cancelling the turn would put the user's work behind their reluctance to
                // answer, so the question is skipped and the model proceeds on its own judgement.
                if (mainWindow.TrySkipQuestion()) return;

                // Routed through EscapeRouting.For so the DECISION is unit-testable; the actions
                // (which need a live dialog or a live turn) stay here.
                var running = turnCts is { IsCancellationRequested: false };
                switch (EscapeRouting.For(openDialog is not null, running))
                {
                    case EscapeTarget.CancelDialog:
                        openDialog!.Cancel();
                        break;

                    case EscapeTarget.CancelTurn:
                        // Cancelling the token unwinds the whole turn: the provider stream, the tool
                        // loop, and any shell process, whose ProcessRunner kills its ENTIRE process
                        // tree on cancellation. The session, its context and its MCP servers survive.
                        turnCts!.Cancel();
                        transcript.Write("[yellow]Stopped.[/]");

                        // ANYTHING QUEUED GOES BACK TO THE COMPOSER, not to the bin. That text was
                        // never sent, so cancelling a run must not eat what someone typed — they can
                        // now edit it, resend it, or clear it, which is the whole point of stopping.
                        //
                        // ABOVE any text already in the composer, preserving the order things were
                        // written in: the queued lines were typed first.
                        if (queuedPrompts.Count > 0)
                        {
                            mainWindow.Input.Input =
                                PromptQueue.Restore(queuedPrompts, mainWindow.Input.Input);
                            queuedPrompts.Clear();
                        }
                        break;
                }
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
            // NAMED settingsSession, not `session`: the agent session is in scope here and the two are
            // different objects. A shadowing local made `session.WorkingDirectory` silently mean the
            // settings dialog's own state rather than the folder the agent works in.
            var settingsSession = SettingsSession.ForLoad(load);
            var dialog = new SettingsDialog(system, mainWindow.Window, paths, settingsSession,
                permissionRules, session.WorkingDirectory);
            openDialog = dialog;
            try
            {
                var result = await dialog.RunAsync(page, cts.Token);
                if (result is null) return;   // cancelled, or TryCompose found nothing dirty

                // Re-resolve and re-wire so an edit made here takes effect in THIS session rather than
                // at next launch — exactly one re-wire, gated on a non-null result, matching the
                // "exactly one re-wire on Save, none on Cancel" rule the retired F7/F8 handlers followed.
                // SAME REASON AS /model: a Save can change the provider, and the panel has to
                // describe the session that is now running rather than the one that started.
                var reresolved = ProviderResolver.Resolve(paths, env, useMock: false);
                resolution = reresolved;

                WireRunner(reresolved);
                mainWindow.SetResolution(reresolved);
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
            if (permissionRules.GetTrust(session.WorkingDirectory) != TrustState.Unknown) return;

            var prompt = new TrustQuestionControl(session.WorkingDirectory);
            var content = prompt.BuildContent();   // built ONCE — see PermissionPromptControl's contract
            mainWindow.ShowPermissionPrompt(content);
            try
            {
                var trusted = await prompt.Completion;
                var state = trusted ? TrustState.Trusted : TrustState.Untrusted;
                try
                {
                    permissionRules.SetTrust(session.WorkingDirectory, state);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    transcript.Write($"[yellow]could not save folder trust: {ex.Message}[/]");
                }
                transcript.Write(trusted
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
                permissionRules.RulesFor(session.WorkingDirectory).Rules.Count);
            mainWindow.RefreshSessionPanel();

            // LIVE VALUES FOR `/sessions resume `. Registered here, in the composition root, because
            // the store is the composition root's — SessionCommands stays a description of the
            // commands rather than a view of the database. Read on each keystroke, never cached.
            commandMenu.Values = source => source switch
            {
                ValueSources.Sessions => SessionsCommand.Completions(SafeList()),
                ValueSources.Providers => ModelCommand.Completions(resolution.Providers, session.InstanceName),
                _ => [],
            };

            // NEVER RESUME SILENTLY. A context the user did not ask for is one they cannot account
            // for, and it is paid for on the very first turn — so this asks, on the first pump, for
            // the same reason everything else here is deferred: a dialog needs a render tick to join.
            // NOT WHEN THE COMMAND LINE ALREADY ANSWERED. `--resume` said which session to continue,
            // and pointing at the list afterwards would be the app answering a question nobody asked
            // twice over.
            if (!options.Resume.Wanted)
                HintAtResume();
        });

        // THE LIST THE COMMAND AND THE PALETTE BOTH READ. Scoped to this folder unless asked
        // otherwise, and re-read on every use: a session that ended in another window a minute ago
        // has to appear, and a cached list is a list that lies about exactly that.
        // COMPLETION MUST NEVER THROW. It runs on a keystroke inside layout, where an exception from
        // a locked database would take down the composer rather than produce an empty menu.
        IReadOnlyList<SessionInfo> SafeList()
        {
            try { return ListSessions(false); }
            catch { return []; }
        }

        IReadOnlyList<SessionInfo> ListSessions(bool all) =>
            sessions.List(all ? null : session.WorkingDirectory, all);

        void HandleSessions(string argument)
        {
            try
            {
                // "all" IS READ BEFORE THE DECISION, because it changes which rows exist rather than
                // what is done with them — and `resume 3` has to mean the row the user is looking at.
                var all = SessionCommands.ArgumentWords($"/sessions {argument}")
                    .Any(w => w.Equals("all", StringComparison.OrdinalIgnoreCase));

                var rows = ListSessions(all);
                var result = SessionsCommand.Decide(
                    argument, rows, SqliteSessionStore.DefaultRetention, all);

                if (result.ResumeUid is null)
                {
                    mainWindow.Chat.AddMessage(ChatRole.System, result.Reply);
                    return;
                }

                // RESTORING MID-TURN IS REFUSED. WireRunner replaces the agent the running turn is
                // appending to — the tool results of a call already in flight would land in a
                // conversation nobody is reading, which is the orphan shape that 400s a session.
                if (IsTurnRunning())
                {
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        "[yellow]A turn is running — press Escape to stop it first.[/]");
                    return;
                }

                if (sessions.LoadByUid(result.ResumeUid) is { Session: { } snapshot })
                    RestoreSession(snapshot);
                else
                    mainWindow.Chat.AddMessage(ChatRole.System,
                        "[yellow]That session could not be read back.[/]");
            }
            catch (Exception ex)
            {
                // REPORTED, like /stats reads and unlike the writes: an empty list from a locked
                // database says "you have no earlier sessions", which is a lie a user cannot detect.
                mainWindow.Chat.AddMessage(ChatRole.System,
                    $"[{ColorScheme.DangerMarkup}]Could not read sessions: {ex.Message}[/]");
            }
        }

        void SwitchModel(string argument)
        {
            var decision = ModelCommand.Decide(argument, resolution.Providers, session.InstanceName);

            if (decision.SwitchTo is null)
            {
                mainWindow.Chat.AddMessage(ChatRole.System, decision.Reply);
                return;
            }

            // REFUSED MID-TURN, like /mode and /compress. Re-wiring replaces the agent the running
            // turn is appending to — its tool results would land in a conversation nobody is
            // reading, which is the orphan shape that 400s a session permanently.
            if (IsTurnRunning())
            {
                mainWindow.Chat.AddMessage(ChatRole.System,
                    "[yellow]A turn is running — press Escape to stop it first.[/]");
                return;
            }

            var next = ProviderResolver.ResolveInstance(paths, env, decision.SwitchTo);
            if (next is null || !next.HasProvider)
            {
                mainWindow.Chat.AddMessage(ChatRole.System,
                    $"[{ColorScheme.DangerMarkup}]Could not start {decision.SwitchTo}.[/]");
                return;
            }

            // THE CONVERSATION AND THE SPEND BOTH CARRY — one call, because arming one without the
            // other is silently wrong in both directions. Through the same seam a resume uses:
            // WireRunner takes the session's pending resume to build a host over an existing
            // conversation. See Session.CarryToNextWire.
            // FROM THE SESSION'S HOST, not the `runner` local — they are the same object, and one
            // of the two names is the one a second session would have.
            var window = session.Host!.Context.Window;
            var used = session.Host.Context.Used;
            session.CarryToNextWire();


            resolution = next;
            WireRunner(next);

            // THE WINDOW HAS TO FOLLOW THE MODEL. MainWindow held its resolution readonly from
            // startup, so the status bar went on quoting the old context window after any re-wire —
            // F5 had the same defect, unnoticed because a reconfiguration usually keeps the model.
            mainWindow.SetResolution(next);

            // NOT COMPACTED HERE, deliberately. The turn loop measures pressure before every send
            // and compacts if it must — doing it now would be the same work in a worse place, and
            // would summarise a conversation the user might not send another turn on.
            transcript.Write(ModelCommand.Switched(
                decision.SwitchTo, next.Provider!.ModelId, next.ContextWindow, window, used));
        }

        // THE ONE WAY BACK INTO A SESSION, shared by the startup offer and by /sessions resume.
        // Restoring is four steps that only work together — seed, re-wire, retire the old row, and
        // say so — and the second caller is exactly when a sequence like that gets copied with one
        // step quietly missing.
        void RestoreSession(SessionSnapshot snapshot)
        {
            session.PendResume(snapshot);
            WireRunner(resolution);   // rebuilds the runner over the restored context

            // RETIRE THE ROW IT CAME FROM. The resumed session is a NEW agent with a new id
            // writing its own rows, so leaving the old one open would offer the same crashed
            // context again at every launch — and accepting it twice would fork the conversation
            // into two sessions claiming the same history. SUPERSEDED rather than finished: see
            // MarkSuperseded, which is why this one survives pruning.
            sessions.MarkSuperseded(snapshot.AgentId);

            // SAY SO IN THE TRANSCRIPT. The restored turns are not rendered — they are the
            // model's memory, not this session's scrollback — so without a line here the user
            // faces an empty screen and an agent that mysteriously already knows things.
            transcript.Write(
                $"[yellow]Resumed an earlier session: {snapshot.Context.Count} messages restored. "
                + "They are not shown above, but the agent remembers them.[/]");
        }

        // A HINT, NOT A QUESTION.
        //
        // This used to be a dialog: "an earlier session ended without closing — resume it?", asked on
        // the first render, before the user had typed anything. Three things were wrong with it.
        //
        // It asked at the worst possible moment. Someone opening the app is about to do something,
        // and the first thing they met was a question about LAST time — one they had to answer to
        // reach the composer, with the "wrong" answer costing them a conversation they might have
        // wanted. During this feature's own drives it fired every single launch.
        //
        // It could only ever offer ONE session, the newest unfinished one. Everything older was
        // unreachable, so the dialog was not a way into your sessions — it was a way into exactly
        // one of them, presented as though it were the choice.
        //
        // And it made "resume" a thing that happens TO you rather than something you ask for. Now
        // /sessions lists them and --resume opens one; this line only says the door exists.
        void HintAtResume()
        {
            var here = sessions.List(session.WorkingDirectory).Count;
            if (here == 0) return;

            // THE UNFINISHED ONE IS WORTH NAMING, because "ended without closing" is the case where
            // someone lost work and is looking for it. Everything else is just a count.
            var unfinished = sessions.LoadLatestUnfinished(session.WorkingDirectory);

            if (SessionsCommand.StartupHint(here, unfinished?.Context.Count) is { } line)
                transcript.Write(line);
        }

        int code = system.Run();

        // ENDED PROPERLY, so do not offer it back. Reaching this line is the only evidence available
        // that the process was not killed mid-session — which is precisely what makes an unfinished
        // row mean something.
        // ONLY IF THERE IS SOMETHING TO COME BACK TO. A session is written per turn, so one where
        // nothing was said was never stored — pointing at it would hand the user a command that
        // reports "no session matches" and makes resume look broken on its first use.
        var endedSessionId = runner?.HasSavedTurn == true ? runner.SessionId : null;
        runner?.MarkSessionFinished();
        // I1 #1: AgentHost.Dispose releases EVERY scheduler this session's runner ever created (each
        // one's CancellationTokenSource + two SemaphoreSlims) — without this they leak for the rest of
        // the process's lifetime, since (as of review round 2's N2 fix) AgentHost no longer disposes
        // schedulers one-at-a-time as goals swap; this is now the only release point.
        mainWindow.Dispose();   // stops the panel clock
        runner?.Dispose();

        // THE SESSION OWNS THE SERVERS, so this is where they end. An orphaned child outlives the
        // app and holds whatever it had open — the only failure in this feature that survives the
        // process. Best-effort: shutdown is not a place to throw or to wait indefinitely.
        try { mcp.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* best effort: shutdown is not a place to hang */ }

        // HOW TO COME BACK, printed AFTER the TUI has released the terminal so it stays on screen
        // rather than being painted over by the last frame.
        //
        // THIS IS THE MOMENT THE ID IS WORTH SOMETHING. Everywhere else it is an implementation
        // detail; here it is the one thing that turns "I closed that by accident" into a command.
        // Costless to ignore — a line of grey text on a terminal the user is already leaving — and
        // the alternative is finding out that resume exists by reading the documentation of an app
        // you have already stopped using.
        if (endedSessionId is { Length: > 0 } id)
            Console.WriteLine(SessionsCommand.ExitHint(id));

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
            ? $"Configuration saved. Model: {ModelLabelOf(reResolved)}. Type a goal and press Enter."
            : "Configuration saved, but it did not load cleanly: " + string.Join("; ", reResolved.Errors));
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

    /// <summary>
    /// Asks before wiping usage history, with the confirmation IN the transcript.
    ///
    /// <para>A MESSAGE WITH ACTIONS, NOT A MODAL. The transcript control carries a footer of buttons
    /// per message (<c>SetActions</c>), so the question lives where the answer will appear — no popup
    /// stealing focus, no dialog covering the numbers the user is deciding about. The destructive
    /// choice is <see cref="ChatActionVariant.Danger"/> and the safe one is default, so the visual
    /// weight matches the consequence rather than the reading order.</para>
    ///
    /// <para><see cref="ChatActionAfterPress.Hide"/> on both: once answered, the buttons go. A
    /// confirmation that stays pressable is one a user can answer twice, and the second press acts on
    /// a question that was already settled.</para>
    /// </summary>
    private static void ConfirmClearHistory(MainWindow mainWindow, UsageHistoryStore history)
    {
        var rows = history.TotalRows();
        var sessions = history.SessionsSince(DateTimeOffset.UtcNow.AddYears(-10)).Count;

        var id = mainWindow.Chat.AddMessage(ChatRole.System,
            StatsCommand.ConfirmText(rows, sessions));

        // NOTHING TO DELETE MEANS NO BUTTONS. Offering a destructive action that would do nothing
        // teaches a user the button is harmless.
        if (rows == 0) return;

        mainWindow.Chat.SetActions(id,
        [
            new ChatMessageAction
            {
                Id = "clear",
                Label = "Delete history",
                Variant = ChatActionVariant.Danger,
                AfterPress = ChatActionAfterPress.Hide,
                OnClick = ctx =>
                {
                    try
                    {
                        history.Clear();
                        ctx.SetStatus($"{rows:N0} records deleted", SharpConsoleUI.Core.NotificationSeverity.Success);
                    }
                    catch (Exception ex)
                    {
                        // Clear is the one history operation that THROWS rather than swallowing —
                        // a delete that silently failed would leave the user believing their history
                        // is gone when it is not.
                        ctx.SetStatus($"could not clear: {ex.Message}", SharpConsoleUI.Core.NotificationSeverity.Danger);
                    }
                },
            },
            new ChatMessageAction
            {
                Id = "keep",
                Label = "Keep",
                AfterPress = ChatActionAfterPress.Hide,
                OnClick = ctx => ctx.SetStatus("history kept", SharpConsoleUI.Core.NotificationSeverity.Info),
            },
        ]);
    }
}
