using CxAgent.Core.Commands;
using CxAgent.Core.Helpers;
using System.Reflection;
using CxAgent.Core.Sessions;
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
    /// <summary>
    /// The catalog this process runs on, resolved once.
    ///
    /// <para>--model OVERRIDES defaultProvider FOR THIS RUN ONLY, without touching config. Same rule
    /// /model follows: naming an instance that is not configured returns null so the caller can stop,
    /// rather than falling back — silently starting on the model the user was trying to avoid is the
    /// worst of the available outcomes.</para>
    ///
    /// <para>A METHOD RATHER THAN TWO BRANCHES AT THE CALL SITE, so the result can be bound by an
    /// initialiser and never assigned again. See the caller for why that matters here.</para>
    /// </summary>
    private static ResolvedConfig? ResolveStartup(AppPaths paths,
        IReadOnlyDictionary<string, string> env, bool useMock, string? instance) =>
        instance is { } wanted && !useMock
            ? ConfigResolver.ResolveInstance(paths, env, wanted)
            : ConfigResolver.Resolve(paths, env, useMock);

    private static string ModelLabelOf(ResolvedConfig resolution)
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
    /// <para>Public because a front end is a consumer, not an internal: the InternalsVisibleTo grant
    /// in AssemblyWiring covers Core's assemble members, not the app's own entry point.</para>
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
            // THE COMPLAINT, THEN THE OPTIONS. Someone who mistyped a flag has just demonstrated they
            // do not know the flags; an error alone sends them looking for help, which is a step some
            // never take. Both go to stderr so a script redirecting stdout still sees why it failed.
            Console.Error.WriteLine($"cxagent: {error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage.Text);
            return 2;
        }

        // --help PRINTS AND EXITS, like --version: a question about the binary, answered without
        // reading config or building a window, so it works even when the app cannot start.
        if (options.ShowHelp)
        {
            Console.WriteLine(Usage.Text);
            return 0;
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
        // A WORKING MODE, not a bare AgentMode: the edits axis joins it below when a resumed session
        // carries one. The implicit widening keeps --mode meaning exactly what it did.
        WorkingMode startupMode = options.Mode;
        // --config-dir OVERRIDES EVERYTHING, including XDG_CONFIG_HOME, because it is the more
        // specific instruction: someone who typed a path on the command line means that path.
        // Null falls through to AppPaths' own resolution, so the default is untouched.
        var paths = new AppPaths(options.ConfigDir);
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

        // RESOLVED ONCE, AND NEVER REBOUND. This was a mutable local that four sites repointed —
        // startup, --model, F5's save, and /model — with every other consumer in this method closing
        // over the VARIABLE. Correctness then depended on when each consumer happened to read, which
        // is not a property anything can check: reading too early kept a stale record (the auto
        // classifier consulted a provider config no longer described, silently, because the mode
        // still worked), and reading too late compared a record with itself (the MCP reload was
        // gated on `resolution.McpServers` after `resolution` had already become the new value —
        // always equal, so the reload never ran once, and it compiled clean and shipped).
        //
        // `readonly` in a local function's closure is not expressible, so this is a local that is
        // simply never assigned again — every later site now takes it as a PARAMETER or reads it as
        // the process's fixed catalog. The two startup cases fold into one expression below so that
        // there is no window where it holds a value somebody could observe and then see change.
        //
        // WHAT THIS IS NOT: it is not the active model. That is session state (Session.InstanceName)
        // and /model changes it freely — see SwitchModel. Conflating "the catalog this process was
        // started with" and "the model this session is talking to" into one variable is what made
        // both of the above bugs possible; they are different lifetimes and now different things.
        //
        // --model OVERRIDES defaultProvider FOR THIS RUN ONLY, without touching config. Same rule
        // /model follows: naming an instance that is not configured stops rather than falling back,
        // because silently starting on the model the user was trying to avoid is the worst outcome.
        // ASSIGNED ONCE, BY CONVENTION — and the convention is not compiler-enforced, which is worth
        // stating plainly rather than implying otherwise. C# has no readonly local, a local captured
        // by a closure cannot be one, and Run is static so there is no readonly field to hold it
        // either. `resolution = something` further down would still compile today; it was tried, and
        // it did.
        //
        // What the helper buys is smaller and real: the declaration and the value are one line, so
        // there is no `ResolvedConfig resolution;` sitting empty inviting branches to fill it,
        // and the two startup cases cannot drift apart. Enforcement here is the comment above and
        // the fact that no consumer needs a rebind any more — F5 restarts, /model passes its own
        // record. If a fourth bug of this shape ever appears, the answer is a wrapper type with a
        // genuinely readonly field, not a stronger comment.
        var startup = ResolveStartup(paths, env, useMock, options.Instance);
        if (startup is null)
        {
            Console.Error.WriteLine($"cxagent: no provider called '{options.Instance}' in config.");
            return 2;
        }

        var resolution = startup;

        var driver = new NetConsoleDriver();
        var system = new ConsoleWindowSystem(driver,
            new ConsoleWindowSystemOptions(InstallSynchronizationContext: true, ShowTopPanel: false, ShowBottomPanel: false));

        // THE PALETTE, EXPRESSED IN WHATEVER THEME IS ACTIVE — before any window is built, because
        // MainWindow reads ColorScheme's surfaces in its own field initialisers and a palette derived
        // after that point would leave the chrome painted in the defaults while everything built
        // later used the theme.
        //
        // AND AGAIN ON EVERY CHANGE, which is the whole point of the picker: surfaces are PAINTED, so
        // re-deriving and repainting shows the new theme at once. Text already written to the
        // transcript keeps the colours it was written with — see the theme spec; scrollback is
        // append-only and re-rendering it would fight that for no gain.
        // CXAGENT'S OWN GROUND, REGISTERED AND MADE ACTIVE FIRST. ModernGray's background is lighter
        // than the near-black this app was designed against, so deriving straight from it would have
        // lightened every panel on upgrade. See CxAgentTheme for why that is a theme rather than a
        // multiplier applied to whatever theme is active.
        // PRECEDENCE: the argument beats config, and both are ignored when theme selection is off.
        // An explicit --theme is a decision the user made for THIS run, so it outranks the file; the
        // gate exists so the whole route can be closed without touching either.
        CxAgentTheme.Install(system,
            Features.ThemeSelection ? options.Theme ?? resolution.Theme : null);

        ColorScheme.DeriveFrom(system.ThemeStateService.CurrentTheme);
        var logs = new LogFileManager(paths);

        // THE RESUME BUFFER. Built before the host so it can be handed in at construction, and
        // pruned once here rather than on a timer: startup is the only moment nothing is mid-turn,
        // and finished sessions are the only rows old enough to be worth dropping.
        var sessions = new SqliteSessionStore(paths);
        sessions.Prune(SqliteSessionStore.DefaultRetention);

        // USAGE HISTORY — a different file, and NOT pruned. The resume database above is a buffer
        // whose rows are worthless once a session ends cleanly; this is the archive, and pruning an
        // archive on startup would delete the answer to "where did last month go" every time the app
        // opened.
        var history = new UsageHistoryStore(paths);

        // CONSTRUCTED BEFORE THE WINDOW, because the remembered edit mode below has to be resolved
        // before MainWindow's StartupMode banner is written — that banner is a chat message and
        // cannot be revised, so a mode restored after it would be announced wrong for the rest of
        // the session.
        var session = new Core.Sessions.Session(Path.GetFullPath(Environment.CurrentDirectory));
        var permissionRules = new PermissionRulesStore(paths);
        string? migrationNotice = null;

        // FOLD THE SCOPES TWO OLD BUGS LEFT BEHIND, before anything reads trust or rules. Pre-identity
        // scopes were bare paths; ctime-era scopes moved every time the agent wrote a file. Both
        // stranded the grants and trust recorded under them, which is why a folder already trusted
        // asked again. Idempotent, so it costs one no-op pass on every later launch.
        //
        // The file is copied to permissions.json.premigration first — this rewrites decisions the
        // user made, and "it deleted my grants" must be answerable with a file rather than a claim.
        try
        {
            var migration = PermissionScopeMigration.Run(permissionRules);
            if (migration.ChangedAnything)
                migrationNotice = $"[dim]permissions: folded {migration.Scopes} stale folder "
                    + $"{(migration.Scopes == 1 ? "scope" : "scopes")} from an older identity scheme "
                    + $"({migration.Trusted} trust {(migration.Trusted == 1 ? "decision" : "decisions")} "
                    + $"kept, {migration.Rules} {(migration.Rules == 1 ? "rule" : "rules")} moved). "
                    + "Previous file saved as permissions.json.premigration.[/]";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be rewritten still works, it just keeps asking. Never fatal.
        }

        // THE FOLDER'S REMEMBERED EDIT MODE, restored exactly — including the permissive ones. This
        // is the one place cxagent lets a past decision widen a later session without an act in it,
        // and it is deliberate: re-picking a mode on every launch is friction that buys no safety,
        // because TRUST still floors what any mode can do. A restored AcceptEdits/Auto on a folder
        // whose trust is Unknown asks anyway.
        //
        // BEFORE the resume override further down, not after: a resumed session's own recorded mode
        // is the more specific fact — it says what this conversation was left in, not merely what
        // the folder usually runs as.
        //
        // Only the edits axis. --mode carries AgentMode alone, so there is no CLI value to conflict
        // with here; if the edits axis ever gets a flag, the flag must win over this.
        if (permissionRules.GetEditMode(session.WorkingDirectory) is { } rememberedEdits)
            startupMode = startupMode with { Edits = rememberedEdits };

        // THE SAME FALLBACK SessionFactory APPLIES, applied here too because the BANNER cannot be
        // revised. SessionFactory flips fan-out to single when the `agent` tool is withheld, but it
        // runs after the window is built and corrects its own copy of the mode — so the session was
        // genuinely in single mode while the status line read "fan-out" for its whole life. Seen on
        // a live drive against a config carrying `-agent`: the notice printed, the mode line
        // disagreed with it three rows below.
        //
        // NOT A SECOND GUARD: both call the one Offers, so there is no second rule to drift. What is
        // duplicated is WHEN it is asked, which is forced by the banner being written once.
        if (startupMode.CanDelegate
            && !CxAgent.Core.Plugins.ToolSelection.Offers(resolution.Tools, CxAgent.Core.Plugins.Tool.Agent))
            startupMode = startupMode with { Agent = CxAgent.Core.Agents.AgentMode.Single };

        var mainWindow = new MainWindow(system, resolution, logs)
        {
            // BEFORE Build(), because the banner it writes is a chat message and cannot be revised.
            // The SetMode call further down still fixes the composer line on every /mode; this is the
            // one readout that has to be right the first time.
            StartupMode = startupMode,
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

        // SEEDED WITH THE RESOLVED MODE, not the constructor default. The policy is what actually
        // decides whether a write asks, and it was only ever assigned on a LATER /mode or Shift+Tab —
        // so a restored AlwaysAsk would have shown in the status bar while the policy still ran at
        // AcceptEdits, which is the dangerous direction of that mismatch.
        var permissionPolicy = new PermissionPolicy(session.WorkingDirectory, permissionRules,
            startupMode.Edits);
        // The UI's own transcript writer. The control it wraps is created with mainWindow above and
        // never replaced, so there is no later lifetime to
        // chase: every caller below can hold this one instance for good.
        var transcript = new TranscriptWriter(system, mainWindow.Chat);
        // NO SESSION IN IT. The gate is the process's — a rules store and a way to ask — and every
        // decision reads the policy carried on the request instead. That is what lets one gate serve
        // any number of sessions honestly.
        var permissionGate = WindowPermissionPrompt.Gate(system, mainWindow, permissionRules, transcript);

        // THE CLASSIFIER, WHEN ONE IS CONFIGURED. Resolved from the same registry every other
        // instance comes from, so a classifier is an ordinary provider entry and its spend is
        // attributed like any other. Null here is the whole gate on `auto`: unconfigured means the
        // mode is not listed, not cyclable, and not parseable, so this is never consulted.
        // Guards the LoadError echo below so it is reported once, on the FIRST WireRunner call
        // only — F5/F7/F8 re-wires reuse this same permissionRules instance, and its LoadError
        // describes what happened at construction, not live state, so repeating it on every
        // re-wire would just be noise about an event that already happened and was already told.
        var permissionLoadErrorReported = false;

        using var cts = new CancellationTokenSource();


        // The currently-open consolidated Settings dialog, or null when none is open. Captured by the
        // Escape global shortcut (routes Escape to Cancel while a dialog is open) and by
        // OpenSettingsAsync (reentrancy: a second F5/F7/F8 press selects a page in this instance rather
        // than opening a second dialog). Cleared in OpenSettingsAsync's `finally` — see its comment.





        // Owned by the SESSION, not by any one AgentHost: a re-wire swaps the model and must not
        // kill the servers. Assigned below, before the first WireRunner, and read by every host it
        // builds thereafter.
        // The running turn's cancellation scope, or null when nothing is running. Replaced on every
        // WHETHER THIS FRONT END'S OWN CONTINUATION IS STILL ON THE STACK — not whether a turn is
        // running, which is the session's answer and is asked of it directly now.
        //
        // THE DIFFERENCE IS THE FALLING EDGE, and it is why this survives: the continuation clears
        // this the moment a turn ends, while IsBusy is written by whichever thread ran the turn.
        // Both are true mid-turn; only this one is guaranteed false by the time the continuation's
        // next line runs, which is what the post-turn drain below depends on.
        //
        // WHAT LEFT. Deciding whether a typed line becomes a steer or a prompt moved to Session.Submit,
        // which reads IsBusy — that decision was the only thing enforcing "two SendAsync calls on one
        // agent append to ONE live Context.Messages", from a bool a second front end could not see.
        // The cancellation scope moved to AgentHost, beside the busy flag and the turn it belongs to.


        // Messages typed while a turn was running, in the order they were typed. Joined into ONE
        // prompt when the turn ends (D18) — appended, never replaced: two messages are usually one
        // thought completed, and dropping either half is silent data loss the user cannot see.

        // THE ONE BLOCK QUEUED MESSAGES SHARE, or null when nothing is queued. One row that updates
        // beats a row per message: three quick corrections are one thought, and three transcript
        // lines for them push the running turn's own output off the screen just when it matters.
        SharpConsoleUI.Controls.ChatMessageId? queuedBlock = null;

            // ONE BLOCK, REWRITTEN. Adding a message updates the row rather than appending a new
            // one, so a burst of corrections stays one line and the running turn keeps the screen.
            void ShowQueued(string? body)
            {
                if (body is not { Length: > 0 }) { RemoveQueuedBlock(); return; }

                var text = $"[dim]queued[/] {ChatTranscriptSink.Escape(body)}";

                // REMOVED AND RE-ADDED, not rewritten in place. UpdateMessage left the block wherever
                // it first appeared, so a turn producing output pushed it up the screen and the user
                // was appending to something they could no longer see. It is the most recent thing
                // they said; it belongs at the bottom, in the position the real message will occupy
                // when it goes in. Still ONE row either way — the cost this avoids was a row per
                // message, not a row that moves.
                RemoveQueuedBlock();
                queuedBlock = mainWindow.Chat.AddMessage(ChatRole.System, text);

                // CANCEL PUTS THEM BACK IN THE COMPOSER, not in the bin. What was typed was meant,
                // and the same Restore that Escape uses places it ABOVE anything typed since — the
                // queued thought came first, so it reads first.
                mainWindow.Chat.SetActions(queuedBlock.Value,
                [
                    new ChatMessageAction
                    {
                        Id = "cancel-queued",
                        Label = "Cancel · back to composer",
                        AfterPress = ChatActionAfterPress.Hide,
                        OnClick = _ => DrainQueuedToComposer(),
                    },
                ]);
            }

            // THE ONE PLACE A MODE CHOICE IS REMEMBERED. Shift+Tab and `/mode` are the same decision
            // reached two ways, and a second copy of this is the copy that forgets to persist.
            //
            // Best-effort: a folder whose mode cannot be written (read-only config dir, disk full)
            // must still switch mode for THIS session. Losing the memory is a smaller harm than
            // refusing the change the user just made — the same reasoning as the IOException guards
            // around the gate's own _store.Add.
            // THE ONE PLACE QUEUED TEXT GOES BACK. Escape and the Cancel action are the same act
            // reached two ways, and a second copy of this would be the copy that forgets to clear
            // the block or the list.
            // THE ONE PLACE THE BLOCK IS TAKEN DOWN. Both the send path and the cancel path remove
            // it, and a second copy of this is the copy that clears the id without removing the row.
            // THE ONE PLACE THE BLOCK IS TAKEN DOWN, and it takes its actions with it: RemoveMessage
            // tears down the message's siblings — actions toolbar, status bar, peek row — as well as
            // the panel.
            //
            // IT DID NOT ALWAYS. The toolbar is a SIBLING of the panel rather than a child, so
            // removing the message once left the cancel button on screen and still clickable. It
            // looked intermittent because ShowQueued removes and re-adds the block on every queued
            // line while queuedBlock only ever points at the newest one — so a second line orphaned
            // the first line's toolbar with nothing able to reach it. One line looked fine; two and
            // the buttons stayed. Fixed in SharpConsoleUI (RemoveMessage now removes every sibling),
            // which is where it belonged: any caller removing a message with a footer hit it.
            void RemoveQueuedBlock()
            {
                if (queuedBlock is { } block) mainWindow.Chat.RemoveMessage(block);
                queuedBlock = null;
            }

            // ASKS THE SESSION TO EMPTY THE QUEUE; the restoring is done by the Cancelled handler
            // below. One step here and one subscriber, rather than take-place-remove at every call
            // site — which is how a send path leaves the block sitting above a duplicate of itself.
            void DrainQueuedToComposer() => session.CancelPending();

            // WHAT THIS FRONT END DOES WITH TEXT HANDED BACK. The session does not know a composer
            // exists — it raises Cancelled and this decides. Placed ABOVE anything typed since,
            // preserving the order things were written in: the queued lines were typed first.
            //
            // REMOVED, not rewritten to a tombstone. The queue emptied, so the placeholder has nothing
            // left to stand for — and the text is sitting in the composer in plain view, so a row
            // saying so would explain something already on screen at the cost of a transcript line.
            session.Cancelled += text => system.EnqueueOnUIThread(() =>
            {
                mainWindow.Input.Input = PromptQueue.Restore(text, mainWindow.Input.Input);
                RemoveQueuedBlock();
            });

            // AND WHEN THE TURN TAKES IT, the stand-in has no referent. This was called from three
            // places that each had to remember; it is one subscription now, and the send path can no
            // longer leave the block above the real message.
            session.Drained += _ => system.EnqueueOnUIThread(RemoveQueuedBlock);

            // MARSHALLED HERE, NOT RAISED THERE. Core has no dispatcher and a headless subscriber
            // should not pay for one — the same division Session.Changed already uses.
            session.Pending += (whole, _) => system.EnqueueOnUIThread(() => ShowQueued(whole));


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


        // THE PROCESS'S SESSIONS AND WHAT THEY SHARE, built ONCE. WireRunner runs again on every F5,
        // F7 and /model. Constructing a fresh SharedServices record each time from these
        // same locals — harmless while the members are identical, and exactly the shape that stops
        // being harmless when a second session exists and one re-wire hands it a different record.
        //
        // Over() rather than Create(): the stores here are built before the window (the resume
        // prune has to happen at startup) and the gate cannot exist until there IS a window, so the
        // root assembles them in the order the UI forces and hands the result over. Create() is for
        // a caller with no such ordering — a headless host.
        var manager = Core.Sessions.SessionManager.Over(
            new Core.Sessions.SharedServices
            {
                Logs = logs,
                Resume = sessions,
                History = history,
                Mcp = mcp.Toolset,
                Gate = permissionGate,
                GlobalInstructionsDir = paths.ConfigDir,
            },
            permissionRules);

        // THE TWO ONLY A FRONT END CAN SERVICE. Everything else acts on a session or on the
        // manager's own stores and is seeded in Core; these need a window and a message loop, which
        // is why they are contributed here rather than declared as a category. The session parameter
        // is ignored today and will not be once a process has tabs — a key map describing a session
        // other than the one on screen is worse than none.
        foreach (var declared in SessionCommands.All)
        {
            if (declared.Name == "/help")
                manager.Commands.Register(declared, (_, _) => { mainWindow.ShowHelp(); return true; });

            // OVER THE MANAGER'S. It registered /stats for REPORTING, which is all Core can do;
            // clearing rewrites history and must be confirmed, which needs somebody to ask. Last
            // registration wins, so this one takes the whole command and delegates the reporting
            // half straight back.
            if (declared.Name == "/stats")
                manager.Commands.Register(declared, (session, arguments) =>
                {
                    if (!StatsCommand.IsClear(arguments)) return session.SayUsage(arguments).Handled();

                    ConfirmClearHistory(mainWindow, history);
                    return true;
                });

            // THE LAST FOUR. Each needs something this process owns rather than any session — the
            // config paths /model resolves against, the rules store and classifier /mode reads, the
            // toolset /mcp reloads, the resume store /sessions lists. They are registered here for
            // that reason, not because they are UI: the work they do is already a session's or the
            // manager's, and what stays is the lookup of collaborators only a composition root has.
            if (declared.Name == "/mcp")
                manager.Commands.Register(declared, (session, arguments) =>
                {
                    // FIRE AND FORGET: a reload connects servers and the caller does not wait.
                    // The session parameter is NAMED rather than discarded, because `_ = …` on the
                    // next line would then assign to it instead of discarding the Task.
                    _ = session;
                    _ = mcpCommand.HandleAsync(arguments);
                    return true;
                });

            if (declared.Name == "/exit")
                manager.Commands.Register(declared, (_, _) =>
                {
                    cts.Cancel();
                    system.Shutdown();
                    return true;
                });
        }

        void WireRunner(ResolvedConfig res)
        {
            if (!res.HasProvider) return;

            // REBOUND ON EVERY WIRE, from THIS resolution. Binding once at startup would mean
            // changing `classifier` in config and pressing F5 left `auto` mode consulting the old
            // provider — silently, because the mode still worked. That is the shape of every bug in
            // this method: a re-wire that moves some consumers of a resolution and not others, with
            // nothing marking which is which.
            //
            // CLEARED WHEN NOTHING IS CONFIGURED, not left behind. Removing the classifier entry and
            // re-wiring must actually turn `auto` off; leaving the old instance would keep a mode
            // alive that config no longer describes.
            permissionGate.BindClassifier(res.ClassifierInstance, res.Providers);

            // The outgoing host is disposed by Session.ReplaceHost below, not here: a re-wire that
            // merely reassigned would leak it, and that is a step a caller can forget while the host
            // is a bare local.
            var sink = new ChatTranscriptSink(system, mainWindow.Chat)
            {
                // A STEER TAKEN MID-TURN ARRIVES AS A USER TURN, and its placeholder must go with it.
                // The agent announces through this sink from its own flow; the removal is marshalled
                // onto the UI thread by the sink itself, so this runs where the controls live.
                BeforeUserTurn = RemoveQueuedBlock,
            };
            // The row and the agent must agree from the first frame — a status line that is right
            // only after the user touches something is a status line nobody trusts.
            mainWindow.SetMode(startupMode);
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
            // The failed-job buttons. Delegates rather than an AgentHost reference, because the host
            // is built BELOW this line and REPLACED on every re-wire — capturing the instance would
            // pin whichever one existed when this sink was built. Reading through the closure is the
            // same pattern every other handler here uses, and it is why the session rather than the
            // host is what everything holds now.
            //
            // No inline failure actions. Retry/Skip/Diagnose let the user drive the scheduler by
            // hand while the orchestrator was mid-drive -- "a drive operation is already in
            // progress" on screen -- and a hand-skipped job desynchronised the plan from what the
            // orchestrator believed had run. The failure and its reason reach the model on the next
            // consult, which already has a repair round.
            var jobPanelSink = new InlineJobSink(system, mainWindow.Chat);

            // AND BACK TO THE WINDOW, so its one-second clock can tick the elapsed time on running
            // rows. Assigned here rather than passed in because the sink needs the window's Chat
            // control to exist first — see MainWindow.JobSink. Re-wiring (/model, resume) builds a
            // new sink and overwrites this, which is what we want: the clock must drive whichever
            // sink actually owns the rows on screen.
            mainWindow.JobSink = jobPanelSink;

            // THROUGH THE MANAGER, not SessionFactory directly. Wiring outside it left the manager's
            // collection empty while a session was plainly running, which Adopt() was added to paper
            // over — the session went in afterwards, and nothing checked that what the root wired
            // matched what the manager would have. One routine now does both, so the collection
            // cannot disagree with reality. Idempotent on re-wire: /model and resume call this again
            // with the same Session, and Open adds it only if it is not already there.
            manager.Open(session, res,
                new Core.Sessions.SessionPorts
                {
                    Observer = sink,
                    ToolObserver = jobPanelSink,

                    // OUR OWN TOOL, and the reason this extension point exists. Core cannot render a
                    // diff — it has no transcript and no markup dialect — but this layer does, so
                    // the tool is supplied from here and Core stays ignorant of what a diff is.
                    // SessionFactory wraps it in GatedAgentTool on the way through.
                    Tools = [new Tools.ShowDiffTool(session.WorkingDirectory)],
                    Ask = mainWindow.AskQuestionAsync,

                    // JUDGED BY ITS OWN ROOT AND MODE. The gate is one per process; this is the
                    // session half of the decision, and passing it is what stops a second session
                    // being judged against this one's folder.
                    Policy = permissionPolicy,
                },
                startupMode);

            // Non-fatal config complaints — a server entry we could not read. Said once, here,
            // because a skipped server the user never hears about is indistinguishable from one
            // that is merely slow to connect.
            foreach (var warning in res.Warnings ?? [])
                transcript.Write($"[yellow]{warning}[/]");

            // PERMISSION DECISIONS INTO HISTORY. Set here rather than at the gate's construction
            // because the session id does not exist until the host does — and reassigned on every
            // re-wire (F5 changes provider), so the hook reads the session lazily rather than closing
            // over the id of a host that has since been replaced.
            permissionGate.OnDecision = report =>
                history.SavePermission(new PermissionRecord(
                    session.SessionId ?? "unknown", DateTimeOffset.UtcNow,
                    report.Kind.ToString(), report.Decision, report.Requester,
                    session.WorkingDirectory, report.Subject, report.Flagged));

            session.TokensUpdated += (_, total) => system.EnqueueOnUIThread(() =>
            {
                // THE PARENT'S OWN SPEND, not `total`. The event carries Ledger.TotalTokens, which is
                // the whole session — children share the ledger — and the status bar is this agent's
                // readout: it sits beside an occupancy percentage that is the parent's, so a
                // session-wide figure there read as the parent's and was four times too large.
                var (ownIn, ownOut) = session.OwnSpend;
                mainWindow.SetTokenTotal(ownIn + ownOut);
                mainWindow.SetTokenSplit(ownIn, ownOut);

                // THE SAME EVENT, so the breakdown and the number it breaks down can never disagree.
                // Pushed rather than pulled: the panel refreshes on a clock too, and a stale tally
                // beside a live total is the kind of small inconsistency nobody can explain later.
                //
                // The PANEL keeps the session-wide figures — that is the division: bar is this agent,
                // panel is everything, and "Tokens by agent" is where the two are reconciled.
                // LEDGER IS NULLABLE because a session before its first wire has none; this runs from
                // a token event, which only a wired session raises.
                if (session.Ledger is not { } spend) return;

                mainWindow.SetSpend(new MainWindow.SpendReading
                {
                    ByInstance = spend.ByModel,
                    SubAgentTokens = spend.SubAgentTokens,
                    SplitByInstance = spend.SplitByModel,
                    CacheHitRate = spend.CacheHitRate,
                    CacheByAgent = spend.CacheHitRateByAgent,
                    CacheWrittenTokens = spend.CacheWrittenTokens,
                    CostByInstance = spend.CostByInstance,
                    TotalCost = spend.TotalCost,
                });
            });
            session.ContextUsedUpdated += (_, used) => system.EnqueueOnUIThread(() => mainWindow.SetContextUsed(used));
            session.ContextCompressed += (_, d) => system.EnqueueOnUIThread(() => mainWindow.MarkContextStale(d.Before, d.After));
            session.ContextEstimatedUpdated += (_, used) => system.EnqueueOnUIThread(() => mainWindow.SetContextUsed(used, estimated: true));
            // ONCE, AT WIRE-UP. The agent's id is fixed for its life, so there is nothing to wait for
            // and nothing to re-raise — a per-prompt subscription would fire on every
            // prompt because every prompt minted a new id.
            mainWindow.SessionId = session.SessionId ?? "";
            mainWindow.RefreshSessionPanel();
            session.TurnCompleted += (_, calls) => system.EnqueueOnUIThread(() =>
            {
                mainWindow.SessionPanel.RecordTurn(calls);
                // THE PARENT'S SPLIT, matching the total beside it. The ledger's InputTokens and
                // OutputTokens include every child, and a bar showing a session-wide ↑/↓ under a
                // parent-only total would be two figures that cannot be added together.
                var (turnIn, turnOut) = session.OwnSpend;
                mainWindow.SetTokenSplit(turnIn, turnOut);

                // SKILLS, RE-READ EVERY TURN like the agent's own discovery — a skill added or
                // edited mid-session shows up here on the same turn its description reaches the
                // prompt, rather than after a restart.
                //
                // The LOADED list is derived from the parent's window, so it empties itself when
                // compaction removes a body. That silent stop is the thing worth showing.
                mainWindow.SkillCount = Core.Skills.SkillCatalog
                    .Find(session.WorkingDirectory, paths.ConfigDir).Skills.Count;
                mainWindow.LoadedSkills = session.LoadedSkills;

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

                // THE EDIT MODE COMES BACK WITH THE CONTEXT. Resuming into the accept-edits default a
                // session that was deliberately left in always-ask would silently undo the user's
                // decision — and widening must be an act, never a side effect of continuing.
                //
                // ABSENT RESOLVES TO ALWAYS-ASK, not to the default: a row written before the column
                // existed says nothing about what the user chose, and absent is not permission.
                startupMode = startupMode with { Edits = snapshot.Edits ?? EditMode.AlwaysAsk };

                // AND THE POLICY WITH IT. The policy was seeded from startupMode before this block
                // ran, so a resume that narrows the mode has to narrow the thing that enforces it —
                // otherwise the status bar would say always-ask while writes stayed silent.
                //
                // DIRECTLY, NOT THROUGH Session.SetMode, and this is the one place that is correct:
                // this runs before the first wire, so there is no host to move and SetMode would
                // refuse. The policy exists already because the gate is built before the session.
                permissionPolicy.Edits = startupMode.Edits;

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

        // THE ONE PART OF A RESUME ONLY THIS LAYER CAN BUILD, handed over once. The manager owns the
        // sequence — arm, rewire, retire the row, say so — and /sessions resume now runs entirely in
        // Core; without this hook it would have nothing to rebuild the host with, and the manager
        // refuses rather than arming a resume nothing applies.
        //
        // SET BEFORE THE FIRST WIRE, so a resume arriving on the first render pump finds it.
        manager.Rewire = () => WireRunner(resolution);

        WireRunner(resolution);   // startup path, unchanged in effect

        // AFTER THE WIRE, because the sink it writes to is created inside it.
        if (migrationNotice is not null)
            transcript.Write(migrationNotice);
        if (resumeNotice is not null)
            transcript.Write(resumeNotice);

        // Submit model: plain Enter SUBMITS, and a line ending in a BACKSLASH continues onto the
        // next one — the shell's own convention, and Claude Code's.
        //
        // Shift+Enter was the first answer and it does not work here. The reasoning was that it is
        // "the chat-UI convention, portable across terminals", but that is a GUI assumption: most
        // Unix terminals send a bare '\r' for Enter with no modifier bits at all, so Shift+Enter and
        // Enter are the same byte and the app cannot tell them apart. Documented in three
        // places and reachable in none. (Ctrl+Enter fails for exactly the same reason, which the
        // original comment already noted without following the observation through.)
        //
        // A trailing '\' needs no modifier to survive, which is the whole point: it is IN THE TEXT.
        // Every terminal delivers it, and any user who has continued a shell command already knows
        // it. We intercept Enter in PreviewKeyPressed (fires BEFORE the focused control) and set
        // e.Handled so the MultilineEditControl never inserts its own newline; we insert the newline
        // ourselves when the text ends in a backslash. Gated to when the composer has focus, so Enter in the job panel (expand
        // block) still works. Registered UNCONDITIONALLY (not just when a provider is configured at
        // startup) because it reads through the session, so a provider wired in later via
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

        // DECLARED BEFORE THE KEY HANDLER because the handler must forward to it, and assigned later
        // where mainWindow exists. A desktop portal does NOT capture keys on its own here — the theme
        // list drew, took no arrows and left the cursor in the composer until this forwarding existed.
        ThemePortal? themePicker = null;

        window.PreviewKeyPressed += (_, e) =>
        {
            // THE THEME LIST SWALLOWS EVERY KEY WHILE OPEN, so arrows move its selection rather than
            // scrolling the transcript beneath it. Same contract cratis's status-bar chooser uses.
            if (themePicker is { IsOpen: true } picker)
            {
                picker.ProcessKey(e.KeyInfo);
                e.Handled = true;
                return;
            }


            // SHIFT+TAB CYCLES THE EDIT AXIS ONLY. Delegation changes what the model is offered and
            // what a turn may spend, so a keystroke beside the composer is the wrong weight for that
            // decision and it stays on /mode. Claude Code cycles only its permission dial for the
            // same reason.
            //
            // Gated on composer focus exactly as the Enter interception below is, so Shift+Tab keeps
            // its ordinary reverse-navigation meaning everywhere else in the UI.
            // SHIFT+TAB ONLY — PLAIN TAB MUST PASS THROUGH. This briefly accepted bare Tab, on the
            // reasoning that Shift+Tab arrives as its own escape sequence (CSI Z) and the modifier may
            // not survive the terminal. It does not survive tmux, which is true and was measured — but
            // swallowing plain Tab broke every DIALOG, because Tab is how their buttons are navigated.
            // The trust prompt at startup became unanswerable and the composer never got focus back:
            // the app looked alive and deaf. A shortcut that costs the startup dialog is not a
            // shortcut.
            //
            // System.ConsoleKey HAS NO Backtab MEMBER, so there is no second spelling to accept: a
            // terminal that reports CSI Z as Tab-without-Shift simply cannot reach this shortcut, and
            // /mode edits is the way in there. Losing a shortcut on some terminals is a smaller cost
            // than losing Tab everywhere.
            if (e.KeyInfo.Key == ConsoleKey.Tab
                && (e.KeyInfo.Modifiers & ConsoleModifiers.Shift) != 0
                && mainWindow.Input.HasFocus)
            {
                e.Handled = true;

                if (!session.HasAgent) return;   // no agent to set a mode on

                // A TURN IN FLIGHT IS DECLINED, the same predicate /mode uses: the tool list is fixed
                // once a request begins, and a silent flip mid-turn is exactly what that guards.
                    // THE CYCLE SKIPS AUTO WHEN NO CLASSIFIER IS CONFIGURED, so Shift+Tab never lands on
                // a mode that would do nothing. With one, the order runs strict -> permissive ->
                // reviewed, which reads as increasing autonomy.
                var nextEdits = session.Mode.Edits switch
                {
                    EditMode.AlwaysAsk => EditMode.AcceptEdits,
                    EditMode.AcceptEdits when permissionGate.Classifier is not null => EditMode.Auto,
                    _ => EditMode.AlwaysAsk,
                };

                // SAME ONE CALL AS /mode. Shift+Tab and the command are the same decision reached
                // two ways, and the pair of lines this replaced is the pair that gets copied with
                // the policy half missing.
                // NO REPAINT HERE. SetMode announces, and the subscription above follows — the
                // no repaint here: a repaint line beside it is the line a new command forgets.
                // ONE CALL, NOT TWO. SetMode remembers the preference itself, so there is no second
                // line here to be copied without its partner.
                session.SetMode(session.Mode with { Edits = nextEdits });

                // SAID OUT LOUD, because a keystroke that changes what runs without asking must not
                // be silent itself — and the composer line alone is easy to miss mid-flow.
                // THE SAME SENTENCE /mode PRODUCES. This carried its own, thinner one — "edits:
                // accept-edits." — which omitted what is ACTUALLY in force: on an untrusted folder
                // accept-edits changes nothing observable, and a readout that does not say so is
                // wrong exactly when it matters.

                return;
            }

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
            var goalText = mainWindow.Input.Input;
            if (string.IsNullOrWhiteSpace(goalText)) return;

            // NO PROVIDER BLOCKS A GOAL, NOT A COMMAND. This handler used to open with
            // `if (!session.HasAgent || !mainWindow.SubmissionEnabled) return;`, swallowing the
            // keystroke before anything looked at what was typed — so a session opened without a
            // working provider could not run /exit, /help, /stats, /sessions, or even /model, the
            // one command that FIXES having no provider. The window was unusable except by killing
            // it, and it said nothing about why.
            //
            // THE CLASSIFICATION ALREADY EXISTED. CommandOutcome says which commands need the model:
            // NeedsProvider (/compress) and NeedsTurn (/init). Its own documentation says the rest
            // "answer from state the app already holds, costing no tokens and no time" — the guard
            // simply was not asking.
            //
            // TWO FLAGS MEAN "NO PROVIDER", AND THE FIRST FIX ONLY MOVED ONE. It added the check
            // below but left `if (!mainWindow.SubmissionEnabled) return;` ABOVE it, and
            // SubmissionEnabled is set from resolution.HasProvider (MainWindow.cs:500) — so the
            // handler still returned before the classification ran and /exit still did nothing.
            // Reported against v0.4.2: text types into the composer, Enter does nothing. Both flags
            // now sit inside one check. SubmissionEnabled is safe here because it is not a busy
            // flag: its only other use is choosing the placeholder text.
            if (!session.HasAgent || !mainWindow.SubmissionEnabled)
            {
                var outcome = SessionCommands.Match(goalText)?.Outcome ?? CommandOutcome.NotACommand;
                if (outcome is CommandOutcome.NotACommand
                    or CommandOutcome.NeedsProvider or CommandOutcome.NeedsTurn)
                {
                    // SAY WHY, rather than dropping the keystroke. Silence here is what made this
                    // read as a frozen window rather than a session waiting to be configured.
                    mainWindow.Chat.AddMessage(ChatRole.System, outcome is CommandOutcome.NotACommand
                        ? "No model is configured. Run /model to pick one, or /exit to leave."
                        : "That command needs a model. Run /model to pick one first.");
                    mainWindow.Input.Input = "";
                    return;
                }
            }
            // RECORD IT FOR ↑/↓ OURSELVES. PromptControl records history inside its own Submit(),
            // which this handler pre-empts — we consume Enter before the control sees it, so nothing
            // would ever reach the history and the feature would be silently dead.
            mainWindow.Input.RecordHistory(goalText);

            // WHAT TO SHOW, when it differs from what is sent — see the NeedsTurn case below.

            mainWindow.Input.Input = "";   // clear the composer for the next goal
            mainWindow.RetireComposerPlaceholder();

            // ONE DISPATCH, DRIVEN BY THE OUTCOME. This was three ordered checks — IsCompress, then a
            // Match whose Quit case was an outcome and whose /help case was a NAME comparison, then
            // TryHandle — and the order between them was load-bearing without saying so. Adding a
            // command meant finding the right rung. Now the command's own outcome says who services
            // it, which is also what lets a menu dispatch a chosen row through this same path.
            // THE REGISTRY FIRST, BEFORE THE OUTCOME SWITCH. Core seeded what it can service and
            // this method registered what needs a window; a command that has finished moving is
            // handled here and never reaches the dispatch below — which matters because that
            // dispatch ends in a ShowHelp() catch-all, so a migrated command reaching it prints the
            // key map instead of doing anything.
            if (manager.Commands.TryRun(session, goalText)) return;

            if (SessionCommands.Match(goalText) is { } command)
            {
                switch (command.Outcome)
                {
                    case CommandOutcome.NeedsTurn:
                        // REWRITTEN INTO A PROMPT AND FALLING THROUGH to the ordinary goal path
                        // below — not handled here. Everything that path already does is exactly
                        // what this needs: the running-turn queue, the cancellation scope, the
                        // spinner, the token accounting. Starting a turn here instead would be a
                        // second submission route that has to relearn all of it.
                        if (command.Name == "/init")
                        {
                            // DECLINED WHILE A TURN RUNS — the session refuses and says so. Queued
                            // prompts are JOINED into one message, so an /init waiting behind two
                            // other instructions would reach the model as a paragraph of its briefing
                            // glued to unrelated work, attributed to the user besides.
                            //
                            // THE SESSION SENDS IT AND SAYS WHAT TO SHOW. The prompt and its echo used
                            // to be two locals threaded down separately — the briefing in goalText, the
                            // word "/init" in a turnEcho set here and read a hundred lines below — and
                            // splitting them is how a briefing ends up on the transcript as the user's
                            // own words. Session.Initialise carries the pair.
                            if (session.Initialise() is not Session.SubmitOutcome.Started init) return;
                            WhenTurnEnds(init.Turn);
                            mainWindow.RetireComposerHint();
                            return;
                        }
                        break;

                    case CommandOutcome.NeedsProvider:
                        // /compress means COMPRESS — it summarises through the model, exactly as
                        // auto-compression does, rather than deleting the oldest half. Truncation
                        // survives only as the fallback when that call fails. It is out here rather
                        // than in SessionCommands because that type is synchronous and provider-free
                        // by design — which is what keeps it testable without a window.
                        // THROUGH THE RUNNER, which owns the provider, the ledger and the job panel.
                        // Calling the compressor directly, with no job panel, could
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
                        // THE SESSION DECIDES AND SAYS SO. It refuses while a turn runs — see
                        // Session.CompressNow for why compaction is refused rather than queued —
                        // and announces the refusal through the observer, so there is nothing to
                        // report here.
                        _ = session.CompressNow(cts.Token);
                        return;
                }
            }

            // Everything else that begins with a slash — /clear, and any unrecognised command, which
            // gets the "available commands" reply rather than being sent to the model as a task.
            // Costs nothing: no goal, no provider call, no tokens.
            if (SessionCommands.TryHandle(goalText, out var commandReply))
            {
                if (commandReply is { Length: > 0 })
                    mainWindow.Chat.AddMessage(ChatRole.System, commandReply);
                return;
            }

            var disposition = session.Submit(goalText);
            if (disposition is not Session.SubmitOutcome.Started started) return;

            // Fire-and-forget on the UI-initiated flow; sync-context resumes continuations on the UI thread.
            // Retire the hint HERE, at submission — not when tokens first arrive. Tied to the token
            // readout it stayed on screen for the whole of a running request, telling the user to type
            // a prompt while the agent was several tool calls into one.
            mainWindow.RetireComposerHint();

            // THE PROCESS TOKEN, not a per-turn one. AgentHost links its own scope off whatever it
            // is handed, so Escape can cancel one turn while this still ends everything on Ctrl+Q or
            // /exit, without this layer owning a lifecycle that belongs to the turn.
            WhenTurnEnds(started.Turn);
        }

        // WHAT THIS FRONT END DOES WHEN A TURN ENDS — not how it runs, which is the session's now.
        // typed after the LAST tool barrier, which no barrier will reach.
        async void WhenTurnEnds(Task turn)
        {
            try
            {
                await turn;
            }
            finally
            {
            }

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
        // F4 IS BACK, and the reason it left was half right. It focused the composer, and was
        // removed as "a route back from a focus that can no longer happen" — true of the job panel it
        // was written for, which is gone. But two things still take focus away deliberately:
        // MainWindow.FocusQuestion moves it to a question's first option, and the permission prompt
        // moves it into the prompt panel. Both restore it on the way out; a user who reaches either
        // by another path, or a control that keeps focus after being dismissed, is left typing into
        // nothing with no way back that does not involve guessing at Tab order.
        //
        // A KEY THAT IS USUALLY UNNECESSARY IS STILL WORTH HAVING when the failure it covers is "the
        // keyboard does nothing and I cannot tell why". That is not a cost the user can debug.
        system.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F4, mainWindow.FocusComposer);

        // F9 OPENS THE THEME LIST, and the item at the left of the status bar opens the same one on a
        // click. One key for the whole registry rather than a key per theme: the list already knows
        // what is installed, and pinning three of them to three keys would go stale the moment a
        // seventh appeared.
        // RE-DERIVED AND RE-APPLIED ON EVERY SWITCH. Subscribed here rather than beside the initial
        // DeriveFrom because ReapplyTheme needs the window, which does not exist that early.
        system.ThemeStateService.ThemeChanged += (_, e) =>
        {
            ColorScheme.DeriveFrom(e.NewTheme);
            mainWindow.ReapplyTheme();
            // ForceFullRepaint, NOT ForceFullRedraw. The first re-emits EVERY cell and resets the
            // driver's front buffer; the second clears and invalidates, which still lets the diff
            // renderer skip cells it believes are unchanged. A theme change moves colours without
            // moving characters, so those skipped cells keep the OLD theme's escape sequences and
            // the screen ends up with stray ANSI scattered through it. Reported live.
            system.ForceFullRepaint();
        };
        // THE PICKER IS GATED; THE THEME IS NOT. CxAgentTheme is installed above regardless, and a
        // `theme` key in config still switches at startup — what this skips is the interactive
        // switcher. See Features.ThemePicker for why it is off.
        //
        // IN A LOCAL FUNCTION so a disabled flag compiles clean. Written inline, every line of it sat
        // behind `if (false)` and the compiler rightly called the whole block unreachable and the
        // picker variable never-assigned — four warnings for a feature that is merely switched off.
        void WireThemePicker()
        {
    // LOCAL ALIASES. Both are definitely non-null by the time this runs — it is called below, after
    // the window exists — but a local function is analysed independently of its one call site, so
    // the compiler cannot see that and warns on every use.
    var sys = system!;
    var win = mainWindow!;
    var picker = new ThemePortal(sys);
    themePicker = picker;
    picker.ThemeChosen += (_, name) =>
    {
        // The switch raises ThemeChanged, which re-derives ColorScheme and repaints — see where
        // that is wired above. All this has to do is keep the label honest.
        sys.ThemeStateService.SwitchTheme(name);
        win.SetThemeLabel(name);
        win.FocusComposer();   // hand the keyboard back to where the user was
    };

    // THE CARET FOLLOWS THE OVERLAY. Opening the list takes the composer out of editing mode so
    // its cursor stops blinking behind the portal; closing puts it back where the user left it.
    void ToggleThemePicker()
    {
        themePicker.Toggle();
        win.SetComposerEditing(!picker.IsOpen);
    }

    sys.RegisterGlobalShortcut(ConsoleModifiers.None, ConsoleKey.F9, ToggleThemePicker);

    // ARROWS, ENTER AND ESCAPE, THROUGH THE ONE ROUTE THAT REACHES A PORTAL. A desktop portal is
    // painted above the window but does NOT capture the keyboard here, and PreviewKeyPressed
    // fires too late to help — the list drew, took no keys, and stayed open on Escape. Global
    // shortcuts ARE consulted first, which F9 proved by working when nothing else did.
    //
    // The Func<bool> overload is what makes this safe: it consumes the key ONLY while the list is
    // open and declines otherwise, so arrows keep their ordinary meaning everywhere else rather
    // than being swallowed for the whole session.
    bool ToPicker(ConsoleKey key)
    {
        if (!picker.IsOpen) return false;
        picker.ProcessKey(new ConsoleKeyInfo('\0', key, false, false, false));
        win.SetComposerEditing(!picker.IsOpen);   // Enter/Escape may have closed it
        return true;
    }

    foreach (var key in new[]
             { ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.Enter, ConsoleKey.Escape })
    {
        var captured = key;
        sys.RegisterGlobalShortcut(ConsoleModifiers.None, captured, () => ToPicker(captured));
    }
    win.ShowThemeItem(
        sys.ThemeStateService.CurrentTheme.Name ?? CxAgentTheme.Name, ToggleThemePicker);
        }

        if (Features.ThemePicker) WireThemePicker();

        // F2 and F6 are GONE, and F6 was DEAD CODE the whole time.
        //
        // F6 diagnosed "whatever job has focus", resolved through FocusedJobId() — which walks the
        // focus path for a JobBlockControl. Those are created only by JobPanelControl, and the job
        // panel is never placed in the grid: jobs render INLINE in the transcript. So the lookup
        // always returned null and the key was a no-op in every mode, while Help advertised it. The
        // same flow is still reachable from the Diagnose button on a failed job's own block, which
        // addresses its job by id rather than by focus.
        //
        // F2 cleared the composer and refocused it. The clearing half is what made it redundant:
        // Ctrl+U does that, and history on the up-arrow made "empty the box" the rarer intent anyway.
        // The refocusing half came back as F4 above, on its own, where it is one job and not two.
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
        system.RegisterGlobalShortcut(ConsoleModifiers.Control, ConsoleKey.Q, () => { cts.Cancel(); system.Shutdown(); });
        // F9 Approve / Esc Discard — copilot mode's (P9) approve-or-discard gate. The session is read
        // through the closure (same pattern as every other handler here), so these track whichever
        // AgentHost WireRunner last installed. Both ApproveDraft/DiscardDraft are synchronous and
        // self-guard to a no-op when nothing is currently drafting (AgentHost.cs:162/174) — no
        // pending-approval pre-check needed here, and none of the other handlers in this
        // block pre-check their own preconditions either (F6 DiagnoseFocusedJob is the same shape).
        // Esc, not another F-key: this codebase has no OTHER Esc binding anywhere (grepped before
        // choosing it), so it's free, and Esc-to-cancel/dismiss is the universal convention — a
        // second F-key would be one more thing to memorize for no reason.
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

                // A PERMISSION PROMPT NEXT, for the same reason and with the same shape: Escape
                // answers it "no" and the run continues. It used to fall through to CancelTurn below
                // — a prompt only exists mid-turn — and cancelling fired the gate's registration,
                // which resolves the prompt as Deny anyway. So the key denied AND destroyed the run,
                // when Deny is a real answer the model can adapt to.
                if (mainWindow.TryDenyPermission()) return;

                if (EscapeRouting.For(session.IsBusy) is EscapeTarget.CancelTurn)
                    session.CancelTurn();
            });

        // MainWindow stays independent of SettingsDialog/SetupWizard; AppBootstrap supplies the flow
        // via these seams. F5/F7/F8 all route through the ONE consolidated handler below, differing
        // only in which page they land on and (F5 only) whether an absent/invalid config runs the
        // setup wizard instead of opening the dialog — see SettingsEntry.Classify.

        // Holds the currently-open SettingsDialog instance, if any — read by the Escape handler above
        // and by OpenSettingsAsync's reentrancy check just below. Null whenever no dialog is open;
        // OpenSettingsAsync's `finally` is what guarantees that, so Escape is never left pointed at a
        // closed dialog.
        // THE WIZARD IS FIRST-RUN ONLY NOW. The settings dialog it used to sit beside is gone: since
        // config stopped being applied in place, that dialog wrote a file and asked for a restart —
        // 680 lines of editor for a job a text editor does better, over a file the user can open
        // directly. What could not be replaced by an editor is the FIRST run, where there is no file
        // to open and no schema to guess from, so that is what survives.
        //
        // Reached from exactly one place: startup with no usable provider.
        async Task RunFirstRunSetupAsync()
        {
            await RunSetupFlowAsync(system, mainWindow, paths, env, WireRunner, cts.Token);
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
            await RunFirstRunSetupAsync();
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

            // THE UI FOLLOWS THE SESSION, rather than each command remembering to repaint. Every
            // place that changed a mode or a model used to call SetMode/SetResolution on the line
            // after — which is a line a new command can forget, and a second front end would have
            // had to know to write at all. The session announces what moved; this reads the new
            // state off the session, which it already holds.
            //
            // MARSHALLED: a change can land from a turn's own thread, and these touch controls.
            session.Changed += kind => system.EnqueueOnUIThread(() =>
            {
                if (kind is Core.Sessions.SessionChangeKind.Mode)
                    mainWindow.SetMode(session.Mode);

                if (kind is Core.Sessions.SessionChangeKind.Model && session.Resolution is { } current)
                    mainWindow.SetResolution(current);

                // THE GAUGE, AND THE SCROLLBACK WITH IT. Clearing the transcript is THIS front end's
                // answer to "the messages behind it are gone" — a log writer would draw a divider and
                // keep them, which is why the session announces the fact rather than the remedy.
                if (kind is Core.Sessions.SessionChangeKind.ContextCleared)
                {
                    mainWindow.SetContextUsed(0);

                    // THE SCROLLBACK GOES, and the session's own line arrives after it — see
                    // Session.ClearContext, which announces before it speaks precisely so a watcher
                    // whose reaction wipes the surface does not wipe the explanation with it. An
                    // empty screen with nothing said is worse than no clear at all.
                    mainWindow.Chat.Clear();
                }
            });

            // KEEP THE COUNT LIVE. Seeding once left "Always" grants invisible until something else
            // happened to redraw the panel. The grant can land on a scheduler thread, so the recount
            // is marshalled rather than run inline — RefreshSessionPanel touches controls.
            permissionRules.RulesChanged += () => system.EnqueueOnUIThread(() =>
                mainWindow.SetPermissionRuleCount(
                    permissionRules.RulesFor(session.WorkingDirectory).Rules.Count));

            // ASK THE OWNER, and know nothing about what it looks up. This used to switch on the
            // source name and reach into a resume store, a provider catalog and the session's own
            // instance to build each answer — the internals of three layers in the one place least
            // equipped to own any of them. It also discouraged the feature: adding a popup meant
            // editing this method, so /mode edits and /mcp never got one despite the mechanism being
            // right here.
            //
            // SESSION FIRST, THEN MANAGER. Each returns empty for a set it does not own, so neither
            // has to know what the other answers. Read on every keystroke, never cached: a session
            // that ended in another window a minute ago has to appear.
            commandMenu.Values = source =>
            {
                var values = session.Values(source);
                if (values.Count == 0)
                    values = manager.Values(source, session.WorkingDirectory);

                return [.. values.Select(v => new CommandArgument(v.Name, v.Summary))];
            };

            // NEVER RESUME SILENTLY. A context the user did not ask for is one they cannot account
            // for, and it is paid for on the very first turn — so this asks, on the first pump, for
            // the same reason everything else here is deferred: a dialog needs a render tick to join.
            // NOT WHEN THE COMMAND LINE ALREADY ANSWERED. `--resume` said which session to continue,
            // and pointing at the list afterwards would be the app answering a question nobody asked
            // twice over.
            if (!options.Resume.Wanted)
                StartupHint();
        });



        // THE ONE WAY BACK INTO A SESSION, shared by the startup offer and by /sessions resume.
        // Restoring is four steps that only work together — seed, re-wire, retire the old row, and
        // say so — and the second caller is exactly when a sequence like that gets copied with one
        // step quietly missing.

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
        //
        // NAMED FOR WHEN IT FIRES, and deliberately the same name as the Core half it calls. It read
        // "HintAtResume" — a hint TOWARD resuming, as though the subject were this session — when
        // what it reports is the OTHER sessions in this folder: how many there are, and whether one
        // died mid-conversation. That is also why the line goes to this front end's own surface
        // rather than being said by the session: a session announcing facts about its predecessors
        // would claim authorship of state that is not its own.
        void StartupHint()
        {
            var here = sessions.List(session.WorkingDirectory).Count;
            if (here == 0) return;

            // THE UNFINISHED ONE IS WORTH NAMING, because "ended without closing" is the case where
            // someone lost work and is looking for it. Everything else is just a count.
            var unfinished = sessions.LoadLatestUnfinished(session.WorkingDirectory);

            if (SessionHints.Startup(here, unfinished?.Context.Count) is { } line)
                transcript.Write(line);
        }

        int code = system.Run();

        // ENDED PROPERLY, so do not offer it back. Reaching this line is the only evidence available
        // that the process was not killed mid-session — which is precisely what makes an unfinished
        // row mean something.
        // ONLY IF THERE IS SOMETHING TO COME BACK TO. A session is written per turn, so one where
        // nothing was said was never stored — pointing at it would hand the user a command that
        // reports "no session matches" and makes resume look broken on its first use.
        var endedSessionId = session.HasSavedTurn ? session.SessionId : null;
        session.MarkFinished();
        // I1 #1: AgentHost.Dispose releases EVERY scheduler this session's host ever created (each
        // one's CancellationTokenSource + two SemaphoreSlims) — without this they leak for the rest of
        // the process's lifetime, since (as of review round 2's N2 fix) AgentHost no longer disposes
        // schedulers one-at-a-time as goals swap; this is now the only release point.
        // READ BEFORE THE CLOSE, because the ledger goes with the host. Everything the panel was
        // showing — spend, cache, what the children used — is available right up to this line and
        // gone immediately after it, which is why the terminal used to be blank on the way out.
        var spend = session is { Ledger: { } ledger } && ledger.TotalTokens > 0
            ? new SessionSpend(ledger.TotalTokens, ledger.InputTokens, ledger.OutputTokens,
                ledger.SubAgentTokens)
            {
                CacheHitRate = ledger.CacheHitRate,
                Cost = ledger.TotalCost,
            }
            : null;

        mainWindow.Dispose();   // stops the panel clock

        // THROUGH THE MANAGER, which disposes the host AND releases the turn's cancellation scope —
        // two steps that must happen together, and a bare host Dispose() here did only one of
        // them. It is also the last thing holding a host reference in this method.
        manager.Close(session);

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
        if (SessionHints.Farewell(endedSessionId, spend) is { } farewell)
            Console.WriteLine(farewell);

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
        Action<ResolvedConfig> wireRunner,
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
        var reResolved = ConfigResolver.Resolve(paths, env, useMock: false);
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
                        ctx.SetStatus($"{DisplayNumber.Grouped(rows)} records deleted", SharpConsoleUI.Core.NotificationSeverity.Success);
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
